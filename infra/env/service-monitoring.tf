# One dashboard and one set of alarms per service (Stage 4, lesson s4-l08). Each service's page shows
# what its own team answers for: its requests, errors and latency, its tasks, its queue and dead-letter
# queues, its topics, and the failure-mode gauges of the modules it runs. Queues and topics are those of
# messaging.tf (s4-l02); the Payments service is payments-split.tf's (s4-l05), and each service's
# Service Connect name is the one the service module gives its public port, <name>-public.

locals {
  services = {
    pledges = {
      ecs_service = "pledges"
      queue       = "pledges"
      topics      = ["pledges"]
      gauges      = ["live_past_deadline", "ended_undecided", "close_recheck_mismatch", "display_total_mismatch", "undelivered_events"]
    }
    payments = {
      ecs_service = "payments"
      queue       = "payments"
      topics      = ["payments"]
      gauges      = ["stuck_charging", "reconciliation_differences", "undelivered_events"]
    }
  }

  # Requests, 5xx and latency, from Service Connect's own metrics for each service's inbound calls
  # (milliseconds); the monolith's internal load balancer is gone since s5-l10.
  service_requests = {
    pledges = {
      requests = ["AWS/ECS", "RequestCount", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "pledges", "DiscoveryName", "pledges-public"]
      errors   = ["AWS/ECS", "HTTPCode_Target_5XX_Count", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "pledges", "DiscoveryName", "pledges-public"]
      latency  = ["AWS/ECS", "TargetResponseTime", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "pledges", "DiscoveryName", "pledges-public"]
      p99_max  = 1000 # milliseconds
    }
    payments = {
      requests = ["AWS/ECS", "RequestCount", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "payments", "DiscoveryName", "payments-public"]
      errors   = ["AWS/ECS", "HTTPCode_Target_5XX_Count", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "payments", "DiscoveryName", "payments-public"]
      latency  = ["AWS/ECS", "TargetResponseTime", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", "payments", "DiscoveryName", "payments-public"]
      p99_max  = 1000 # milliseconds
    }
  }
}

resource "aws_cloudwatch_dashboard" "service" {
  for_each       = local.services
  dashboard_name = "${var.project}-sandbox-${each.key}"
  dashboard_body = jsonencode({
    widgets = concat(
      [
        {
          type = "metric", x = 0, y = 0, width = 8, height = 6
          properties = {
            title   = "Requests and 5xx"
            region  = var.region
            stat    = "Sum"
            period  = 60
            metrics = [local.service_requests[each.key].requests, local.service_requests[each.key].errors]
          }
        },
        {
          type = "metric", x = 8, y = 0, width = 8, height = 6
          properties = {
            title   = "Latency, p50 and p99"
            region  = var.region
            period  = 60
            metrics = [concat(local.service_requests[each.key].latency, [{ stat = "p50" }]), concat(local.service_requests[each.key].latency, [{ stat = "p99" }])]
            annotations = {
              horizontal = [{ value = local.service_requests[each.key].p99_max, label = "p99 alarm above" }]
            }
          }
        },
        {
          type = "metric", x = 16, y = 0, width = 8, height = 6
          properties = {
            title  = "Tasks, CPU and memory"
            region = var.region
            period = 60
            stat   = "Average"
            metrics = [
              ["ECS/ContainerInsights", "RunningTaskCount", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", each.value.ecs_service, { stat = "Minimum" }],
              ["ECS/ContainerInsights", "CpuUtilized", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", each.value.ecs_service, { yAxis = "right" }],
              ["ECS/ContainerInsights", "MemoryUtilized", "ClusterName", aws_ecs_cluster.main.name, "ServiceName", each.value.ecs_service, { yAxis = "right" }],
            ]
          }
        },
        {
          type = "metric", x = 0, y = 6, width = 8, height = 6
          properties = {
            title  = "Its queue: depth and oldest message (s)"
            region = var.region
            period = 60
            stat   = "Maximum"
            metrics = [
              ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", aws_sqs_queue.events[each.value.queue].name],
              ["AWS/SQS", "ApproximateAgeOfOldestMessage", "QueueName", aws_sqs_queue.events[each.value.queue].name, { yAxis = "right" }],
            ]
          }
        },
        {
          type = "metric", x = 8, y = 6, width = 8, height = 6
          properties = {
            title  = "Dead letters: its queue's and its subscriptions'"
            region = var.region
            period = 60
            stat   = "Maximum"
            metrics = [
              ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", aws_sqs_queue.dead_letters[each.value.queue].name],
              ["AWS/SQS", "ApproximateNumberOfMessagesVisible", "QueueName", aws_sqs_queue.subscription_dead_letters[each.value.queue].name],
            ]
          }
        },
        {
          type = "metric", x = 16, y = 6, width = 8, height = 6
          properties = {
            title  = "Its topics: failed and filtered out"
            region = var.region
            period = 60
            stat   = "Sum"
            metrics = concat(
              [for t in each.value.topics : ["AWS/SNS", "NumberOfNotificationsFailed", "TopicName", aws_sns_topic.events[t].name]],
              [for t in each.value.topics : ["AWS/SNS", "NumberOfNotificationsFilteredOut-InvalidAttributes", "TopicName", aws_sns_topic.events[t].name]],
            )
          }
        },
        {
          type = "text", x = 0, y = 12, width = 24, height = 2
          properties = {
            markdown = "Traces: X-Ray, service **crowdfunding-${each.key}**. A message's handling is a child of the change that wrote it only for one pledge's story (PaymentMethodSaved, the charge results); other events start a trace linked to it. A PSP webhook's trace links to its card setup's or charge's trace; the linked id is the span attribute `crowdfunding.linked_trace_id`."
          }
        },
      ],
      [for i, key in each.value.gauges : {
        type   = "metric"
        x      = (i % 3) * 8
        y      = 14 + floor(i / 3) * 6
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
      }]
    )
  })
}

# Slow answers, per service: the p99 above its limit for five minutes. Quiet minutes have no data.
resource "aws_cloudwatch_metric_alarm" "service_latency" {
  for_each            = local.service_requests
  alarm_name          = "${var.project}-sandbox-${each.key}_p99_latency"
  alarm_description   = "The ${each.key} service's p99 response time has been above ${each.value.p99_max} for five minutes: see its dashboard, then the slowest traces of the service in X-Ray."
  namespace           = each.value.latency[0]
  metric_name         = each.value.latency[1]
  dimensions          = { for i in range(2, length(each.value.latency), 2) : each.value.latency[i] => each.value.latency[i + 1] }
  extended_statistic  = "p99"
  period              = 60
  evaluation_periods  = 5
  comparison_operator = "GreaterThanThreshold"
  threshold           = each.value.p99_max
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# Payments' 5xx share, from Service Connect: it has no load balancer of its own, so the gateway's
# alarm in monitoring.tf does not see it.
resource "aws_cloudwatch_metric_alarm" "payments_5xx" {
  alarm_name          = "${var.project}-sandbox-payments_5xx_rate"
  alarm_description   = "More than 5% of the calls to Payments answered 5xx for three minutes: see its error log."
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
      namespace   = "AWS/ECS"
      metric_name = "HTTPCode_Target_5XX_Count"
      dimensions  = { ClusterName = aws_ecs_cluster.main.name, ServiceName = "payments", DiscoveryName = "payments-public" }
      stat        = "Sum"
      period      = 60
    }
  }
  metric_query {
    id = "requests"
    metric {
      namespace   = "AWS/ECS"
      metric_name = "RequestCount"
      dimensions  = { ClusterName = aws_ecs_cluster.main.name, ServiceName = "payments", DiscoveryName = "payments-public" }
      stat        = "Sum"
      period      = 60
    }
  }
}

# Payments' running-tasks alarm is payments-split.tf's (s4-l05).

output "service_dashboards" {
  value = { for k, d in aws_cloudwatch_dashboard.service : k => d.dashboard_name }
}
