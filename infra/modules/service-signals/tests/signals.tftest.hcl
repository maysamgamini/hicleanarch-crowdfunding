# tofu test (or terraform test), with a mocked AWS provider: no account, no calls. Run from
# infra/modules/service-signals (scripts/check-infra.sh does).
mock_provider "aws" {}

variables {
  service         = "pledges"
  name_prefix     = "hicleanarch-sandbox"
  region          = "us-east-1"
  alarm_topic_arn = "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-alarms"
  log_group_name  = "/hicleanarch/sandbox/pledges"
  # Service Connect's metrics for the service's public port; milliseconds.
  requests = {
    namespace            = "AWS/ECS"
    dimensions           = { ClusterName = "hicleanarch-sandbox", ServiceName = "pledges", DiscoveryName = "pledges-public" }
    target_dimensions    = { TargetDiscoveryName = "pledges-public" }
    requests_metric      = "RequestCount"
    errors_metric        = "HTTPCode_Target_5XX_Count"
    latency_metric       = "TargetResponseTime"
    latency_units_per_ms = 1
  }
  # Pledges' queue carries CampaignPublished: a minute in which a message waited longer than the
  # clients' retries (1 + 2 + 4 s) is a bad minute.
  queues = {
    events = { queue_name = "hicleanarch-sandbox-pledges", max_age_seconds = 10, target = 0.99 }
  }
}

# Applied against the mocked provider, so the dashboard's body (which names the pages' ARNs) is known.
run "every_objective_has_a_fast_and_a_slow_page_from_two_windows" {
  command = apply

  assert {
    condition     = length(aws_cloudwatch_metric_alarm.request_window) == 8 && length(aws_cloudwatch_metric_alarm.lag_window) == 4
    error_message = "Two request objectives and one lag objective, each with two speeds of two windows."
  }

  assert {
    condition = toset(keys(aws_cloudwatch_composite_alarm.burn)) == toset([
      "availability-fast", "availability-slow", "latency-fast", "latency-slow", "lag-events-fast", "lag-events-slow",
    ])
    error_message = "One page per objective and speed."
  }

  assert {
    condition     = aws_cloudwatch_composite_alarm.burn["availability-fast"].alarm_rule == "ALARM(\"hicleanarch-sandbox-pledges-availability-fast-long\") AND ALARM(\"hicleanarch-sandbox-pledges-availability-fast-short\")"
    error_message = "A page needs both windows in ALARM."
  }

  assert {
    condition     = alltrue([for a in aws_cloudwatch_metric_alarm.request_window : try(length(a.alarm_actions), 0) == 0]) && alltrue([for a in aws_cloudwatch_metric_alarm.lag_window : try(length(a.alarm_actions), 0) == 0])
    error_message = "A window alarm alone pages nobody."
  }

  assert {
    condition     = alltrue([for a in aws_cloudwatch_composite_alarm.burn : a.alarm_actions == toset([var.alarm_topic_arn])])
    error_message = "Every page goes to the alarm topic."
  }

  assert {
    condition     = aws_cloudwatch_metric_alarm.request_window["availability-fast-long"].threshold == 14.4 && aws_cloudwatch_metric_alarm.request_window["availability-slow-short"].threshold == 6
    error_message = "Fast pages at 14.4 times the sustainable burn, slow at 6."
  }

  assert {
    condition     = one([for q in aws_cloudwatch_metric_alarm.request_window["availability-fast-long"].metric_query : q.expression if q.id == "burn"]) == "(FILL(errors, 0) / requests) / 0.001000"
    error_message = "Availability burn: the 5xx share over the budget's share."
  }

  assert {
    condition     = one([for q in aws_cloudwatch_metric_alarm.request_window["latency-slow-long"].metric_query : q.metric[0].stat if q.id == "within"]) == "TC(:1000)"
    error_message = "Service Connect reports milliseconds: TC(:1000)."
  }

  assert {
    condition     = one([for q in aws_cloudwatch_metric_alarm.request_window["latency-slow-long"].metric_query : q.metric[0].period if q.id == "timed"]) == 21600
    error_message = "A request window is the alarm's period."
  }

  assert {
    condition     = aws_cloudwatch_metric_alarm.lag_window["lag-events-fast-long"].evaluation_periods == 60 && aws_cloudwatch_metric_alarm.lag_window["lag-events-fast-long"].datapoints_to_alarm == 9
    error_message = "Fast lag: 9 bad minutes of 60 burn 14.4 times a 1% budget."
  }

  assert {
    condition     = aws_cloudwatch_metric_alarm.lag_window["lag-events-slow-long"].evaluation_periods == 360 && aws_cloudwatch_metric_alarm.lag_window["lag-events-slow-long"].datapoints_to_alarm == 22
    error_message = "Slow lag: 22 bad minutes of 360."
  }

  assert {
    condition     = aws_cloudwatch_dashboard.service.dashboard_name == "hicleanarch-sandbox-pledges-signals" && strcontains(aws_cloudwatch_dashboard.service.dashboard_body, "hicleanarch-sandbox-pledges")
    error_message = "One dashboard per service, with its queue's lag."
  }
}

