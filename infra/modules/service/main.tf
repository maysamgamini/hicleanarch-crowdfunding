# One service on the shared cluster (lesson s5-l01): its roles under the task boundary, its log group,
# its task definitions (the service, and its migrate step) and its ECS service with Service Connect and
# the deployment circuit breaker. Every name starts with <project>-sandbox-<name>, the only names the
# service's deploy role may touch (infra/bootstrap). Its network place (security group, database and
# Valkey rules) and its one-time schema hand-over stay with the shared layer, which owns the network and
# the database owner's secret.

data "aws_caller_identity" "current" {}

locals {
  prefix        = "${var.project}-sandbox-${var.name}"
  task_boundary = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/${var.project}-task-boundary"
}

data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Used by ECS to start the service's tasks: pull the image, write logs, read the service's own secrets.
resource "aws_iam_role" "execution" {
  name                 = "${local.prefix}-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

# This release's secrets, and the previous release's (kept_secret_arns, CQA-2124). The first apply of a
# deploy keeps the task definition the service runs (CQA-2103), which names the previous release's
# secrets: ECS reads them again for every task it starts or replaces from it, and for a rollback to it.
# A secret this release drops leaves the role one release later, once no revision that may run needs it.
data "aws_iam_policy_document" "secrets" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = distinct(concat([for s in var.secrets : s.valueFrom], var.kept_secret_arns))
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.secrets.json
}

# Used by ECS to start the migrate task only: the service's secrets and its migration secrets, which the
# service's own execution role never sees.
resource "aws_iam_role" "migrate_execution" {
  name                 = "${local.prefix}-migrate-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "migrate_execution" {
  role       = aws_iam_role.migrate_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "migrate_secrets" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [for s in concat(var.secrets, var.migrate_secrets) : s.valueFrom]
  }
}

resource "aws_iam_role_policy" "migrate_execution_secrets" {
  role   = aws_iam_role.migrate_execution.id
  policy = data.aws_iam_policy_document.migrate_secrets.json
}

# Used by the running containers: telemetry, its own queue, its own topic, and what the root adds.
resource "aws_iam_role" "task" {
  name                 = "${local.prefix}-task"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

data "aws_iam_policy_document" "task" {
  source_policy_documents = var.task_policy_json == "" ? [] : [var.task_policy_json]

  statement {
    sid = "Telemetry"
    actions = [
      "xray:PutTraceSegments", "xray:PutTelemetryRecords", "xray:GetSamplingRules", "xray:GetSamplingTargets",
      "cloudwatch:PutMetricData", "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams",
    ]
    resources = ["*"]
  }

  dynamic "statement" {
    for_each = var.queue_arn == null ? [] : [var.queue_arn]
    content {
      sid = "ReadItsQueue"
      # What the SQS reader calls; nothing in the services asks a queue's attributes (CQA-2105).
      actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility"]
      resources = [statement.value]
    }
  }

  dynamic "statement" {
    for_each = var.topic_arn == null ? [] : [var.topic_arn]
    content {
      sid       = "PublishItsEvents"
      actions   = ["sns:Publish"]
      resources = [statement.value]
    }
  }

  dynamic "statement" {
    # Each producer checks its own topic's subscriptions (s4-l10): no other topic is named.
    for_each = var.topic_arn == null ? [] : [var.topic_arn]
    content {
      sid       = "CheckItsSubscriptions"
      actions   = ["sns:ListSubscriptionsByTopic"]
      resources = [statement.value]
    }
  }

  dynamic "statement" {
    # A subscription's ARN is its topic's plus an id.
    for_each = var.topic_arn == null ? [] : [var.topic_arn]
    content {
      sid       = "ReadItsSubscriptionsFilters"
      actions   = ["sns:GetSubscriptionAttributes"]
      resources = ["${statement.value}:*"]
    }
  }
}

resource "aws_iam_role_policy" "task" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task.json
}

# Under /<project>/sandbox/, where the deploy roles' log rights reach.
resource "aws_cloudwatch_log_group" "service" {
  name              = "/${var.project}/sandbox/${var.name}"
  retention_in_days = 1
}

