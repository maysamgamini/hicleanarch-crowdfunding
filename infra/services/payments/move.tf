# Lesson s4-l07's move-payments task, run once per step by scripts/move-payments.sh with the step as a
# command override: Payments' image in Payments' security group. It reaches the shared cluster as its
# owner (to fence and prune), the pause flag in Valkey with Payments' Valkey user, and the clone once it
# exists. Its own execution role reads those secrets, so Payments' service never holds the owner's.
resource "aws_iam_role" "move_execution" {
  name                 = "${var.project}-sandbox-payments-move-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "move_execution" {
  role       = aws_iam_role.move_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

locals {
  clone_secrets = local.shared.payments_own_secret_arn == "" ? [] : [
    { name = "ConnectionStrings__PaymentsOwnOwner", valueFrom = local.shared.payments_own_owner_secret_arn },
    { name = "ConnectionStrings__PaymentsOwn", valueFrom = local.shared.payments_own_secret_arn },
  ]
}

data "aws_iam_policy_document" "move_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = concat(
      [local.shared.shared_owner_secret_arn, local.shared.shared_payments_secret_arn,
      local.shared.payments_webhook_secret_arn, local.shared.payments_valkey_secret_arn],
      [for s in local.clone_secrets : s.valueFrom],
    )
  }
}

resource "aws_iam_role_policy" "move_execution_secrets" {
  role   = aws_iam_role.move_execution.id
  policy = data.aws_iam_policy_document.move_secrets.json
}

resource "aws_ecs_task_definition" "move_payments" {
  family                   = "${var.project}-payments-move"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.move_execution.arn
  task_role_arn            = aws_iam_role.task.arn
  skip_destroy             = true
  container_definitions = jsonencode([{
    name      = "move-payments"
    image     = var.image
    essential = true
    command   = ["move-payments"]
    environment = [
      { name = "Auth__Authority", value = local.shared.cognito_issuer },
      { name = "Auth__ClientId", value = local.shared.user_pool_client_id },
    ]
    secrets = concat([
      { name = "ConnectionStrings__Owner", valueFrom = local.shared.shared_owner_secret_arn },
      { name = "ConnectionStrings__Payments", valueFrom = local.shared.shared_payments_secret_arn },
      { name = "Payments__WebhookSecret", valueFrom = local.shared.payments_webhook_secret_arn },
      { name = "ConnectionStrings__Valkey", valueFrom = local.shared.payments_valkey_secret_arn },
    ], local.clone_secrets)
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.payments.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "move-payments"
      }
    }
  }])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}
