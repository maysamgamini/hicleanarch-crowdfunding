resource "aws_cloudwatch_log_group" "payments" {
  name              = "/${var.project}/sandbox/payments"
  retention_in_days = 1
}

locals {
  # The consumers of Payments' events run elsewhere, so every route of its events is Broker; it reads
  # the payments queue, publishes to its own topic, and checks that topic's subscription to the
  # monolith's queue, which it knows by ARN only (s4-l10).
  broker_environment = concat(
    [
      { name = "Events__Routes__Pledges", value = "Broker" },
      { name = "Events__Routes__Campaigns", value = "Broker" },
      { name = "Events__Routes__Notifications", value = "Broker" },
      { name = "Events__Broker__Region", value = var.region },
      { name = "Events__Broker__Queues__payments__Arn", value = local.shared.payments_queue_arn },
      { name = "Events__Broker__Queues__payments__Url", value = local.shared.payments_queue_url },
      { name = "Events__Broker__Queues__monolith__Arn", value = local.shared.monolith_queue_arn },
      { name = "Events__Broker__Read__0", value = "payments" },
      { name = "Events__Broker__Topics__payments", value = local.shared.payments_topic_arn },
    ]
  )

  payments_secrets = [
    { name = "ConnectionStrings__Payments", valueFrom = local.shared.payments_connection_secret_arn },
    { name = "Payments__WebhookSecret", valueFrom = local.shared.payments_webhook_secret_arn },
    { name = "ConnectionStrings__Valkey", valueFrom = local.shared.payments_valkey_secret_arn },
  ]

  payments_container = {
    name      = "payments"
    image     = var.image
    essential = true
    # Named ports: Service Connect publishes them as payments:8080 and payments:8081.
    portMappings = [
      { name = "public", containerPort = 8080, protocol = "tcp", appProtocol = "http" },
      { name = "internal", containerPort = 8081, protocol = "tcp", appProtocol = "http" },
    ]
    environment = concat([
      { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
      { name = "OTEL_SERVICE_NAME", value = "crowdfunding-payments" },
      { name = "Auth__Authority", value = local.shared.cognito_issuer },
      { name = "Auth__ClientId", value = local.shared.user_pool_client_id },
      { name = "ForwardedHeaders__KnownNetwork", value = local.shared.vpc_cidr },
      { name = "Payments__Budget__Prefix", value = local.shared.psp_budget_prefix },
      # Only the monolith's service identity may call the internal endpoint (s4-l04).
      { name = "Auth__ServiceClients__0", value = local.shared.monolith_service_client_id },
    ], local.broker_environment)
    secrets     = local.payments_secrets
    stopTimeout = 30
    # No load balancer asks Payments whether it is ready, so the container does (CQA-1518): the program's
    # own probe calls /health/ready on port 8080 (the image has no curl). Three failures 30 s apart mark
    # the task unhealthy; the 60 s start period covers start-up and migrations' locks. A deployment whose
    # tasks never become healthy is rolled back by the circuit breaker, and Service Connect stops
    # sending to an unhealthy task.
    healthCheck = {
      command     = ["CMD", "dotnet", "CrowdFunding.Payments.Host.dll", "health"]
      interval    = 30
      timeout     = 5
      retries     = 3
      startPeriod = 60
    }
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.payments.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "payments"
      }
    }
  }

  collector_container = {
    name      = "otel-collector"
    image     = local.shared.collector_image
    essential = false
    command   = ["--config=/etc/ecs/ecs-default-config.yaml"]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.payments.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "collector"
      }
    }
  }
}

# Task definitions are never deregistered by OpenTofu (skip_destroy): deregistering is the one ECS
# action IAM cannot limit to a family, and allowing it would let Payments' deploy role retire the
# monolith's. A registered revision that nothing runs costs nothing.
resource "aws_ecs_task_definition" "payments" {
  family                   = "${var.project}-payments"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  skip_destroy             = true
  container_definitions    = jsonencode([merge(local.payments_container, { image = coalesce(var.service_image, var.image) }), local.collector_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# The release image, run once per deploy with the argument `migrate`: Payments.Host prepares Payments'
# schema and login role only, as the database owner, before any task of the service runs the release.
resource "aws_ecs_task_definition" "migrate" {
  family                   = "${var.project}-payments-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.migrate_execution.arn
  task_role_arn            = aws_iam_role.task.arn
  skip_destroy             = true
  container_definitions = jsonencode([merge(local.payments_container, {
    name         = "migrate"
    command      = ["migrate"]
    portMappings = []
    secrets = concat(local.payments_secrets, [
      { name = "ConnectionStrings__Owner", valueFrom = local.shared.payments_database_owner_secret_arn },
    ])
  })])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# Two full applies per deploy, as for the monolith: the first keeps service_image while the migration
# runs, the second moves the service to the release. A new environment gets the service on the second.
resource "aws_ecs_service" "payments" {
  count           = var.service_image == "" ? 0 : 1
  name            = local.service_name
  cluster         = local.shared.cluster_arn
  task_definition = aws_ecs_task_definition.payments.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"
  # ECS moves tasks back into a zone that lost them, once it recovers.
  availability_zone_rebalancing = "ENABLED"

  network_configuration {
    subnets          = local.shared.task_subnet_ids
    security_groups  = [local.shared.payments_security_group_id]
    assign_public_ip = true
  }

  # Payments is a server in the namespace: the gateway reaches payments:8080, the monolith payments:8081.
  service_connect_configuration {
    enabled   = true
    namespace = local.shared.service_connect_namespace_arn

    service {
      port_name      = "public"
      discovery_name = "payments-public"
      client_alias {
        dns_name = "payments"
        port     = 8080
      }
    }

    # The proxy in each client task waits at most 2 s for a reply on 8081, the remote client's attempt
    # (lesson s4-a04's time budget; the default is 15 s). It retries a failed connection twice on
    # another task: AWS manages that and it cannot be turned off; the card setup is idempotent per pledge.
    service {
      port_name      = "internal"
      discovery_name = "payments-internal"
      client_alias {
        dns_name = "payments"
        port     = 8081
      }
      timeout {
        per_request_timeout_seconds = 2
      }
    }

    log_configuration {
      log_driver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.payments.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "service-connect"
      }
    }
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }
}

locals {
  service_name = "payments"
}

# Fewer Payments tasks than it should run: tasks are crashing or cannot start.
resource "aws_cloudwatch_metric_alarm" "running_tasks" {
  count               = var.service_image == "" ? 0 : 1
  alarm_name          = "${var.project}-sandbox-payments_running_tasks"
  alarm_description   = "The payments service runs fewer tasks than its ${var.desired_count}: tasks are crashing or cannot start."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = local.shared.cluster_name, ServiceName = local.service_name }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 3
  comparison_operator = "LessThanThreshold"
  threshold           = var.desired_count
  treat_missing_data  = "breaching"
  alarm_actions       = [local.shared.alarm_topic_arn]
  ok_actions          = [local.shared.alarm_topic_arn]
}
