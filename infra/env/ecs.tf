resource "aws_ecs_cluster" "main" {
  name = "${var.project}-sandbox"

  # Per-service task counts in CloudWatch, which the running-tasks alarms read (monitoring.tf).
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

# The one-off tasks of the shared layer (the schemas' hand-overs) log here. Every service has its own group
# (infra/modules/service); the monolith that used this one is gone since s5-l10.
resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/sandbox/api"
  retention_in_days = 1
}

# The collector's built-in ECS configuration sends metrics to this group. OpenTofu creates it, so
# destroy removes it; the task role may write to it but not create groups of its own.
resource "aws_cloudwatch_log_group" "collector_metrics" {
  name              = "/aws/ecs/application/metrics"
  retention_in_days = 1
}

# Lesson s5-l10: no monolith is left. The old host runs Pledges alone, as the Pledges service
# (infra/services/pledges, on the service module), so this layer keeps the network, the cluster, the
# database, Valkey, the broker and the gateway, and no program of its own. The sandbox is rebuilt at this
# tag (scripts/destroy-*, then a fresh deploy); the cutover on a live environment is the lesson's exercise.
