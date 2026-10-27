# The Pledges service's place in the shared layer (lesson s5-l10): its security group and its rules to the
# database, Valkey and Payments' internal port, and the one-time hand-over of its schema. Everything else of
# the service (roles, task definitions, ECS service, log group, scaling) is its own OpenTofu root,
# infra/services/pledges, applied by its own deploy role. It is the old monolith's host, running Pledges
# alone; the gateway's catch-all sends it every path no other service serves.

variable "pledges_image" {
  description = "The Pledges.Host image the one-time hand-over runs; empty until the release that gives Pledges its own service (s5-l10). The service itself is deployed from infra/services/pledges."
  type        = string
  default     = ""
}

locals {
  pledges_public_address = "http://pledges:8080/"
}

resource "aws_security_group" "pledges" {
  name        = "${var.project}-sandbox-pledges"
  description = "The Pledges service: its endpoints from the gateway only"
  vpc_id      = aws_vpc.main.id
}

# 8080 from the gateway's tasks: the catch-all, and nothing else reaches it.
resource "aws_vpc_security_group_ingress_rule" "pledges_from_gateway" {
  security_group_id            = aws_security_group.pledges.id
  referenced_security_group_id = aws_security_group.gateway.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

# Out: Payments' internal port, SNS, SQS, Cognito's token endpoint and the other AWS APIs, the database, Valkey.
resource "aws_vpc_security_group_egress_rule" "pledges_out" {
  security_group_id = aws_security_group.pledges.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "database_from_pledges" {
  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = aws_security_group.pledges.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

# The display totals and the sold-out flags, as the pledges Valkey user (~pledges:*).
resource "aws_vpc_security_group_ingress_rule" "valkey_from_pledges" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.pledges.id
  ip_protocol                  = "tcp"
  from_port                    = 6379
  to_port                      = 6379
}

# Once, in the release that gives Pledges its own service (s5-l10, scripts/hand-over.sh pledges): the
# service's image with `hand-over`, as the cluster's owner, gives the pledges schema and every object in it
# to pledges_migrator. From then on nothing migrates as the owner: every schema has its service's role.
resource "aws_iam_role" "pledges_handover_execution" {
  name                 = "${var.project}-sandbox-handover-pledges-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "pledges_handover_execution" {
  role       = aws_iam_role.pledges_handover_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "pledges_handover_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_secretsmanager_secret.connection["pledges"].arn,
      aws_secretsmanager_secret.connection["pledges_jobs"].arn,
      aws_secretsmanager_secret.migrator["pledges"].arn,
      aws_secretsmanager_secret.connection["owner"].arn,
    ]
  }
}

resource "aws_iam_role_policy" "pledges_handover_execution_secrets" {
  role   = aws_iam_role.pledges_handover_execution.id
  policy = data.aws_iam_policy_document.pledges_handover_secrets.json
}

resource "aws_ecs_task_definition" "pledges_handover" {
  count                    = var.pledges_image == "" ? 0 : 1
  family                   = "${var.project}-handover-pledges"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.pledges_handover_execution.arn
  container_definitions = jsonencode([{
    name      = "hand-over"
    image     = var.pledges_image
    essential = true
    command   = ["hand-over"]
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
    ]
    secrets = [
      { name = "ConnectionStrings__Pledges", valueFrom = aws_secretsmanager_secret.connection["pledges"].arn },
      { name = "ConnectionStrings__PledgesJobs", valueFrom = aws_secretsmanager_secret.connection["pledges_jobs"].arn },
      { name = "ConnectionStrings__Migrator", valueFrom = aws_secretsmanager_secret.migrator["pledges"].arn },
      { name = "ConnectionStrings__Owner", valueFrom = aws_secretsmanager_secret.connection["owner"].arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "handover-pledges"
      }
    }
  }])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}
