# What the service promises, and when to wake someone (lesson s5-l13, decision service-slos): every
# service on this module gets its objectives, its burn-rate pages and its dashboard from
# infra/modules/service-signals, under names its own deploy role may change (infra/bootstrap/services.tf).
#
# - Availability and latency of its public endpoints, from Service Connect's metrics for the public
#   port (AWS/ECS, milliseconds): every request the gateway sends it.
# - The lag of the queue it reads, if it reads one: the age of the oldest message, minute by minute.
locals {
  # arn:aws:sqs:<region>:<account>:<name>
  queue_name = var.queue_arn == null ? null : element(split(":", var.queue_arn), 5)
}

module "signals" {
  source = "../service-signals"

  service         = var.name
  name_prefix     = "${var.project}-sandbox"
  region          = var.region
  alarm_topic_arn = var.shared.alarm_topic_arn
  log_group_name  = aws_cloudwatch_log_group.service.name

  requests = {
    namespace            = "AWS/ECS"
    dimensions           = { ClusterName = var.shared.cluster_name, ServiceName = var.name, DiscoveryName = "${var.name}-public" }
    requests_metric      = "RequestCount"
    errors_metric        = "HTTPCode_Target_5XX_Count"
    latency_metric       = "TargetResponseTime"
    latency_units_per_ms = 1
  }
  availability_objective = var.objectives.availability
  latency_objective      = var.objectives.latency

  queues = local.queue_name == null ? {} : {
    events = { queue_name = local.queue_name, max_age_seconds = var.objectives.lag.max_age_seconds, target = var.objectives.lag.target }
  }
}
