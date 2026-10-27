# Plain HTTP on port 80, reached only through CloudFront (cdn.tf): the sandbox has no domain name,
# so no certificate. Production adds a certificate from ACM and an HTTPS listener.

resource "aws_lb" "api" {
  name               = "${var.project}-api"
  load_balancer_type = "application"
  subnets            = aws_subnet.public[*].id
  security_groups    = [aws_security_group.alb.id]
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.api.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "fixed-response"
    fixed_response {
      content_type = "text/plain"
      message_body = "Forbidden"
      status_code  = "403"
    }
  }
}

# Forwarded only when CloudFront's origin header is present.
resource "aws_lb_listener_rule" "from_cloudfront" {
  listener_arn = aws_lb_listener.http.arn
  priority     = 1

  condition {
    http_header {
      http_header_name = local.origin_header
      values           = [random_password.origin_header.result]
    }
  }

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway.arn
  }
}
