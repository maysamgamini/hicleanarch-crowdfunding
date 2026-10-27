# The Campaigns service's place in the shared layer (lesson s5-l08): its security group and its rules to the
# database and Valkey, its own Valkey user (valkey.tf), the one-time hand-over of its schema, and where the
# gateway sends Campaigns' paths. The monolith keeps nothing of Campaigns since s5-l09. Everything else of the service (roles, task
# definitions, ECS service, log group) is its own OpenTofu root, infra/services/campaigns, applied by its
# own deploy role.

variable "campaigns_image" {
  description = "The Campaigns.Host image the one-time hand-over runs; empty until the release that gives Campaigns its own host (s5-l08). The service itself is deployed from infra/services/campaigns."
  type        = string
  default     = ""
}

variable "campaigns_serves_routes" {
  description = "Whether the gateway sends Campaigns' paths to the Campaigns service. Set it only after infra/services/campaigns has deployed the service (scripts/deploy-service.sh), so the routes always have a target; until then they go to the monolith, which since s5-l09 answers them 404."
  type        = bool
  default     = false
}

locals {
  campaigns_public_address = var.campaigns_serves_routes ? "http://campaigns:8080/" : "http://${aws_lb.internal.dns_name}/"
}

resource "aws_security_group" "campaigns" {
  name        = "${var.project}-sandbox-campaigns"
  description = "The Campaigns service: its endpoints from the gateway only"
  vpc_id      = aws_vpc.main.id
}

# 8080 (Campaigns' paths) from the gateway's tasks, in the gateway's security group, as Payments' and
# Notifications' public ports are. No other caller reaches Campaigns: nothing calls it, events reach it.
resource "aws_vpc_security_group_ingress_rule" "campaigns_from_gateway" {
  security_group_id            = aws_security_group.campaigns.id
  referenced_security_group_id = aws_security_group.gateway.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

# Out: SNS, SQS and the other AWS APIs, the database and Valkey.
resource "aws_vpc_security_group_egress_rule" "campaigns_out" {
  security_group_id = aws_security_group.campaigns.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "database_from_campaigns" {
  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = aws_security_group.campaigns.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

# The catalog cache and its refill locks, as the campaigns Valkey user (~campaigns:*).
resource "aws_vpc_security_group_ingress_rule" "valkey_from_campaigns" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.campaigns.id
  ip_protocol                  = "tcp"
  from_port                    = 6379
  to_port                      = 6379
}

locals {
  # The secrets the Campaigns service's container gets (infra/services/campaigns).
  campaigns_secret_arns = [
    aws_secretsmanager_secret.connection["campaigns"].arn,
    aws_secretsmanager_secret.read_connection["campaigns_read"].arn,
    aws_secretsmanager_secret.valkey["campaigns"].arn,
  ]
}

# Once, in the release that gives Campaigns its own host (s5-l08, scripts/hand-over.sh campaigns): the
# service's image with `hand-over`, as the cluster's owner, gives the campaigns schema and every object in it
# to campaigns_migrator. From then on the monolith's migrate step skips the schema.
resource "aws_iam_role" "campaigns_handover_execution" {
  name                 = "${var.project}-sandbox-handover-campaigns-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "campaigns_handover_execution" {
  role       = aws_iam_role.campaigns_handover_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "campaigns_handover_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_secretsmanager_secret.connection["campaigns"].arn,
      aws_secretsmanager_secret.migrator["campaigns"].arn,
      aws_secretsmanager_secret.connection["owner"].arn,
    ]
  }
}

resource "aws_iam_role_policy" "campaigns_handover_execution_secrets" {
  role   = aws_iam_role.campaigns_handover_execution.id
  policy = data.aws_iam_policy_document.campaigns_handover_secrets.json
}

resource "aws_ecs_task_definition" "campaigns_handover" {
  count                    = var.campaigns_image == "" ? 0 : 1
  family                   = "${var.project}-handover-campaigns"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.campaigns_handover_execution.arn
  container_definitions = jsonencode([{
    name      = "hand-over"
    image     = var.campaigns_image
    essential = true
    command   = ["hand-over"]
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
    ]
    secrets = [
      { name = "ConnectionStrings__Campaigns", valueFrom = aws_secretsmanager_secret.connection["campaigns"].arn },
      { name = "ConnectionStrings__Migrator", valueFrom = aws_secretsmanager_secret.migrator["campaigns"].arn },
      { name = "ConnectionStrings__Owner", valueFrom = aws_secretsmanager_secret.connection["owner"].arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "handover-campaigns"
      }
    }
  }])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}
