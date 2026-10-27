# One service's objectives, error budgets and burn-rate alarms (lesson s5-l13, decision service-slos).
#
# Each objective has a 30-day error budget. An alarm pages when the budget burns fast enough to matter,
# seen in a long and a short window at once (the Google SRE workbook's multi-window, multi-burn-rate
# alerts): the long window says enough budget went, the short one that it is still going, so the page
# also clears soon after the problem does.
#   fast: 2% of the budget in 1 hour  = 14.4 times the sustainable rate, also over the last 5 minutes
#   slow: 5% of the budget in 6 hours = 6 times,                          also over the last 30 minutes
# Each window is a CloudWatch alarm with no action; each pair is a composite alarm, which pages.
#
# Request objectives (availability, latency) are counted in requests: the window is the alarm's period,
# and the burn rate is metric math on its sums. The lag objective is counted in minutes: an alarm with a
# 1-minute period counts the bad minutes of the window (M out of N datapoints).

locals {
  budget_days = 30

  speeds = {
    fast = { long = 3600, short = 300, factor = 14.4 }
    slow = { long = 21600, short = 1800, factor = 6 }
  }

  request_objectives = { for k, o in {
    availability = {
      target = var.availability_objective
      # The share of requests that answered 5xx.
      bad = "FILL(errors, 0) / requests"
    }
    latency = {
      target = var.latency_objective.target
      # The share of requests slower than the threshold: all of them less those within it.
      bad = "1 - FILL(within, 0) / timed"
    }
  } : k => o if var.requests != null }

  # Every window alarm, flattened: objective x speed x window.
  request_windows = merge([
    for objective, o in local.request_objectives : merge([
      for speed, s in local.speeds : {
        for window in ["long", "short"] : "${objective}-${speed}-${window}" => {
          objective = objective
          speed     = speed
          window    = window
          seconds   = s[window]
          factor    = s.factor
          target    = o.target
          bad       = o.bad
        }
      }
    ]...)
  ]...)

  lag_windows = merge([
    for queue, q in var.queues : merge([
      for speed, s in local.speeds : {
        for window in ["long", "short"] : "lag-${queue}-${speed}-${window}" => {
          queue           = queue
          queue_name      = q.queue_name
          max_age_seconds = q.max_age_seconds
          target          = q.target
          speed           = speed
          window          = window
          minutes         = s[window] / 60
          factor          = s.factor
          # Bad minutes in the window that burn the budget at the speed's rate: factor x the allowed share.
          bad_minutes = ceil(s.factor * (1 - q.target) * s[window] / 60)
        }
      }
    ]...)
  ]...)

  latency_threshold = var.requests == null ? 0 : var.latency_objective.threshold_ms * var.requests.latency_units_per_ms

  composites = merge(
    { for k in distinct([for w in local.request_windows : "${w.objective}-${w.speed}"]) : k => {
      long  = "${k}-long"
      short = "${k}-short"
    } },
    { for k in distinct([for w in local.lag_windows : "lag-${w.queue}-${w.speed}"]) : k => {
      long  = "${k}-long"
      short = "${k}-short"
    } },
  )
}

resource "aws_cloudwatch_metric_alarm" "request_window" {
  for_each            = local.request_windows
  alarm_name          = "${var.name_prefix}-${var.service}-${each.key}"
  alarm_description   = "${var.service}: the ${each.value.objective} budget (${each.value.target * 100}% over ${local.budget_days} days) burns at ${each.value.factor}x or more over ${each.value.seconds / 60} minutes. Part of a composite alarm; it pages nobody alone."
  evaluation_periods  = 1
  comparison_operator = "GreaterThanOrEqualToThreshold"
  threshold           = each.value.factor
  # No requests in the window: nothing burned.
  treat_missing_data = "notBreaching"

  metric_query {
    id          = "burn"
    expression  = "(${each.value.bad}) / ${format("%.6f", 1 - each.value.target)}"
    label       = "${each.value.objective} burn rate"
    return_data = true
  }

  dynamic "metric_query" {
    for_each = each.value.objective == "availability" ? {
      errors   = { metric = var.requests.errors_metric, stat = "Sum" }
      requests = { metric = var.requests.requests_metric, stat = "Sum" }
      } : {
      within = { metric = var.requests.latency_metric, stat = "TC(:${local.latency_threshold})" }
      timed  = { metric = var.requests.latency_metric, stat = "SampleCount" }
    }
    content {
      id = metric_query.key
      metric {
        namespace   = var.requests.namespace
        metric_name = metric_query.value.metric
        dimensions  = var.requests.dimensions
        stat        = metric_query.value.stat
        period      = each.value.seconds
      }
    }
  }
}

resource "aws_cloudwatch_metric_alarm" "lag_window" {
  for_each            = local.lag_windows
  alarm_name          = "${var.name_prefix}-${var.service}-${each.key}"
  alarm_description   = "${var.service}: ${each.value.bad_minutes} or more of the last ${each.value.minutes} minutes had a message older than ${each.value.max_age_seconds} s on ${each.value.queue_name} (objective ${each.value.target * 100}% of minutes). Part of a composite alarm; it pages nobody alone."
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateAgeOfOldestMessage"
  dimensions          = { QueueName = each.value.queue_name }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = each.value.minutes
  datapoints_to_alarm = each.value.bad_minutes
  comparison_operator = "GreaterThanThreshold"
  threshold           = each.value.max_age_seconds
  # An empty queue reports no age: a good minute.
  treat_missing_data = "notBreaching"
}

# The pages: a budget burning in both windows of a speed.
resource "aws_cloudwatch_composite_alarm" "burn" {
  for_each          = local.composites
  alarm_name        = "${var.name_prefix}-${var.service}-${each.key}-burn"
  alarm_description = "${var.service} is burning its ${each.key} error budget in both windows: users are affected now. See the ${var.service} dashboard."
  alarm_rule        = "ALARM(\"${var.name_prefix}-${var.service}-${each.value.long}\") AND ALARM(\"${var.name_prefix}-${var.service}-${each.value.short}\")"
  alarm_actions     = [var.alarm_topic_arn]
  ok_actions        = [var.alarm_topic_arn]

  depends_on = [aws_cloudwatch_metric_alarm.request_window, aws_cloudwatch_metric_alarm.lag_window]
}
