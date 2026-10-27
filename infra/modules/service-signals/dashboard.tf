# One dashboard per service: what its users get (requests, errors, latency), how far behind its queue
# is, its pages, and its journey lines. Dashboards are code, so a new service gets one by calling this
# module (the service module does). Named <prefix>-<service>-signals: the shared layer's operations
# dashboard per service (infra/env/service-monitoring.tf, s4-l08) keeps <prefix>-<service>.
locals {
  # Zero or one source of request metrics, so a service without endpoints gets no request widgets.
  request_sources = var.requests == null ? [] : [var.requests]
  dimension_list  = flatten([for r in local.request_sources : [for k, v in r.dimensions : [k, v]]])

  request_widgets = flatten([for r in local.request_sources : [
    {
      type = "metric", width = 12, height = 6
      properties = {
        title  = "Requests and 5xx answers"
        region = var.region
        stat   = "Sum"
        period = 60
        metrics = [
          concat([var.requests.namespace, var.requests.requests_metric], local.dimension_list),
          concat([var.requests.namespace, var.requests.errors_metric], local.dimension_list),
        ]
      }
    },
    {
      type = "metric", width = 12, height = 6
      properties = {
        title  = "Latency p50 and p99 (objective: ${var.latency_objective.target * 100}% within ${var.latency_objective.threshold_ms} ms)"
        region = var.region
        period = 60
        metrics = [
          concat([var.requests.namespace, var.requests.latency_metric], local.dimension_list, [{ stat = "p50" }]),
          concat([var.requests.namespace, var.requests.latency_metric], local.dimension_list, [{ stat = "p99" }]),
        ]
      }
    },
  ]])

  lag_widgets = [for queue, q in var.queues : {
    type = "metric", width = 12, height = 6
    properties = {
      title       = "Lag on ${q.queue_name} (objective: under ${q.max_age_seconds} s in ${q.target * 100}% of minutes)"
      region      = var.region
      stat        = "Maximum"
      period      = 60
      metrics     = [["AWS/SQS", "ApproximateAgeOfOldestMessage", "QueueName", q.queue_name]]
      annotations = { horizontal = [{ value = q.max_age_seconds, label = "objective" }] }
    }
  }]

  page_widget = {
    type = "alarm", width = 24, height = 4
    properties = {
      title  = "Pages: budgets burning in both windows"
      alarms = [for a in aws_cloudwatch_composite_alarm.burn : a.arn]
    }
  }

  journey_widget = {
    type = "log", width = 24, height = 8
    properties = {
      title  = "Journey steps (all campaigns, latest first)"
      region = var.region
      query  = "SOURCE '${var.log_group_name}' | fields @timestamp, State.journey.step, State.campaign.id, State.pledge.id, State.outcome | filter ispresent(State.journey.step) | sort @timestamp desc | limit 100"
      view   = "table"
    }
  }
}

resource "aws_cloudwatch_dashboard" "service" {
  dashboard_name = "${var.name_prefix}-${var.service}-signals"
  dashboard_body = jsonencode({
    widgets = concat([local.page_widget], local.request_widgets, local.lag_widgets, [local.journey_widget])
  })
}
