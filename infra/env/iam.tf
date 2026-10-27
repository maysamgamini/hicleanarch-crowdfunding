data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Used by ECS itself for the gateway's tasks: pull the image, write logs, read the gateway's Valkey user.
# It carries the bootstrap layer's boundary: the deploy role may create it only with it.
resource "aws_iam_role" "execution" {
  name                 = "${var.project}-sandbox-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

# The execution role starts the gateway's tasks (gateway.tf): it reads the gateway's Valkey user and nothing
# else. Every service reads its own secrets through its own roles (infra/services/*); since s5-l10 no
# program of this layer reads a module's connection string.
data "aws_iam_policy_document" "read_connection_strings" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.valkey["gateway"].arn]
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.read_connection_strings.json
}

data "aws_caller_identity" "current" {}

locals {
  task_boundary = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/${var.project}-task-boundary"
}
