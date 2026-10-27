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
    display_total_mismatch = {
      metric      = "totals.display_mismatches"
      meter       = "CrowdFunding.Operations"
      threshold   = 0
      description = "A campaign's display total differs from its ledger's sum at the last nightly check, for longer than the aggregator takes to refresh it: see the error log for the campaign."
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
  treat_missing_data  = "notBreaching"
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
