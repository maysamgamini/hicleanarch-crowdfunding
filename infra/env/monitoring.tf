# One alarm per Round 1 failure mode. The program measures each as an OpenTelemetry gauge; the
# collector's built-in ECS configuration writes them to CloudWatch in the namespace below, with the
# meter's name as the OTelLib dimension.

locals {
  metrics_namespace = "ECS/AWSOTel/Application"

  alarms = {
    live_past_deadline = {
      metric      = "campaigns.live_past_deadline"
      meter       = "CrowdFunding.Operations"
      threshold   = 0
      description = "A campaign is more than 5 minutes past its deadline and still LIVE: the close job is not running."
    }
    ended_undecided = {
      metric      = "campaigns.ended_undecided"
      meter       = "CrowdFunding.Operations"
      threshold   = 0
      description = "A campaign has been ENDED for more than 5 minutes without a decision: a close pass died between the close and the decision, and no pass has run since."
    }
    stuck_charging = {
      metric      = "charges.stuck_charging"
      meter       = "CrowdFunding.Operations"
      threshold   = 0
      description = "A charge has had an attempt in flight for more than 10 minutes: the PSP may be down, or no settlement worker is running."
    }
    close_recheck_mismatch = {
      metric      = "pledges.close.recheck_mismatches"
      meter       = "CrowdFunding.Pledges"
      threshold   = 0
      description = "A decision's sum differed from the same sum a minute later: an entry landed after the close's time fence (lesson s3-l05). Treat the campaign's outcome as suspect; see the critical log."
    }
    display_total_mismatch = {
      metric      = "totals.display_mismatches"
      meter       = "CrowdFunding.Operations"
      threshold   = 0
      description = "A campaign's display total differs from its ledger's sum at the last nightly check, for longer than the aggregator takes to refresh it: see the error log for the campaign."
    }
    reconciliation_differences = {
      metric      = "payments.reconciliation.differences"
      meter       = "CrowdFunding.Payments"
      threshold   = 0
      description = "Our money ledger and the PSP's report disagree for a settled campaign: its payout waits. See payments.reconciliation_differences for each movement and kind (lesson s3-l15)."
    }
    undelivered_events = {
      metric      = "outbox.oldest_undelivered_age"
      meter       = "CrowdFunding.Outbox"
      threshold   = 300
      description = "An event has waited more than 5 minutes to reach its handlers: a handler keeps failing, or the task died after the commit."
    }
  }
}

resource "aws_sns_topic" "alarms" {
  name = "${var.project}-sandbox-alarms"
}

resource "aws_sns_topic_subscription" "alarm_email" {
  count     = var.alarm_email == "" ? 0 : 1
  topic_arn = aws_sns_topic.alarms.arn
  protocol  = "email"
  endpoint  = var.alarm_email
}

locals {
  # The gauges are reported by every task at every export, even at 0, so a minute with no data means
  # that no task, or no collector, is reporting: the failure the alarm is there for, so it breaches.
  # A counter reports nothing until it first counts; for these, no data is the normal case.
  counters = ["close_recheck_mismatch"]
}

