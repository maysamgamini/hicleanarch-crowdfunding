# What a deployment watches (lesson s5-l14, decision independent-deploys). The circuit breaker catches
# tasks that cannot start or fail their health checks; it does not see a task that is healthy and answers
# 500, or one that fails every event it takes. ECS also watches these alarms while a deployment runs and
# rolls it back when one goes to ALARM. Both are fast on purpose and neither pages anyone: the burn-rate
# pages of s5-l13 do. An alarm already in ALARM when a deployment starts is ignored by that deployment,
# so these alarms must be honest: they read the service's own traffic and its own log group only.

# The share of the service's requests that answered 5xx, minute by minute (Service Connect's metrics for
# its public port): above 5% in 2 of the last 3 minutes. A quiet minute has no data and counts as good.
resource "aws_cloudwatch_metric_alarm" "deployment_5xx" {
  alarm_name          = "${local.prefix}-deployment-5xx"
  alarm_description   = "More than 5% of the requests to ${var.name} answered 5xx in 2 of the last 3 minutes. ECS rolls a running deployment back on it; it pages nobody."
  evaluation_periods  = 3
  datapoints_to_alarm = 2
  comparison_operator = "GreaterThanThreshold"
  threshold           = 5
  treat_missing_data  = "notBreaching"

  metric_query {
    id          = "share"
    expression  = "100 * FILL(errors, 0) / requests"
    label       = "5xx %"
    return_data = true
  }

  dynamic "metric_query" {
    for_each = { errors = "HTTPCode_Target_5XX_Count", requests = "RequestCount" }
    content {
      id = metric_query.key
      metric {
        namespace   = "AWS/ECS"
        metric_name = metric_query.value
        dimensions  = { ClusterName = var.shared.cluster_name, ServiceName = var.name, DiscoveryName = "${var.name}-public" }
        stat        = "Sum"
        period      = 60
      }
    }
  }
}

# For a consumer: the handlers that failed an event, counted from the service's own journey lines (s5-l13,
# outcome "failed"), so the count is this service's and no other's. More than 5 a minute in 2 of the last
# 3 minutes. One poison message retried with back-off fails a few times a minute at most; a release whose
# handlers all throw fails every message it takes.
resource "aws_cloudwatch_log_metric_filter" "handler_failures" {
  count          = var.queue_arn == null ? 0 : 1
  name           = "${local.prefix}-handler-failures"
  log_group_name = aws_cloudwatch_log_group.service.name
  pattern        = "{ ($.EventId = 7000) && ($.State.outcome = \"failed\") }"

  metric_transformation {
    name          = "${var.name}_handler_failures"
    namespace     = "${var.project}/sandbox"
    value         = "1"
    default_value = "0"
    unit          = "Count"
  }
}

resource "aws_cloudwatch_metric_alarm" "deployment_handler_failures" {
  count               = var.queue_arn == null ? 0 : 1
  alarm_name          = "${local.prefix}-deployment-handler-failures"
  alarm_description   = "${var.name}'s handlers failed more than 5 events a minute in 2 of the last 3 minutes. ECS rolls a running deployment back on it; it pages nobody."
  namespace           = aws_cloudwatch_log_metric_filter.handler_failures[0].metric_transformation[0].namespace
  metric_name         = aws_cloudwatch_log_metric_filter.handler_failures[0].metric_transformation[0].name
  statistic           = "Sum"
  period              = 60
  evaluation_periods  = 3
  datapoints_to_alarm = 2
  comparison_operator = "GreaterThanThreshold"
  threshold           = 5
  treat_missing_data  = "notBreaching"
}

locals {
  deployment_alarm_names = concat(
    [aws_cloudwatch_metric_alarm.deployment_5xx.alarm_name],
    aws_cloudwatch_metric_alarm.deployment_handler_failures[*].alarm_name,
  )
}
