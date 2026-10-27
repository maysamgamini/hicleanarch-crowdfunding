# Lesson s4-l05 split Payments off in three sub-steps, each undone by one variable: the service ran,
# the gateway sent it Payments' public paths, the monolith stopped running Payments. Since s4-l10 there
# is nothing to undo them to: Payments runs only in its own service (infra/services/payments), the
# gateway always sends it Payments' four public paths, and the sandbox deploys it before the monolith.

locals {
  # Through Service Connect.
  payments_public_address = "http://payments:8080/"
  # The PSP budget's bucket names in Valkey (Payments:Budget:Prefix), for Payments' tasks alone.
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
