# Lesson s4-l05: Payments gets a host of its own, in three sub-steps, each undone by one variable.
#   1. The Payments service runs (payments_count; rollback 0).
#   2. The gateway sends Payments' four public paths to it (payments_routes_to; rollback "monolith").
#   3. The monolith stops running Payments and calls it over HTTP (payments_mode; rollback "InProcess").
# Without a Payments image or with no task, the gateway and the monolith fall back to the monolith alone.

variable "payments_count" {
  description = "How many Payments tasks run. 0 is the first sub-step's rollback."
  type        = number
  default     = 2

  validation {
    condition     = var.payments_count >= 0
    error_message = "payments_count cannot be negative."
  }
}

variable "payments_routes_to" {
  description = "Where the gateway sends Payments' public paths: \"payments\" (the service) or \"monolith\" (the second sub-step's rollback)."
  type        = string
  default     = "payments"

  validation {
    condition     = contains(["payments", "monolith"], var.payments_routes_to)
    error_message = "payments_routes_to is \"payments\" or \"monolith\"."
  }
}

variable "payments_mode" {
  description = "Modules:Payments:Mode of the monolith: \"Remote\" (Payments runs in its service) or \"InProcess\" (the third sub-step's rollback)."
  type        = string
  default     = "Remote"

  validation {
    condition     = contains(["Remote", "InProcess"], var.payments_mode)
    error_message = "payments_mode is \"Remote\" or \"InProcess\"."
  }
}

locals {
  payments_running = var.payments_image != "" && var.payments_count > 0
  payments_mode    = local.payments_running ? var.payments_mode : "InProcess"
  # Through Service Connect, or back to the monolith behind the internal load balancer.
  payments_public_address = local.payments_running && var.payments_routes_to == "payments" ? "http://payments:8080/" : "http://${aws_lb.internal.dns_name}/"
  # One budget at the PSP while both hosts run Payments' code: the same bucket names in the same Valkey.
  psp_budget_prefix = "psp"
}

# The gateway's own security group: the one shared task group of Release 1 is split into the gateway's,
# the monolith's (aws_security_group.tasks) and Payments'.
resource "aws_security_group" "gateway" {
  name        = "${var.project}-sandbox-gateway"
  description = "The gateway: requests only from the public load balancer"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "gateway_from_alb" {
  security_group_id            = aws_security_group.gateway.id
  referenced_security_group_id = aws_security_group.alb.id
  from_port                    = 8080
  to_port                      = 8080
  ip_protocol                  = "tcp"
}

# Out: the internal load balancer, Payments, Valkey, and the AWS APIs (ECR, logs, Secrets Manager).
resource "aws_vpc_security_group_egress_rule" "gateway_out" {
  security_group_id = aws_security_group.gateway.id
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
}

resource "aws_vpc_security_group_ingress_rule" "valkey_from_gateway" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.gateway.id
  from_port                    = 6379
  to_port                      = 6379
  ip_protocol                  = "tcp"
}

# The Payments service. Service Connect makes it a server under two names on two ports: payments:8080
# for its public endpoints (the gateway's routes) and payments:8081 for its internal one (the monolith's
# command). The proxy in each client task waits at most 2 s for a reply on 8081, the remote client's
# attempt (lesson s4-a04's time budget; the default is 15 s). It retries a failed connection twice on
# another task: AWS manages that and it cannot be turned off; the card setup is idempotent per pledge.
# No load balancer: nothing outside the VPC reaches it except through the gateway.
resource "aws_ecs_service" "payments" {
  count           = var.payments_image == "" ? 0 : 1
  name            = "payments"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.payments[0].arn
  desired_count   = var.payments_count
  launch_type     = "FARGATE"
  # ECS moves tasks back into a zone that lost them, once it recovers.
  availability_zone_rebalancing = "ENABLED"

  # Public subnets with public IPs and no inbound rule from the internet, as the other services:
  # private subnets would need a NAT gateway or interface endpoints, at more an hour than the tasks.
  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.payments.id]
    assign_public_ip = true
  }

  service_connect_configuration {
    enabled   = true
    namespace = aws_service_discovery_http_namespace.services.arn

    service {
      port_name      = "public"
      discovery_name = "payments-public"
      client_alias {
        dns_name = "payments"
        port     = 8080
      }
    }

    service {
      port_name      = "internal"
      discovery_name = "payments-internal"
      client_alias {
        dns_name = "payments"
        port     = 8081
      }
      timeout {
        per_request_timeout_seconds = 2
      }
    }

    log_configuration {
      log_driver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.payments.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "service-connect"
      }
    }
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }
}

output "payments_service" {
  value = one(aws_ecs_service.payments[*].name)
}

resource "aws_cloudwatch_metric_alarm" "payments_running_tasks" {
  count               = local.payments_running ? 1 : 0
  alarm_name          = "${var.project}-sandbox-payments_running_tasks"
  alarm_description   = "The payments service runs fewer tasks than its ${var.payments_count}: tasks are crashing or cannot start."
  namespace           = "ECS/ContainerInsights"
  metric_name         = "RunningTaskCount"
  dimensions          = { ClusterName = aws_ecs_cluster.main.name, ServiceName = "payments" }
  statistic           = "Minimum"
  period              = 60
  evaluation_periods  = 3
  comparison_operator = "LessThanThreshold"
  threshold           = var.payments_count
  treat_missing_data  = "breaching"
  alarm_actions       = [aws_sns_topic.alarms.arn]
  ok_actions          = [aws_sns_topic.alarms.arn]
}

# The sandbox workflow keeps the running image through migrate, as for the monolith's service_image.
output "payments_image" {
  value = var.payments_image
}
