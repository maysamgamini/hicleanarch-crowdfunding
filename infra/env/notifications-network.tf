# The Notifications service's place in the shared layer (Stage 5): its security group and its rule to
# the database, its migration role's secret (services.tf) and the one-time hand-over of its schema.
# Everything else of the service (roles, task definitions, ECS service, log group) is its own OpenTofu
# root, infra/services/notifications, applied by its own deploy role (lesson s5-l03). The monolith keeps
# nothing of it since s5-l04.

moved {
  from = aws_vpc_security_group_ingress_rule.notifications_from_tasks
  to   = aws_vpc_security_group_ingress_rule.notifications_from_gateway
}

variable "notifications_serves_preferences" {
  description = "Whether the gateway sends PUT /v1/me/notifications/{kind} to the Notifications service. Set it only after infra/services/notifications has deployed the service (scripts/deploy-service.sh), so the route always has a target; until then the gateway sends it to the monolith, which answers 404 (the catch-all: the Pledges service since s5-l10)."
  type        = bool
  default     = false
}

variable "notifications_image" {
  description = "The Notifications.Host image the one-time hand-over runs; empty until the release that gives Notifications its own host (s5-l03). The service itself is deployed from infra/services/notifications."
  type        = string
  default     = ""
}

locals {
  notifications_public_address = var.notifications_serves_preferences ? "http://notifications:8080/" : local.pledges_public_address
}

resource "aws_security_group" "notifications" {
  name        = "${var.project}-sandbox-notifications"
  description = "The Notifications service: its endpoint from the gateway only"
  vpc_id      = aws_vpc.main.id
}

# 8080 (PUT /v1/me/notifications/{kind}) from the gateway's tasks, in the gateway's security group, as
# Payments' public port is (payments-network.tf). No other caller reaches Notifications.
resource "aws_vpc_security_group_ingress_rule" "notifications_from_gateway" {
  security_group_id            = aws_security_group.notifications.id
  referenced_security_group_id = aws_security_group.gateway.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

# Out: SES, Cognito, SQS and the other AWS APIs, and the database.
resource "aws_vpc_security_group_egress_rule" "notifications_out" {
  security_group_id = aws_security_group.notifications.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "database_from_notifications" {
  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = aws_security_group.notifications.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

locals {
  # The secrets the Notifications service's container gets (infra/services/notifications).
  notifications_secret_arns = [aws_secretsmanager_secret.connection["notifications"].arn]

  handover_task_definitions = {
    for name, task in {
      notifications = aws_ecs_task_definition.notifications_handover
      campaigns     = aws_ecs_task_definition.campaigns_handover
      pledges       = aws_ecs_task_definition.pledges_handover
    } : name => one(task[*].arn) if length(task) > 0
  }
}

# Once, in the release that gives Notifications its own host (s5-l03, scripts/hand-over.sh notifications): the
# service's image with `hand-over`, logged in as the cluster's owner, prepares the schema as the
# monolith's migrate step does and gives it, with every object in it, to the service's migration role.
# From then on the monolith's migrate step skips the schema. It belongs to the shared layer because it
# alone reads the owner's secret, which no service's deploy role may hand to a role of its own.
resource "aws_iam_role" "notifications_handover_execution" {
  name                 = "${var.project}-sandbox-handover-notifications-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "notifications_handover_execution" {
  role       = aws_iam_role.notifications_handover_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "notifications_handover_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = concat(local.notifications_secret_arns, [
      aws_secretsmanager_secret.migrator["notifications"].arn, aws_secretsmanager_secret.connection["owner"].arn,
    ])
  }
}

resource "aws_iam_role_policy" "notifications_handover_execution_secrets" {
  role   = aws_iam_role.notifications_handover_execution.id
  policy = data.aws_iam_policy_document.notifications_handover_secrets.json
}

resource "aws_ecs_task_definition" "notifications_handover" {
  count                    = var.notifications_image == "" ? 0 : 1
  family                   = "${var.project}-handover-notifications"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.notifications_handover_execution.arn
  container_definitions = jsonencode([{
    name      = "hand-over"
    image     = var.notifications_image
    essential = true
    command   = ["hand-over"]
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
    ]
    secrets = [
      { name = "ConnectionStrings__Notifications", valueFrom = aws_secretsmanager_secret.connection["notifications"].arn },
      { name = "ConnectionStrings__Migrator", valueFrom = aws_secretsmanager_secret.migrator["notifications"].arn },
      { name = "ConnectionStrings__Owner", valueFrom = aws_secretsmanager_secret.connection["owner"].arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "handover-notifications"
      }
    }
  }])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

output "handover_task_definitions" {
  description = "The one-time hand-over task of each service whose image is set (scripts/hand-over.sh)."
  value       = local.handover_task_definitions
}