locals {
  # The image the service runs, and its tag (<repository>:<tag>).
  service_runs    = coalesce(var.service_image, var.image)
  service_version = reverse(split(":", local.service_runs))[0]

  logs = {
    logDriver = "awslogs"
    options = {
      awslogs-group         = aws_cloudwatch_log_group.service.name
      awslogs-region        = var.region
      awslogs-stream-prefix = var.name
    }
  }

  ports = concat(
    [{ name = "public", containerPort = 8080, protocol = "tcp", appProtocol = "http" }],
    var.internal_port == null ? [] : [{ name = "internal", containerPort = 8081, protocol = "tcp", appProtocol = "http" }],
  )

  container = {
    name      = var.name
    image     = var.image
    essential = true
    # Named ports: Service Connect publishes them as <name>:8080 (and <name>:8081).
    portMappings = local.ports
    environment = concat([
      { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
      { name = "OTEL_SERVICE_NAME", value = "crowdfunding-${var.name}" },
      # What GET /version answers (lesson s5-l14): the release's image tag, the commit it was built from.
      { name = "SERVICE_VERSION", value = local.service_version },
      { name = "Auth__Authority", value = var.shared.cognito_issuer },
      { name = "Auth__ClientId", value = var.shared.user_pool_client_id },
      { name = "ForwardedHeaders__KnownNetwork", value = var.shared.vpc_cidr },
    ], var.environment)
    secrets     = var.secrets
    stopTimeout = 30
    # No load balancer asks a service whether it is ready, so the container does (CQA-1518): the
    # program's own probe calls /health/ready on port 8080 (the image has no curl). Three failures 30 s
    # apart mark the task unhealthy; the 60 s start period covers start-up and migrations' locks. A
    # deployment whose tasks never become healthy is rolled back by the circuit breaker, and Service
    # Connect stops sending to an unhealthy task.
    healthCheck = {
      command     = ["CMD", "dotnet", var.assembly, "health"]
      interval    = 30
      timeout     = 5
      retries     = 3
      startPeriod = 60
    }
    logConfiguration = local.logs
  }

  collector = {
    name             = "otel-collector"
    image            = var.shared.collector_image
    essential        = false
    command          = ["--config=/etc/ecs/ecs-default-config.yaml"]
    logConfiguration = merge(local.logs, { options = merge(local.logs.options, { awslogs-stream-prefix = "collector" }) })
  }
}

# Task definitions are never deregistered by OpenTofu (skip_destroy): ECS cannot limit deregistering to
# a family, so no deploy role is given it. A registered revision that nothing runs costs nothing.
resource "aws_ecs_task_definition" "service" {
  family                   = "${var.project}-${var.name}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  skip_destroy             = true
  container_definitions    = jsonencode([merge(local.container, { image = local.service_runs }), local.collector])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# The release image, run once per deploy with `migrate`, before any task runs the release.
resource "aws_ecs_task_definition" "migrate" {
  family                   = "${var.project}-${var.name}-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.migrate_execution.arn
  task_role_arn            = aws_iam_role.task.arn
  skip_destroy             = true
  container_definitions = jsonencode([merge(local.container, {
    name         = "migrate"
    command      = ["migrate"]
    portMappings = []
    healthCheck  = null
    secrets      = concat(var.secrets, var.migrate_secrets)
  })])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# Two full applies per deploy: the first keeps service_image while the migration runs, the second moves
# the service to the release. A new environment gets the service on the second.
#
# CQA-2103: the first apply keeps the service on the task definition it runs, not only on its image. A
# release that removes a setting the running image still reads would otherwise pair the old image with
# the new settings, and every task ECS replaced before the second apply would fail to start. ECS keeps
# running and replacing tasks of the revision it runs. The roles are not versioned: a release narrows a
# role only once no image that needs the right still runs. For the execution role's secrets the deploy
# scripts do that (kept_secret_arns, CQA-2124).
resource "aws_ecs_service" "service" {
  count           = var.service_image == "" ? 0 : 1
  name            = var.name
  cluster         = var.shared.cluster_arn
  task_definition = coalesce(var.service_task_definition, aws_ecs_task_definition.service.arn)
  desired_count   = var.desired_count
  launch_type     = "FARGATE"
  # ECS moves tasks back into a zone that lost them, once it recovers.
  availability_zone_rebalancing = "ENABLED"

  network_configuration {
    subnets          = var.shared.task_subnet_ids
    security_groups  = [var.security_group_id]
    assign_public_ip = true
  }

  # A server in the namespace: callers reach <name>:8080, and <name>:8081 when it has an internal port.
  service_connect_configuration {
    enabled   = true
    namespace = var.shared.service_connect_namespace_arn

    service {
      port_name      = "public"
      discovery_name = "${var.name}-public"
      client_alias {
        dns_name = var.name
        port     = 8080
      }
    }

    dynamic "service" {
      for_each = var.internal_port == null ? [] : [var.internal_port]
      content {
        port_name      = "internal"
        discovery_name = "${var.name}-internal"
        client_alias {
          dns_name = var.name
          port     = 8081
        }
        timeout {
          per_request_timeout_seconds = service.value.per_request_timeout_seconds
        }
      }
    }

    log_configuration {
      log_driver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.service.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "service-connect"
      }
    }
  }

  # Both detectors roll back (lesson s5-l14): the circuit breaker, for tasks that fail to start or fail
  # their health checks, and the deployment alarms (deployment.tf), for tasks that are healthy but
  # answer 5xx or fail the events they take.
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  alarms {
    alarm_names = local.deployment_alarm_names
    enable      = true
    rollback    = true
  }
}

# Fewer tasks than the service should run: tasks are crashing or cannot start.
resource "aws_cloudwatch_metric_alarm" "running_tasks" {
  count               = var.service_image == "" ? 0 : 1
  alarm_name          = "${local.prefix}_running_tasks"
  alarm_description   = "The ${var.name} service runs fewer tasks than its ${var.desired_count}: tasks are crashing or cannot start."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = var.shared.cluster_name, ServiceName = var.name }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 3
  comparison_operator = "LessThanThreshold"
  threshold           = var.desired_count
  treat_missing_data  = "breaching"
  alarm_actions       = [var.shared.alarm_topic_arn]
  ok_actions          = [var.shared.alarm_topic_arn]
}
