# The Payments service (Stage 4): its namespace, security group, roles and task definition. The ECS
# service, its Service Connect settings and the gateway's routes are in payments-split.tf (s4-l05).
# Until s4-l10 the monolith keeps Payments' secrets and rights for its InProcess fallback; from then on
# its roles lose everything of Payments.

variable "payments_image" {
  description = "The Payments.Host image; empty until Stage 4 builds it."
  type        = string
  default     = ""
}

# Service Connect: services find each other by name (payments:8080, payments:8081) inside this namespace.
resource "aws_service_discovery_http_namespace" "services" {
  name        = "${var.project}-sandbox"
  description = "Service Connect names for the monolith, the gateway and Payments."
}

resource "aws_security_group" "payments" {
  name   = "${var.project}-sandbox-payments"
  vpc_id = aws_vpc.main.id
}

# 8080 (public endpoints) from the gateway only; 8081 (the internal command) from the monolith only.
resource "aws_vpc_security_group_ingress_rule" "payments_public_from_gateway" {
  security_group_id            = aws_security_group.payments.id
  referenced_security_group_id = aws_security_group.gateway.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

resource "aws_vpc_security_group_ingress_rule" "payments_internal_from_monolith" {
  security_group_id            = aws_security_group.payments.id
  referenced_security_group_id = aws_security_group.tasks.id
  ip_protocol                  = "tcp"
  from_port                    = 8081
  to_port                      = 8081
}

# Out: the PSP, AWS APIs, the database and Valkey.
resource "aws_vpc_security_group_egress_rule" "payments_out" {
  security_group_id = aws_security_group.payments.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "database_from_payments" {
  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = aws_security_group.payments.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_vpc_security_group_ingress_rule" "valkey_from_payments" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.payments.id
  ip_protocol                  = "tcp"
  from_port                    = 6379
  to_port                      = 6379
}

# Payments' own roles: it reads only its own secrets, its own queue and publishes to its own topic.
resource "aws_iam_role" "payments_execution" {
  name                 = "${var.project}-sandbox-payments-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "payments_execution" {
  role       = aws_iam_role.payments_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "payments_secrets" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.connection["payments"].arn, aws_secretsmanager_secret.webhook.arn, aws_secretsmanager_secret.valkey["payments"].arn]
  }
}

resource "aws_iam_role_policy" "payments_execution_secrets" {
  role   = aws_iam_role.payments_execution.id
  policy = data.aws_iam_policy_document.payments_secrets.json
}

resource "aws_iam_role" "payments_task" {
  name                 = "${var.project}-sandbox-payments-task"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy" "payments_task_telemetry" {
  role   = aws_iam_role.payments_task.id
  policy = data.aws_iam_policy_document.telemetry.json
}

data "aws_iam_policy_document" "payments_messaging" {
  statement {
    sid       = "ReadItsQueue"
    actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"]
    resources = [aws_sqs_queue.events["payments"].arn]
  }
  statement {
    sid       = "PublishItsEvents"
    actions   = ["sns:Publish"]
    resources = [aws_sns_topic.events["payments"].arn]
  }
  statement {
    sid       = "CheckItsSubscriptions"
    actions   = ["sns:ListSubscriptionsByTopic"]
    resources = [for t in aws_sns_topic.events : t.arn]
  }
  statement {
    # A subscription's ARN is its topic's plus an id.
    sid       = "ReadItsSubscriptionsFilters"
    actions   = ["sns:GetSubscriptionAttributes"]
    resources = [for t in aws_sns_topic.events : "${t.arn}:*"]
  }
}

resource "aws_iam_role_policy" "payments_task_messaging" {
  role   = aws_iam_role.payments_task.id
  policy = data.aws_iam_policy_document.payments_messaging.json
}

resource "aws_cloudwatch_log_group" "payments" {
  name              = "/ecs/${var.project}-sandbox-payments"
  retention_in_days = 7
}

locals {
  # Payments.Host: the consumers of its events run elsewhere, so every route of its events is Broker;
  # it reads the payments queue and checks the subscriptions of the topics it reads from.
  payments_broker_environment = concat(
    [
      { name = "Events__Routes__Pledges", value = "Broker" },
      { name = "Events__Routes__Campaigns", value = "Broker" },
      { name = "Events__Routes__Notifications", value = "Broker" },
      { name = "Events__Broker__Region", value = var.region },
      { name = "Events__Broker__Queues__payments__Arn", value = aws_sqs_queue.events["payments"].arn },
      { name = "Events__Broker__Queues__payments__Url", value = aws_sqs_queue.events["payments"].id },
      { name = "Events__Broker__Read__0", value = "payments" },
    ],
    [for t, topic in aws_sns_topic.events : { name = "Events__Broker__Topics__${t}", value = topic.arn }],
  )
}

resource "aws_ecs_task_definition" "payments" {
  count                    = var.payments_image == "" ? 0 : 1
  family                   = "${var.project}-payments"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.payments_execution.arn
  task_role_arn            = aws_iam_role.payments_task.arn
  container_definitions = jsonencode([
    {
      name      = "payments"
      image     = var.payments_image
      essential = true
      # Named ports: Service Connect publishes them as payments:8080 and payments:8081.
      portMappings = [
        { name = "public", containerPort = 8080, protocol = "tcp", appProtocol = "http" },
        { name = "internal", containerPort = 8081, protocol = "tcp", appProtocol = "http" },
      ]
      environment = concat([
        { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
        { name = "OTEL_SERVICE_NAME", value = "crowdfunding-payments" },
        { name = "Auth__Authority", value = local.cognito_issuer },
        { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
        { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
        { name = "Payments__Budget__Prefix", value = local.psp_budget_prefix },
        # Only the monolith's service identity may call the internal endpoint (s4-l04).
        { name = "Auth__ServiceClients__0", value = aws_cognito_user_pool_client.monolith_service.id },
      ], local.payments_broker_environment)
      secrets = [
        { name = "ConnectionStrings__Payments", valueFrom = aws_secretsmanager_secret.connection["payments"].arn },
        { name = "Payments__WebhookSecret", valueFrom = aws_secretsmanager_secret.webhook.arn },
        { name = "ConnectionStrings__Valkey", valueFrom = aws_secretsmanager_secret.valkey["payments"].arn },
      ]
      stopTimeout = 30
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.payments.name
          awslogs-region        = var.region
          awslogs-stream-prefix = "payments"
        }
      }
    },
    merge(local.collector_container, {
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.payments.name
          awslogs-region        = var.region
          awslogs-stream-prefix = "collector"
        }
      }
    }),
  ])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}
