# The gateway: its own small service. The public load balancer now sends every request to it,
# and it forwards /v1/** to the monolith through an internal load balancer, which only the
# tasks can reach. Moving a module out later is a new route in the gateway's configuration.

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

# The internal load balancer in front of the monolith. Its address resolves to private IPs, so
# the gateway's traffic stays inside the VPC and its security group can name the tasks.
resource "aws_security_group" "internal_alb" {
  name        = "${var.project}-internal-alb"
  description = "HTTP from the gateway tasks to the internal load balancer"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "internal_alb_from_tasks" {
  security_group_id            = aws_security_group.internal_alb.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 80
  to_port                      = 80
  ip_protocol                  = "tcp"
}

resource "aws_vpc_security_group_egress_rule" "internal_alb_to_tasks" {
  security_group_id            = aws_security_group.internal_alb.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 8080
  to_port                      = 8080
  ip_protocol                  = "tcp"
}

resource "aws_vpc_security_group_ingress_rule" "tasks_from_internal_alb" {
  security_group_id            = aws_security_group.tasks.id
  referenced_security_group_id = aws_security_group.internal_alb.id
  from_port                    = 8080
  to_port                      = 8080
  ip_protocol                  = "tcp"
}

resource "aws_lb" "internal" {
  name               = "${var.project}-internal"
  internal           = true
  load_balancer_type = "application"
  subnets            = aws_subnet.public[*].id
  security_groups    = [aws_security_group.internal_alb.id]
}

resource "aws_lb_listener" "internal" {
  load_balancer_arn = aws_lb.internal.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

resource "aws_cloudwatch_log_group" "gateway" {
  name              = "/${var.project}/sandbox/gateway"
  retention_in_days = 1
}

resource "aws_ecs_task_definition" "gateway" {
  family                   = "${var.project}-gateway"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  container_definitions = jsonencode([{
    name         = "gateway"
    image        = var.gateway_image
    essential    = true
    portMappings = [{ containerPort = 8080, protocol = "tcp" }]
    environment = [
      { name = "ReverseProxy__Clusters__monolith__Destinations__monolith__Address", value = "http://${aws_lb.internal.dns_name}/" },
      { name = "EdgeLimits__Valkey", value = local.valkey_configuration },
      { name = "EdgeLimits__GatewayTasks", value = tostring(var.gateway_count) },
      { name = "EdgeLimits__PerSecond", value = tostring(var.edge_limit_per_second) },
      { name = "EdgeLimits__Burst", value = tostring(var.edge_limit_per_second * 2) },
      # X-Forwarded-For is trusted only from the load balancer, inside the VPC.
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
      # A caller from CloudFront's edges is limited by the viewer's address, not the edge's.
      { name = "EdgeLimits__CloudFrontNetworks", value = join(",", [for e in data.aws_ec2_managed_prefix_list.cloudfront.entries : e.cidr]) },
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

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
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

  depends_on = [aws_lb_listener.http]
}

output "gateway_service" {
  value = aws_ecs_service.gateway.name
}

# CloudFront's origin-facing addresses, as AWS publishes them. The gateway reads the viewer's address
# (CloudFront-Viewer-Address) only from a caller in this list.
data "aws_ec2_managed_prefix_list" "cloudfront" {
  name = "com.amazonaws.global.cloudfront.origin-facing"
}
