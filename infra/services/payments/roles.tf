# Payments' own roles, each under the task boundary: it reads only its own secrets and its own queue,
# and publishes only to its own topic. The names start with hicleanarch-sandbox-payments-, the only
# roles Payments' deploy role may create, change or pass.

data "aws_iam_policy_document" "ecs_tasks_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Used by ECS to start the service's tasks: pull the image, write logs, read Payments' secrets.
resource "aws_iam_role" "execution" {
  name                 = "${var.project}-sandbox-payments-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      local.shared.payments_connection_secret_arn,
      local.shared.payments_webhook_secret_arn,
      local.shared.payments_valkey_secret_arn,
    ]
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.secrets.json
}

# Used by ECS to start the migration task only. It also reads the database owner's secret, which the
# service's own execution role never sees.
resource "aws_iam_role" "migrate_execution" {
  name                 = "${var.project}-sandbox-payments-migrate-execution"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

resource "aws_iam_role_policy_attachment" "migrate_execution" {
  role       = aws_iam_role.migrate_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "migrate_secrets" {
  source_policy_documents = [data.aws_iam_policy_document.secrets.json]
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [local.shared.payments_database_owner_secret_arn]
  }
}

resource "aws_iam_role_policy" "migrate_execution_secrets" {
  role   = aws_iam_role.migrate_execution.id
  policy = data.aws_iam_policy_document.migrate_secrets.json
}

# Used by the running containers: telemetry, its own queue, its own topic.
resource "aws_iam_role" "task" {
  name                 = "${var.project}-sandbox-payments-task"
  assume_role_policy   = data.aws_iam_policy_document.ecs_tasks_assume.json
  permissions_boundary = local.task_boundary
}

data "aws_iam_policy_document" "task" {
  statement {
    sid = "Telemetry"
    actions = [
      "xray:PutTraceSegments", "xray:PutTelemetryRecords", "xray:GetSamplingRules", "xray:GetSamplingTargets",
      "cloudwatch:PutMetricData", "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams",
    ]
    resources = ["*"]
  }
  statement {
    sid       = "ReadItsQueue"
    actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"]
    resources = [local.shared.payments_queue_arn]
  }
  statement {
    sid       = "PublishItsEvents"
    actions   = ["sns:Publish"]
    resources = [local.shared.payments_topic_arn]
  }
  statement {
    sid       = "CheckItsSubscriptions"
    actions   = ["sns:ListSubscriptionsByTopic"]
    resources = values(local.shared.event_topic_arns)
  }
  statement {
    # A subscription's ARN is its topic's plus an id.
    sid       = "ReadItsSubscriptionsFilters"
    actions   = ["sns:GetSubscriptionAttributes"]
    resources = [for arn in values(local.shared.event_topic_arns) : "${arn}:*"]
  }
}

resource "aws_iam_role_policy" "task" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task.json
}
