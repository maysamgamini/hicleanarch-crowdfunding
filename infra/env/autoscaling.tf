# More tasks when a launch comes, fewer after it. Two target-tracking policies: the service scales
# out when either says so and scales in only when both allow it. They react to load that has already
# arrived, minutes late for a crowd that comes at a known minute, so a known launch also gets a
# scheduled minimum (var.launch). The scaling exists only while the service does (ecs.tf).

variable "launch" {
  description = "A known launch: from start to end (UTC, RFC 3339 such as 2026-11-02T15:45:00Z) at least min_count tasks run, started before the crowd arrives. Null: none scheduled."
  type = object({
    start     = string
    end       = string
    min_count = number
  })
  default = null

  validation {
    condition = var.launch == null || (
      can(timecmp(var.launch.start, var.launch.end)) && endswith(var.launch.start, "Z") && endswith(var.launch.end, "Z")
      && try(timecmp(var.launch.start, var.launch.end) < 0, false)
    )
    error_message = "launch.start and launch.end must be UTC times (ending in Z), start before end."
  }
  validation {
    condition     = var.launch == null || try(var.launch.min_count <= var.max_count, false)
    error_message = "launch.min_count must not exceed max_count, which the connection budget allows."
  }
}

locals {
  # A scheduled action raises the target's minimum, so an apply during the launch must not lower it again.
  in_launch   = var.launch != null && try(timecmp(plantimestamp(), var.launch.start) >= 0 && timecmp(plantimestamp(), var.launch.end) < 0, false)
  min_running = local.in_launch ? var.launch.min_count : var.desired_count
}

resource "aws_appautoscaling_target" "api" {
  count              = length(aws_ecs_service.api)
  service_namespace  = "ecs"
  resource_id        = "service/${aws_ecs_cluster.main.name}/${aws_ecs_service.api[0].name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = local.min_running
  max_capacity       = var.max_count

  # More tasks means more pools: the most they can open must fit under Aurora's max_connections.
  lifecycle {
    precondition {
      condition     = local.db_writer_connections <= local.db_max_connections - local.db_reserved_connections
      error_message = "max_count tasks could open ${local.db_writer_connections} connections to the writer, more than its ${local.db_max_connections - local.db_reserved_connections}: raise the database's maximum ACU, lower the pools, or put a pooler (RDS Proxy) in front."
    }
  }
}

resource "aws_appautoscaling_policy" "cpu" {
  count              = length(aws_appautoscaling_target.api)
  name               = "${var.project}-api-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.api[0].service_namespace
  resource_id        = aws_appautoscaling_target.api[0].resource_id
  scalable_dimension = aws_appautoscaling_target.api[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    target_value       = 60
    scale_out_cooldown = 60
    scale_in_cooldown  = 300

    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}

resource "aws_appautoscaling_policy" "requests" {
  count              = length(aws_appautoscaling_target.api)
  name               = "${var.project}-api-requests"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.api[0].service_namespace
  resource_id        = aws_appautoscaling_target.api[0].resource_id
  scalable_dimension = aws_appautoscaling_target.api[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    # Requests per task per minute; a starting value, to be set from the load test's numbers.
    target_value       = 3000
    scale_out_cooldown = 60
    scale_in_cooldown  = 300

    # The API's target group sits behind the internal load balancer, the one the gateway calls.
    predefined_metric_specification {
      predefined_metric_type = "ALBRequestCountPerTarget"
      resource_label         = "${aws_lb.internal.arn_suffix}/${aws_lb_target_group.api.arn_suffix}"
    }
  }
}

# The launch's minimum, raised at its start and lowered at its end; target tracking adds tasks above
# it as usual. New tasks take minutes to start and pass their checks, so start well before the crowd.
resource "aws_appautoscaling_scheduled_action" "launch_start" {
  count              = var.launch == null ? 0 : length(aws_appautoscaling_target.api)
  name               = "${var.project}-launch-start"
  service_namespace  = aws_appautoscaling_target.api[0].service_namespace
  resource_id        = aws_appautoscaling_target.api[0].resource_id
  scalable_dimension = aws_appautoscaling_target.api[0].scalable_dimension
  schedule           = "at(${trimsuffix(var.launch.start, "Z")})"
  timezone           = "UTC"

  scalable_target_action {
    min_capacity = var.launch.min_count
    max_capacity = var.max_count
  }
}

resource "aws_appautoscaling_scheduled_action" "launch_end" {
  count              = var.launch == null ? 0 : length(aws_appautoscaling_target.api)
  name               = "${var.project}-launch-end"
  service_namespace  = aws_appautoscaling_target.api[0].service_namespace
  resource_id        = aws_appautoscaling_target.api[0].resource_id
  scalable_dimension = aws_appautoscaling_target.api[0].scalable_dimension
  schedule           = "at(${trimsuffix(var.launch.end, "Z")})"
  timezone           = "UTC"

  scalable_target_action {
    min_capacity = var.desired_count
    max_capacity = var.max_count
  }

  # One change at a time to the same target: App Auto Scaling can refuse concurrent ones.
  depends_on = [aws_appautoscaling_scheduled_action.launch_start]
}

moved {
  from = aws_appautoscaling_target.api
  to   = aws_appautoscaling_target.api[0]
}

moved {
  from = aws_appautoscaling_policy.cpu
  to   = aws_appautoscaling_policy.cpu[0]
}

moved {
  from = aws_appautoscaling_policy.requests
  to   = aws_appautoscaling_policy.requests[0]
}
