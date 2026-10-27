# Lesson s4-l05: Payments gets a host of its own, in three sub-steps, each undone by one variable.
#   1. The Payments service runs (its own root, infra/services/payments: desired_count; rollback 0).
#   2. The gateway sends Payments' four public paths to it (payments_routes_to; rollback "monolith").
#   3. The monolith stops running Payments and calls it over HTTP (payments_mode; rollback "InProcess").
# Until the Payments service is deployed (payments_deployed), the gateway and the monolith use the monolith alone.

variable "payments_deployed" {
  description = "Whether the Payments service runs (infra/services/payments, deployed before the monolith's rollout). False: the monolith runs Payments and the gateway sends its paths to the monolith."
  type        = bool
  default     = false
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
  payments_running = var.payments_deployed
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

output "payments_deployed" {
  description = "The next monolith deploy keeps it unless told otherwise."
  value       = var.payments_deployed
}
