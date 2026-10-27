resource "aws_ecs_cluster" "main" {
  name = "${var.project}-sandbox"
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/sandbox/api"
  retention_in_days = 1
}

# The collector's built-in ECS configuration sends metrics to this group. Terraform creates it, so
# destroy removes it; the task role may write to it but not create groups of its own.
resource "aws_cloudwatch_log_group" "collector_metrics" {
  name              = "/aws/ecs/application/metrics"
  retention_in_days = 1
}

locals {
  # The running program gets only the modules' connection strings; the owner's goes to the
  # migration task alone.
  module_secrets = [
    { name = "ConnectionStrings__Campaigns", valueFrom = aws_secretsmanager_secret.connection["campaigns"].arn },
    { name = "ConnectionStrings__Pledges", valueFrom = aws_secretsmanager_secret.connection["pledges"].arn },
    { name = "ConnectionStrings__PledgesJobs", valueFrom = aws_secretsmanager_secret.connection["pledges_jobs"].arn },
    { name = "ConnectionStrings__Payments", valueFrom = aws_secretsmanager_secret.connection["payments"].arn },
    { name = "Payments__WebhookSecret", valueFrom = aws_secretsmanager_secret.webhook.arn },
    { name = "ConnectionStrings__Notifications", valueFrom = aws_secretsmanager_secret.connection["notifications"].arn },
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
    environment = [
      { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
      { name = "OTEL_SERVICE_NAME", value = "crowdfunding-api" },
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
      { name = "Notifications__Sender", value = var.notifications_from == "" ? "InMemory" : "Ses" },
      { name = "Notifications__FromAddress", value = var.notifications_from },
      { name = "Notifications__UserPoolId", value = aws_cognito_user_pool.users.id },
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
    ]
    # SIGTERM, then this many seconds to finish requests and give back job claims, then SIGKILL.
    stopTimeout = 30
    secrets     = local.module_secrets
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

resource "aws_ecs_task_definition" "api" {
  family                   = "${var.project}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions    = jsonencode([local.api_container, local.collector_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# The same image, run once per deploy with the argument `migrate`. The sandbox workflow creates this
# task first, runs it, and only then lets the service take the new image.
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

resource "aws_ecs_service" "api" {
  name            = "api"
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

  # The workflow applies everything else, including this service's new image, only after `migrate`;
  # the service waits for the internal listener the gateway sends it requests through.
  depends_on = [aws_lb_listener.internal]
}
