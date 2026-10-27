# The operators' role (lesson s5-l13): how support and on-call read the journey view, the dashboards,
# the alarms, the metrics and the traces, and nothing else. It can change nothing, and it reads only
# the project's log groups. Someone in this account assumes it with MFA; in an organisation, an IAM
# Identity Center permission set with the same policy replaces the trust below.
resource "aws_iam_role" "operators" {
  name               = "${var.project}-operators"
  assume_role_policy = data.aws_iam_policy_document.operators_assume.json
}

data "aws_iam_policy_document" "operators_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
    condition {
      test     = "Bool"
      variable = "aws:MultiFactorAuthPresent"
      values   = ["true"]
    }
  }
}

locals {
  project_log_groups = "arn:aws:logs:${var.region}:${data.aws_caller_identity.current.account_id}:log-group:/${var.project}/*"
}

data "aws_iam_policy_document" "operators" {
  # The journey query and the services' log lines, in the project's log groups only.
  statement {
    sid       = "ReadProjectLogs"
    actions   = ["logs:StartQuery", "logs:GetLogEvents", "logs:FilterLogEvents", "logs:DescribeLogStreams"]
    resources = [local.project_log_groups, "${local.project_log_groups}:*"]
  }
  # Calls IAM cannot narrow to a log group: a query's results and the saved queries' list.
  statement {
    sid       = "ReadQueries"
    actions   = ["logs:GetQueryResults", "logs:StopQuery", "logs:DescribeQueries", "logs:DescribeQueryDefinitions", "logs:DescribeLogGroups"]
    resources = ["*"]
  }
  statement {
    sid = "ReadSignals"
    actions = [
      "cloudwatch:GetDashboard", "cloudwatch:ListDashboards", "cloudwatch:DescribeAlarms", "cloudwatch:DescribeAlarmHistory",
      "cloudwatch:GetMetricData", "cloudwatch:ListMetrics",
      "xray:GetTraceSummaries", "xray:BatchGetTraces", "xray:GetServiceGraph", "xray:GetTraceGraph",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "operators" {
  role   = aws_iam_role.operators.id
  policy = data.aws_iam_policy_document.operators.json
}

output "operators_role_arn" {
  description = "Assume it (with MFA) to read the journey view: scripts/journey.sh."
  value       = aws_iam_role.operators.arn
}
