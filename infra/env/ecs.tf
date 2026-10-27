resource "aws_ecs_cluster" "main" {
  name = "${var.project}-sandbox"

  # Per-service task counts in CloudWatch, which the running-tasks alarms read (monitoring.tf).
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/sandbox/api"
  retention_in_days = 1
}

# The collector's built-in ECS configuration sends metrics to this group. OpenTofu creates it, so
# destroy removes it; the task role may write to it but not create groups of its own.
resource "aws_cloudwatch_log_group" "collector_metrics" {
  name              = "/aws/ecs/application/metrics"
  retention_in_days = 1
}

locals {
  # The running program gets only the connection strings of the modules it runs (Campaigns and Pledges
  # since s5-l04); the owner's goes to the migration task alone.
  module_secrets = [
    { name = "ConnectionStrings__Campaigns", valueFrom = aws_secretsmanager_secret.connection["campaigns"].arn },
    { name = "ConnectionStrings__Pledges", valueFrom = aws_secretsmanager_secret.connection["pledges"].arn },
    { name = "ConnectionStrings__PledgesJobs", valueFrom = aws_secretsmanager_secret.connection["pledges_jobs"].arn },
    { name = "ConnectionStrings__CampaignsRead", valueFrom = aws_secretsmanager_secret.read_connection["campaigns_read"].arn },
    { name = "ConnectionStrings__PledgesRead", valueFrom = aws_secretsmanager_secret.read_connection["pledges_read"].arn },
    { name = "ConnectionStrings__Valkey", valueFrom = aws_secretsmanager_secret.valkey["api"].arn },
  ]
  owner_secret = { name = "ConnectionStrings__Owner", valueFrom = aws_secretsmanager_secret.connection["owner"].arn }

  api_container = {
    name      = "api"
    image     = var.image
    essential = true
    portMappings = [{
      containerPort = 8080
      protocol      = "tcp"
    }]
    environment = concat([
      { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
      { name = "OTEL_SERVICE_NAME", value = "crowdfunding-api" },
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
      # Payments runs only in its own service (s4-l10): its command goes to its internal port through
      # Service Connect. The monolith serves nothing for other services, so it listens on 8080 alone.
      { name = "Modules__Payments__BaseAddress", value = "http://payments:8081" },
      # Lesson s5-l05: Pledges maps the creator's LIVE cancel only once check-creators passes.
      { name = "Pledges__FundingCancel__Open", value = tostring(var.funding_cancel_open) },
    ], local.broker_environment, local.service_identity_environment)
    # SIGTERM, then this many seconds to finish requests and give back job claims, then SIGKILL.
    stopTimeout = 30
    secrets     = concat(local.module_secrets, local.service_identity_secrets)
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "api"
      }
    }
  }

  # The AWS Distro for OpenTelemetry collector, with its built-in ECS configuration: it receives
  # OTLP on localhost:4317 and exports traces to X-Ray and metrics to CloudWatch.
  collector_container = {
    name      = "otel-collector"
    image     = var.collector_image
    essential = false
    command   = ["--config=/etc/ecs/ecs-default-config.yaml"]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "collector"
      }
    }
  }
}

# The service's task definition runs service_image, which moves to the new release only after
# `migrate` has run it (see aws_ecs_service.api).
resource "aws_ecs_task_definition" "api" {
  family                   = "${var.project}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions    = jsonencode([merge(local.api_container, { image = coalesce(var.service_image, var.image) }), local.collector_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# The release image, run once per deploy with the argument `migrate`. The sandbox workflow applies
# everything with the service still on its current image, runs this task, and only then applies
# again with service_image set to the release.
resource "aws_ecs_task_definition" "migrate" {
  family                   = "${var.project}-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions = jsonencode([merge(local.api_container, {
    name         = "migrate"
    command      = ["migrate"]
    portMappings = []
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
    ]
    secrets = concat(local.module_secrets, [local.owner_secret])
  })])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# Deploying is two full applies, never a targeted one: the first with service_image unchanged, so
# the service keeps its image while `migrate` runs; the second with service_image = image. A new
# environment has no image to keep, so the service is created only by the second apply.
resource "aws_ecs_service" "api" {
  count           = var.service_image == "" ? 0 : 1
  name            = local.api_service
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"
  # ECS moves tasks back into a zone that lost them, once it recovers.
  availability_zone_rebalancing = "ENABLED"

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
  }

  # A client of Service Connect: payments:8081 resolves to the Payments service's tasks.
  service_connect_configuration {
    enabled   = true
    namespace = aws_service_discovery_http_namespace.services.arn
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.api.arn
    container_name   = "api"
    container_port   = 8080
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # Autoscaling owns the task count once the service exists (autoscaling.tf).
  lifecycle {
    ignore_changes = [desired_count]
  }

  # The service waits for the internal listener the gateway sends it requests through.
  depends_on = [aws_lb_listener.internal]
}

moved {
  from = aws_ecs_service.api
  to   = aws_ecs_service.api[0]
}

locals {
  api_service = "api"
}
