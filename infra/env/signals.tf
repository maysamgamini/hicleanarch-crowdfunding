# Lesson s5-l13: what each service promises, and when to wake someone (decision service-slos). Every
# service's objectives, pages and dashboard come from infra/modules/service-signals, which the service
# module calls in each service's own root. The gateway, which this layer runs, calls it here.

module "gateway_signals" {
  source          = "../modules/service-signals"
  service         = "gateway"
  name_prefix     = "${var.project}-sandbox"
  region          = var.region
  alarm_topic_arn = aws_sns_topic.alarms.arn
  log_group_name  = aws_cloudwatch_log_group.gateway.name

  # The edge as the public sees it, behind CloudFront. Its own refusals (429) are not errors.
  requests = {
    namespace            = "AWS/ApplicationELB"
    dimensions           = { LoadBalancer = aws_lb.api.arn_suffix, TargetGroup = aws_lb_target_group.gateway.arn_suffix }
    requests_metric      = "RequestCount"
    errors_metric        = "HTTPCode_Target_5XX_Count"
    latency_metric       = "TargetResponseTime"
    latency_units_per_ms = 0.001
  }
  availability_objective = 0.999
  # A service's own objective is 1,000 ms; the gateway adds its hop and its checks.
  latency_objective = { threshold_ms = 1500, target = 0.99 }
}

# The journey view: one saved Logs Insights query over the log groups of the four services that write
# journey lines, by campaign. Replace CAMPAIGN_ID in the console, or run scripts/journey.sh, which
# also marks a service that logged nothing. Read through the operators' role (infra/bootstrap/operators.tf).
locals {
  journey_services = ["campaigns", "pledges", "payments", "notifications"]
}

resource "aws_cloudwatch_query_definition" "journey" {
  name            = "${var.project}-sandbox/journey"
  log_group_names = [for s in local.journey_services : "/${var.project}/sandbox/${s}"]
  query_string    = file("${path.module}/journey.logsinsights")
}

output "journey_query" {
  value = aws_cloudwatch_query_definition.journey.name
}
