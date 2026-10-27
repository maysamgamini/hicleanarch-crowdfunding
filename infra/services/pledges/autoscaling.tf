# More tasks when a launch comes, fewer after it (the monolith's scaling of s3, now the Pledges service's).
# Target tracking on CPU reacts to load that has already arrived, minutes late for a crowd that comes at a
# known minute, so a known launch also gets a scheduled minimum (var.launch). The shared layer checks that
# max_count tasks fit the writer's connection budget (infra/env/database.tf) and publishes both counts.

locals {
  max_count = local.shared.pledges_max_count
  # A scheduled action raises the target's minimum, so an apply during the launch must not lower it again.
  in_launch   = var.launch != null && try(timecmp(plantimestamp(), var.launch.start) >= 0 && timecmp(plantimestamp(), var.launch.end) < 0, false)
  min_running = local.in_launch ? var.launch.min_count : local.shared.pledges_desired_count
}

resource "aws_appautoscaling_target" "pledges" {
  count              = module.service.ecs_service == null ? 0 : 1
  service_namespace  = "ecs"
  resource_id        = "service/${local.shared.cluster_name}/${module.service.service}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = local.min_running
  max_capacity       = local.max_count

  lifecycle {
    precondition {
      condition     = var.launch == null || try(var.launch.min_count <= local.max_count, false)
      error_message = "launch.min_count must not exceed max_count, which the connection budget allows."
    }
  }
}

resource "aws_appautoscaling_policy" "cpu" {
  count              = length(aws_appautoscaling_target.pledges)
  name               = "${var.project}-pledges-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.pledges[0].service_namespace
  resource_id        = aws_appautoscaling_target.pledges[0].resource_id
  scalable_dimension = aws_appautoscaling_target.pledges[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    target_value       = 60
    scale_out_cooldown = 60
    scale_in_cooldown  = 300

    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}

# The launch's minimum, raised at its start and lowered at its end; target tracking adds tasks above it.
resource "aws_appautoscaling_scheduled_action" "launch_start" {
  count              = var.launch == null ? 0 : length(aws_appautoscaling_target.pledges)
  name               = "${var.project}-launch-start"
  service_namespace  = aws_appautoscaling_target.pledges[0].service_namespace
  resource_id        = aws_appautoscaling_target.pledges[0].resource_id
  scalable_dimension = aws_appautoscaling_target.pledges[0].scalable_dimension
  schedule           = "at(${trimsuffix(var.launch.start, "Z")})"
  timezone           = "UTC"

  scalable_target_action {
    min_capacity = var.launch.min_count
    max_capacity = local.max_count
  }
}

resource "aws_appautoscaling_scheduled_action" "launch_end" {
  count              = var.launch == null ? 0 : length(aws_appautoscaling_target.pledges)
  name               = "${var.project}-launch-end"
  service_namespace  = aws_appautoscaling_target.pledges[0].service_namespace
  resource_id        = aws_appautoscaling_target.pledges[0].resource_id
  scalable_dimension = aws_appautoscaling_target.pledges[0].scalable_dimension
  schedule           = "at(${trimsuffix(var.launch.end, "Z")})"
  timezone           = "UTC"

  scalable_target_action {
    min_capacity = local.shared.pledges_desired_count
    max_capacity = local.max_count
  }

  # One change at a time to the same target: App Auto Scaling can refuse concurrent ones.
  depends_on = [aws_appautoscaling_scheduled_action.launch_start]
}
