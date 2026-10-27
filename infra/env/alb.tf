# Plain HTTP on port 80: the sandbox has no domain name, so no certificate. Production adds a
# certificate from ACM and an HTTPS listener, and redirects port 80 to it.

resource "aws_lb" "api" {
  name               = "${var.project}-api"
  load_balancer_type = "application"
  subnets            = aws_subnet.public[*].id
  security_groups    = [aws_security_group.alb.id]
}

# Round robin with slow start (the two go together: AWS refuses slow start with least outstanding
# requests). Cross-zone stays on. The deregistration delay is longer than any request and far
# shorter than the 300-second default, so a deploy does not wait five minutes per task.
resource "aws_lb_target_group" "api" {
  name                              = "${var.project}-api"
  port                              = 8080
  protocol                          = "HTTP"
  target_type                       = "ip"
  vpc_id                            = aws_vpc.main.id
  load_balancing_algorithm_type     = "round_robin"
  load_balancing_cross_zone_enabled = "true"
  slow_start                        = 60
  deregistration_delay              = 30

  # The live check never touches the database: ECS replaces a task whose target-group check fails.
  health_check {
    path                = "/health/live"
    matcher             = "200"
    interval            = 10
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.api.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}