# One bad hour at 2% errors: 20 times a 0.1% budget over the last hour and the last 5 minutes, but over
# 6 hours only 3.3 times. The fast page fires; the slow one does not.
run "a_synthetic_burn_fires_the_fast_page_and_not_the_slow_one" {
  command = plan

  module {
    source = "./tests/evaluate"
  }

  variables {
    windows = run.every_objective_has_a_fast_and_a_slow_page_from_two_windows.burn_windows
    synthetic_requests = {
      availability = {
        "3600"  = { bad = 1200, total = 60000 }
        "300"   = { bad = 100, total = 5000 }
        "21600" = { bad = 1200, total = 360000 }
        "1800"  = { bad = 600, total = 30000 }
      }
    }
  }

  assert {
    condition     = jsonencode(output.pages) == jsonencode(["availability-fast"])
    error_message = "Only the fast availability page fires: ${jsonencode(output.pages)}"
  }

  assert {
    condition     = output.firing["availability-slow-short"] && !output.firing["availability-slow-long"]
    error_message = "The slow short window burns, the slow long one does not."
  }
}

# A CampaignPublished held on Pledges' queue for 15 minutes: its age grows by a minute each minute, so
# all 15 minutes are over the 10 s limit. 15 bad minutes of the last 60 page fast (9 needed); of the
# last 360 they are under the slow page's 22.
run "a_held_message_raises_the_lag_and_pages_fast" {
  command = plan

  module {
    source = "./tests/evaluate"
  }

  variables {
    windows = run.every_objective_has_a_fast_and_a_slow_page_from_two_windows.burn_windows
    synthetic_ages = {
      "lag-events" = concat([for i in range(345) : 0], [for i in range(15) : (i + 1) * 60])
    }
  }

  assert {
    condition     = jsonencode(output.pages) == jsonencode(["lag-events-fast"])
    error_message = "The held message pages fast, not slow: ${jsonencode(output.pages)}"
  }
}

# A quiet service: no traffic burns nothing.
run "no_traffic_pages_nobody" {
  command = plan

  module {
    source = "./tests/evaluate"
  }

  variables {
    windows = run.every_objective_has_a_fast_and_a_slow_page_from_two_windows.burn_windows
  }

  assert {
    condition     = length(output.pages) == 0
    error_message = "Nothing fires without traffic."
  }
}

# The gateway behind its load balancer: seconds, so 1,500 ms is TC(:1.5). It reads no queue.
run "load_balancer_latency_in_seconds_and_no_queue" {
  command = plan

  variables {
    service        = "gateway"
    log_group_name = "/hicleanarch/sandbox/gateway"
    requests = {
      namespace            = "AWS/ApplicationELB"
      dimensions           = { LoadBalancer = "app/hicleanarch-sandbox/1", TargetGroup = "targetgroup/hicleanarch-gateway/2" }
      requests_metric      = "RequestCount"
      errors_metric        = "HTTPCode_Target_5XX_Count"
      latency_metric       = "TargetResponseTime"
      latency_units_per_ms = 0.001
    }
    latency_objective = { threshold_ms = 1500, target = 0.99 }
    queues            = {}
  }

  assert {
    condition     = one([for q in aws_cloudwatch_metric_alarm.request_window["latency-fast-short"].metric_query : q.metric[0].stat if q.id == "within"]) == "TC(:1.5)"
    error_message = "The load balancer reports seconds: 1,500 ms is TC(:1.5)."
  }

  assert {
    condition     = length(aws_cloudwatch_metric_alarm.lag_window) == 0 && length(aws_cloudwatch_composite_alarm.burn) == 4
    error_message = "No queue, no lag objective: availability and latency, fast and slow."
  }
}

run "a_consumer_without_endpoints_has_only_its_lag" {
  command = plan

  variables {
    service  = "notifications"
    requests = null
  }

  assert {
    condition     = length(aws_cloudwatch_metric_alarm.request_window) == 0 && length(aws_cloudwatch_composite_alarm.burn) == 2
    error_message = "No endpoint, no request objectives: only the lag pages."
  }
}

# CQA-2123: Service Connect publishes RequestCount under DiscoveryName, and HTTPCode_Target_5XX_Count and
# TargetResponseTime under TargetDiscoveryName only. Each metric of a window reads its own dimensions.
run "each_metric_reads_the_dimensions_it_is_published_under" {
  command = plan

  assert {
    condition = alltrue(flatten([for a in aws_cloudwatch_metric_alarm.request_window : [for q in a.metric_query : [for m in q.metric :
      m.metric_name == "RequestCount" ? m.dimensions == tomap(var.requests.dimensions) : m.dimensions == tomap({ TargetDiscoveryName = "pledges-public" })
    ]]]))
    error_message = "A window reads the 5xx count or the response time under the requests' dimensions, where it has no data."
  }
}
