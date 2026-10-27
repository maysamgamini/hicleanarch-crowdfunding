# Valkey on ElastiCache for the read cache, the display totals, the shared token buckets and the
# gateway's per-client limits. Reachable only from the tasks; nothing to keep on destroy.
resource "aws_elasticache_subnet_group" "valkey" {
  name       = "${var.project}-valkey"
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_security_group" "valkey" {
  name        = "${var.project}-valkey"
  description = "Valkey from the tasks only"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "valkey_from_tasks" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 6379
  to_port                      = 6379
  ip_protocol                  = "tcp"
}

# Two nodes in two zones: when the primary or its zone fails, ElastiCache promotes the replica and
# points the primary endpoint at it, so the clients reconnect to the same name. The replica doubles
# the cost of this cluster; one node would mean minutes without PSP calls while it is replaced.
resource "aws_elasticache_replication_group" "valkey" {
  replication_group_id        = "${var.project}-valkey"
  description                 = "Read cache, display totals, token buckets and gateway rate limits"
  engine                      = "valkey"
  engine_version              = "8.0"
  node_type                   = "cache.t4g.micro"
  num_cache_clusters          = 2
  preferred_cache_cluster_azs = slice(local.azs, 0, 2)
  automatic_failover_enabled  = true
  multi_az_enabled            = true
  port                        = 6379
  subnet_group_name           = aws_elasticache_subnet_group.valkey.name
  security_group_ids          = [aws_security_group.valkey.id]
  transit_encryption_enabled  = true
  at_rest_encryption_enabled  = true
  user_group_ids              = [aws_elasticache_user_group.valkey.id]
  snapshot_retention_limit    = 0
  apply_immediately           = true
}

# Every client signs in as a user of its own (RBAC); a Valkey user group has no default user, so a
# connection without a password can do nothing. The gateway may touch only its own keys.
locals {
  valkey_users = {
    api     = "on ~* &* +@all"
    gateway = "on ~gateway:* &* +@all"
    # The PSP budget and the card-setup lane (Payments:Budget:Prefix), shared with the monolith's Payments code.
    payments = "on ~psp:* &* +@all"
  }
}

ephemeral "random_password" "valkey" {
  for_each = local.valkey_users
  length   = 32
  special  = false
}

resource "aws_elasticache_user" "valkey" {
  for_each      = local.valkey_users
  user_id       = "${var.project}-${each.key}"
  user_name     = each.key
  engine        = "valkey"
  access_string = each.value

  passwords_wo         = ephemeral.random_password.valkey[each.key].result
  passwords_wo_version = 1
}

resource "aws_elasticache_user_group" "valkey" {
  user_group_id = "${var.project}-valkey"
  engine        = "valkey"
  user_ids      = [for u in aws_elasticache_user.valkey : u.user_id]
}

# StackExchange.Redis configuration strings, kept as secrets because they carry the password; TLS
# because transit encryption is on.
resource "aws_secretsmanager_secret" "valkey" {
  for_each                = local.valkey_users
  name                    = "${var.project}/sandbox/valkey/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "valkey" {
  for_each                 = local.valkey_users
  secret_id                = aws_secretsmanager_secret.valkey[each.key].id
  secret_string_wo         = "${aws_elasticache_replication_group.valkey.primary_endpoint_address}:6379,ssl=true,abortConnect=false,user=${each.key},password=${ephemeral.random_password.valkey[each.key].result}"
  secret_string_wo_version = 1
}