resource "aws_cloudwatch_metric_alarm" "failure_mode" {
  for_each            = local.alarms
  alarm_name          = "${var.project}-sandbox-${each.key}"
  alarm_description   = each.value.description
  namespace           = local.metrics_namespace
  metric_name         = each.value.metric
  dimensions          = { OTelLib = each.value.meter }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 2
  comparison_operator = "GreaterThanThreshold"
  threshold           = each.value.threshold
  treat_missing_data  = contains(local.counters, each.key) ? "notBreaching" : "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_dashboard" "operations" {
  dashboard_name = "${var.project}-sandbox"
  dashboard_body = jsonencode({
    widgets = concat(
      [for i, key in keys(local.alarms) : {
        type   = "metric"
        x      = (i % 3) * 8
        y      = floor(i / 3) * 6
        width  = 8
        height = 6
        properties = {
          title   = local.alarms[key].metric
          region  = var.region
          stat    = "Maximum"
          period  = 60
          metrics = [[local.metrics_namespace, local.alarms[key].metric, "OTelLib", local.alarms[key].meter]]
          annotations = {
            horizontal = [{ value = local.alarms[key].threshold, label = "alarm above" }]
          }
        }
      }],
      [{
        type   = "metric"
        x      = 16
        y      = 6
        width  = 8
        height = 6
        properties = {
          title  = "Requests and errors at the load balancer"
          region = var.region
          period = 60
          stat   = "Sum"
          metrics = [
            ["AWS/ApplicationELB", "RequestCount", "LoadBalancer", aws_lb.api.arn_suffix],
            ["AWS/ApplicationELB", "HTTPCode_Target_5XX_Count", "LoadBalancer", aws_lb.api.arn_suffix],
          ]
        }
      }]
    )
  })
}

# Next month's ledger partition is missing: its first pledge would fail (lesson s3-l10). An alarm
# below a threshold, so it needs LessThanThreshold, and missing data must breach: a job or a program
# that stopped reporting is no proof that the partition exists.
resource "aws_cloudwatch_metric_alarm" "ledger_partition" {
  alarm_name          = "${var.project}-sandbox-ledger_partition_missing"
  alarm_description   = "Fewer than one whole month ahead has a ledger partition: next month's first pledge will fail. The partition job, which runs at start and daily, failed; see its error log."
  namespace           = local.metrics_namespace
  metric_name         = "ledger.partition_months_ahead"
  dimensions          = { OTelLib = "CrowdFunding.Pledges" }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# The platform under the program, which the failure-mode gauges cannot see when it is down.

# Fewer tasks running than the service's minimum (ECS Container Insights, enabled on the cluster).
resource "aws_cloudwatch_metric_alarm" "running_tasks" {
  for_each            = { api = var.desired_count, gateway = var.gateway_count }
  alarm_name          = "${var.project}-sandbox-${each.key}_running_tasks"
  alarm_description   = "The ${each.key} service runs fewer tasks than its minimum of ${each.value}: tasks are crashing, failing health checks, or cannot start."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = aws_ecs_cluster.main.name, ServiceName = each.key }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 3
  comparison_operator = "LessThanThreshold"
  threshold           = each.value
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

locals {
  # The public load balancer sends to the gateway; the internal one sends to the monolith.
  balanced = {
    gateway = { load_balancer = aws_lb.api.arn_suffix, target_group = aws_lb_target_group.gateway.arn_suffix }
    api     = { load_balancer = aws_lb.internal.arn_suffix, target_group = aws_lb_target_group.api.arn_suffix }
  }
}

resource "aws_cloudwatch_metric_alarm" "healthy_hosts" {
  for_each            = local.balanced
  alarm_name          = "${var.project}-sandbox-${each.key}_no_healthy_targets"
  alarm_description   = "No ${each.key} task passes the load balancer's health check: every request to it fails."
  namespace           = "AWS/ApplicationELB"
  metric_name         = "HealthyHostCount"
  dimensions          = { LoadBalancer = each.value.load_balancer, TargetGroup = each.value.target_group }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 2
  comparison_operator = "LessThanThreshold"
  threshold           = 1
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# 5xx answers as a share of all requests, so a quiet minute with one error does not page anyone.
# No requests means no data, which is fine here.
resource "aws_cloudwatch_metric_alarm" "target_5xx" {
  for_each            = local.balanced
  alarm_name          = "${var.project}-sandbox-${each.key}_5xx_rate"
  alarm_description   = "More than 5% of the requests to ${each.key} answered 5xx for three minutes: see its error log."
  evaluation_periods  = 3
  comparison_operator = "GreaterThanThreshold"
  threshold           = 5
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]

  metric_query {
    id          = "rate"
    expression  = "100 * FILL(errors, 0) / requests"
    label       = "5xx %"
    return_data = true
  }
  metric_query {
    id = "errors"
    metric {
      namespace   = "AWS/ApplicationELB"
      metric_name = "HTTPCode_Target_5XX_Count"
      dimensions  = { LoadBalancer = each.value.load_balancer, TargetGroup = each.value.target_group }
      stat        = "Sum"
      period      = 60
    }
  }
  metric_query {
    id = "requests"
    metric {
      namespace   = "AWS/ApplicationELB"
      metric_name = "RequestCount"
      dimensions  = { LoadBalancer = each.value.load_balancer, TargetGroup = each.value.target_group }
      stat        = "Sum"
      period      = 60
    }
  }
}

# The Aurora writer, whichever instance holds the role. A paused database reports nothing, which is
# fine. Connections are compared with the budget in README.md; capacity with the maximum ACU.
resource "aws_cloudwatch_metric_alarm" "database_connections" {
  alarm_name          = "${var.project}-sandbox-database_connections"
  alarm_description   = "The writer holds more than 80% of its max_connections (about ${local.db_max_connections}): pools are larger or more numerous than the budget allows. New connections will soon be refused."
  namespace           = "AWS/RDS"
  metric_name         = "DatabaseConnections"
  dimensions          = { DBClusterIdentifier = aws_rds_cluster.main.cluster_identifier, Role = "WRITER" }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 3
  comparison_operator = "GreaterThanThreshold"
  threshold           = floor(local.db_max_connections * 0.8)
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

resource "aws_cloudwatch_metric_alarm" "database_capacity" {
  alarm_name          = "${var.project}-sandbox-database_capacity"
  alarm_description   = "The writer has run above 90% of its maximum ACU for five minutes: it cannot scale further, and queries slow down."
  namespace           = "AWS/RDS"
  metric_name         = "ACUUtilization"
  dimensions          = { DBClusterIdentifier = aws_rds_cluster.main.cluster_identifier, Role = "WRITER" }
  statistic           = "Average"
  period              = 60
  evaluation_periods  = 5
  comparison_operator = "GreaterThanThreshold"
  threshold           = 90
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}
