# The gateway: its own small service. The public load balancer sends every request to it, and it sends
# each path to its service through Service Connect: Payments', Notifications' and Campaigns' paths to
# them, everything else (/v1/**) to the Pledges service, the old monolith's host (s5-l10).

variable "gateway_image" {
  description = "The gateway image, as repository URL and tag."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "edge_limit_per_second" {
  description = "Requests a second one client may make through the gateway. A load test sends everything from one machine, so the sandbox workflow raises it for a load run."
  type        = number
  default     = 20
}

variable "psp_webhook_sources" {
  description = "The PSP's published webhook source addresses, as CIDRs (for Stripe, each address of https://stripe.com/files/ips/ips_webhooks.txt as a /32). They share the webhook route's limit; every other address is limited per address there too. Empty: every caller is limited per address, the PSP included (CQA-1517)."
  type        = list(string)
  default     = []
}

variable "gateway_count" {
  description = "How many gateway tasks run. The gateway splits its limits by this number if Valkey is down."
  type        = number
  default     = 2
}

resource "aws_lb_target_group" "gateway" {
  name                 = "${var.project}-gateway"
  port                 = 8080
  protocol             = "HTTP"
  target_type          = "ip"
  vpc_id               = aws_vpc.main.id
  deregistration_delay = 15

  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 10
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_cloudwatch_log_group" "gateway" {
  name              = "/${var.project}/sandbox/gateway"
  retention_in_days = 1
}

# CQA-2106: ECS adds a Service Connect proxy to every task of the service. AWS recommends 256 more
# CPU units and at least 64 MiB for it ("Amazon ECS Service Connect components", Proxy resources);
# the gateway had 256 units and 0.5 GB, so it takes Fargate's next size, 0.5 vCPU and 1 GB.
resource "aws_ecs_task_definition" "gateway" {
  family                   = "${var.project}-gateway"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  container_definitions = jsonencode([{
    name         = "gateway"
    image        = var.gateway_image
    essential    = true
    portMappings = [{ containerPort = 8080, protocol = "tcp" }]
    environment = [
      # The catch-all: the Pledges service through Service Connect (s5-l10; the monolith's internal load balancer before).
      { name = "ReverseProxy__Clusters__pledges__Destinations__pledges__Address", value = local.pledges_public_address },
      # Payments' four public paths: the Payments service through Service Connect, or the monolith (payments-split.tf).
      { name = "ReverseProxy__Clusters__payments__Destinations__payments__Address", value = local.payments_public_address },
      # A user's notification preference: the Notifications service through Service Connect (s5-l03), once
      # it is deployed (notifications_serves_preferences); the monolith before that.
      { name = "ReverseProxy__Clusters__notifications__Destinations__notifications__Address", value = local.notifications_public_address },
      # Campaigns' paths: the Campaigns service through Service Connect once it serves them (s5-l08); the monolith before that.
      { name = "ReverseProxy__Clusters__campaigns__Destinations__campaigns__Address", value = local.campaigns_public_address },
      { name = "EdgeLimits__GatewayTasks", value = tostring(var.gateway_count) },
      { name = "EdgeLimits__PerSecond", value = tostring(var.edge_limit_per_second) },
      { name = "EdgeLimits__Burst", value = tostring(var.edge_limit_per_second * 2) },
      # The PSP's webhook: one limit for the route for the PSP's own addresses (CQA-1306), the per-address
      # limit for anyone else (CQA-1517).
      { name = "EdgeLimits__Webhook__PerSecond", value = "1000" },
      { name = "EdgeLimits__Webhook__Burst", value = "1000" },
      { name = "EdgeLimits__Webhook__Sources", value = join(",", var.psp_webhook_sources) },
      # X-Forwarded-For is trusted only from the load balancer, inside the VPC.
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
      # A caller from CloudFront's edges is limited by the viewer's address, not the edge's.
      { name = "EdgeLimits__CloudFrontNetworks", value = join(",", [for e in data.aws_ec2_managed_prefix_list.cloudfront.entries : e.cidr]) },
    ]
    secrets = [
      { name = "EdgeLimits__Valkey", valueFrom = aws_secretsmanager_secret.valkey["gateway"].arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.gateway.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "gateway"
      }
    }
  }])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

resource "aws_ecs_service" "gateway" {
  name            = "gateway"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.gateway.arn
  desired_count   = var.gateway_count
  launch_type     = "FARGATE"
  # ECS moves tasks back into a zone that lost them, once it recovers.
  availability_zone_rebalancing = "ENABLED"

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.gateway.id]
    assign_public_ip = true
  }

  # A client of Service Connect: payments:8080, notifications:8080, campaigns:8080 and pledges:8080
  # resolve to those services' tasks.
  service_connect_configuration {
    enabled   = true
    namespace = aws_service_discovery_http_namespace.services.arn
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.gateway.arn
    container_name   = "gateway"
    container_port   = 8080
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  depends_on = [aws_lb_listener_rule.from_cloudfront]
}

output "gateway_service" {
  value = aws_ecs_service.gateway.name
}

# CloudFront's origin-facing addresses, as AWS publishes them. The gateway reads the viewer's address
# (CloudFront-Viewer-Address) only from a caller in this list.
data "aws_ec2_managed_prefix_list" "cloudfront" {
  name = "com.amazonaws.global.cloudfront.origin-facing"
}
