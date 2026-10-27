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
# connection without a password can do nothing. The gateway may touch only its own keys, and each
# service only its own prefixes and the command categories it uses (lesson s5-l01).
locals {
  valkey_users = {
    # The monolith, which runs Campaigns, Pledges and Notifications (lesson s5-l01): the catalog cache and
    # its refill locks (campaigns:*), and the display totals and sold-out flags (pledges:*). Since s4-l10
    # it takes no PSP budget token, so buckets:* is not its own any more. Only the categories it uses, and
    # nothing @dangerous (FLUSHALL, KEYS, CONFIG), nor SCAN or RANDOMKEY, which list keys beyond its prefixes.
    api = "on ~campaigns:* ~pledges:* resetchannels -@all +@connection +@read +@write +@scripting -@dangerous -scan -randomkey"
    # The services' users get nothing @dangerous (FLUSHALL, FLUSHDB, KEYS, CONFIG, DEBUG), nor SCAN or RANDOMKEY,
    # which key patterns do not limit (KEYS and SCAN list every service's keys). ValkeyUsersTests runs these
    # strings against a Valkey container (CQA-1516).
    gateway = "on ~gateway:* &* +@all -@dangerous -scan -randomkey"
    # Payments' own host shares the PSP budget's buckets (buckets:<Payments:Budget:Prefix>:*) with the
    # monolith, and has its own flags (payments:*). Only the categories it uses: the connection's own
    # commands (PING, ECHO, CLIENT SETNAME, sent while StackExchange.Redis connects), reads, writes and
    # scripts, no channels; the buckets' script reads Valkey's clock, and TIME is in no category but
    # @fast, so it is named alone.
    # The Campaigns service (s5-l08): the catalog cache and its refill locks, nothing else. The monolith keeps
    # ~campaigns:* while it may still run Campaigns (campaigns_mode), until s5-l09.
    campaigns = "on ~campaigns:* resetchannels -@all +@connection +@read +@write +@scripting -@dangerous -scan -randomkey"
    payments  = "on ~buckets:psp:* ~payments:* resetchannels -@all +@connection +@read +@write +@scripting +time -@dangerous -scan -randomkey"
  }

  # A user limited to its own keys may not read the client's tiebreaker key or subscribe to its
  # configuration channel, so its connection string turns both off. The gateway keeps the client's
  # defaults.
  valkey_restricted_users   = toset(["api", "payments", "campaigns"])
  valkey_restricted_options = ",tiebreaker=,configChannel="
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
  secret_string_wo         = "${aws_elasticache_replication_group.valkey.primary_endpoint_address}:6379,ssl=true,abortConnect=false,user=${each.key},password=${ephemeral.random_password.valkey[each.key].result}${contains(local.valkey_restricted_users, each.key) ? local.valkey_restricted_options : ""}"
  secret_string_wo_version = 1
}
