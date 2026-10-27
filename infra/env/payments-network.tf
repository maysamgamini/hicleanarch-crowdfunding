# Payments' place in the shared network: its Service Connect namespace and security group, and the
# rules that let it reach the database and Valkey. The network has one owner, this layer; everything
# else of the Payments service (its roles, task definitions, ECS service and log group) is its own
# OpenTofu root, infra/services/payments, deployed by its own role (s4-l09). Until s4-l10 the
# monolith keeps its own access for its InProcess fallback.

# Service Connect: services find each other by name (payments:8080, payments:8081) inside this namespace.
resource "aws_service_discovery_http_namespace" "services" {
  name        = "${var.project}-sandbox"
  description = "Service Connect names for the monolith, the gateway and Payments."
}

resource "aws_security_group" "payments" {
  name   = "${var.project}-sandbox-payments"
  vpc_id = aws_vpc.main.id
}

# 8080 (public endpoints) from the gateway only; 8081 (the internal command) from the monolith only.
resource "aws_vpc_security_group_ingress_rule" "payments_public_from_gateway" {
  security_group_id            = aws_security_group.payments.id
  referenced_security_group_id = aws_security_group.gateway.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
}

resource "aws_vpc_security_group_ingress_rule" "payments_internal_from_monolith" {
  security_group_id            = aws_security_group.payments.id
  referenced_security_group_id = aws_security_group.tasks.id
  ip_protocol                  = "tcp"
  from_port                    = 8081
  to_port                      = 8081
}

# Out: the PSP, AWS APIs, the database and Valkey.
resource "aws_vpc_security_group_egress_rule" "payments_out" {
  security_group_id = aws_security_group.payments.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "database_from_payments" {
  security_group_id            = aws_security_group.database.id
  referenced_security_group_id = aws_security_group.payments.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_vpc_security_group_ingress_rule" "valkey_from_payments" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.payments.id
  ip_protocol                  = "tcp"
  from_port                    = 6379
  to_port                      = 6379
}
