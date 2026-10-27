# Valkey on ElastiCache: one small node for the read cache, the display totals, the shared token
# buckets and the gateway's per-client limits. Reachable only from the tasks; nothing to keep on destroy.
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

resource "aws_elasticache_replication_group" "valkey" {
  replication_group_id       = "${var.project}-valkey"
  description                = "Read cache, display totals, token buckets and gateway rate limits"
  engine                     = "valkey"
  engine_version             = "8.0"
  node_type                  = "cache.t4g.micro"
  num_cache_clusters         = 1
  port                       = 6379
  subnet_group_name          = aws_elasticache_subnet_group.valkey.name
  security_group_ids         = [aws_security_group.valkey.id]
  transit_encryption_enabled = true
  at_rest_encryption_enabled = true
  snapshot_retention_limit   = 0
  apply_immediately          = true
}

locals {
  # A StackExchange.Redis configuration string; TLS because transit encryption is on.
  valkey_configuration = "${aws_elasticache_replication_group.valkey.primary_endpoint_address}:6379,ssl=true,abortConnect=false"
}
