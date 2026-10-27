# More tasks when a launch comes, fewer after it. Two target-tracking policies: the service scales
# out when either says so and scales in only when both allow it.

resource "aws_appautoscaling_target" "api" {
  service_namespace  = "ecs"
  resource_id        = "service/${aws_ecs_cluster.main.name}/${aws_ecs_service.api.name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.desired_count
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
  name               = "${var.project}-api-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.api.service_namespace
  resource_id        = aws_appautoscaling_target.api.resource_id
  scalable_dimension = aws_appautoscaling_target.api.scalable_dimension

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
  name               = "${var.project}-api-requests"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.api.service_namespace
  resource_id        = aws_appautoscaling_target.api.resource_id
  scalable_dimension = aws_appautoscaling_target.api.scalable_dimension

  target_tracking_scaling_policy_configuration {
    # Requests per task per minute; a starting value, to be set from the load test's numbers.
    target_value       = 3000
    scale_out_cooldown = 60
    scale_in_cooldown  = 300

    predefined_metric_specification {
      predefined_metric_type = "ALBRequestCountPerTarget"
      resource_label         = "${aws_lb.api.arn_suffix}/${aws_lb_target_group.api.arn_suffix}"
    }
  }
}
