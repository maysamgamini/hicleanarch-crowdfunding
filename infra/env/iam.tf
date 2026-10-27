data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Used by ECS itself: pull the image, write logs, read the connection strings into the container.
# Both roles carry the bootstrap layer's boundary: the deploy role may create them only with it.
resource "aws_iam_role" "execution" {
  name                 = "${var.project}-sandbox-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "read_connection_strings" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = concat([for s in aws_secretsmanager_secret.connection : s.arn], [aws_secretsmanager_secret.webhook.arn])
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.read_connection_strings.json
}

# Used by the running containers: the collector sends traces to X-Ray and metrics to CloudWatch.
resource "aws_iam_role" "task" {
  name                 = "${var.project}-sandbox-task"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

data "aws_caller_identity" "current" {}

locals {
  task_boundary = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/${var.project}-task-boundary"
}

data "aws_iam_policy_document" "telemetry" {
  statement {
    actions = [
      "xray:PutTraceSegments",
      "xray:PutTelemetryRecords",
      "xray:GetSamplingRules",
      "xray:GetSamplingTargets",
      "cloudwatch:PutMetricData",
      "logs:CreateLogStream",
      "logs:PutLogEvents",
      "logs:DescribeLogStreams",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "task_telemetry" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.telemetry.json
}
