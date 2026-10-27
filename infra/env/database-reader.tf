# One reader in another Availability Zone. Queries use the cluster's reader endpoint, which spreads
# new connections over the readers, not queries; Aurora also promotes a reader if the writer fails.
# In the sandbox it is Serverless v2 like the writer, and pauses with it.

resource "aws_rds_cluster_instance" "reader" {
  count               = var.reader_count
  identifier          = "${var.project}-sandbox-reader-${count.index}"
  cluster_identifier  = aws_rds_cluster.main.id
  instance_class      = "db.serverless"
  engine              = aws_rds_cluster.main.engine
  engine_version      = aws_rds_cluster.main.engine_version
  promotion_tier      = 1
  apply_immediately   = true
  publicly_accessible = false

  # The writer first, so the first instance created is the one Aurora makes the writer.
  depends_on = [aws_rds_cluster_instance.writer]
}

locals {
  # Without a reader the endpoint still answers, from the writer.
  db_reader_host = aws_rds_cluster.main.reader_endpoint
  # A pooled connection stays on the reader it first reached. Replacing each one after five minutes
  # spreads the pools over readers added later, and moves them off an instance a failover made the writer.
  read_options = "Timeout=60;${local.db_tls};Maximum Pool Size=${local.read_pool_size};Connection Lifetime=300"
  read_connection_strings = {
    campaigns_read = "Host=${local.db_reader_host};Port=5432;Database=crowdfunding;Username=${local.db_roles.campaigns};Password=${ephemeral.random_password.db["campaigns"].result};${local.read_options}"
    pledges_read   = "Host=${local.db_reader_host};Port=5432;Database=crowdfunding;Username=${local.db_roles.pledges};Password=${ephemeral.random_password.db["pledges"].result};${local.read_options}"
  }
}

resource "aws_secretsmanager_secret" "read_connection" {
  for_each                = toset(["campaigns_read", "pledges_read"])
  name                    = "${var.project}/sandbox/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "read_connection" {
  for_each                 = aws_secretsmanager_secret.read_connection
  secret_id                = each.value.id
  secret_string_wo         = local.read_connection_strings[each.key]
  secret_string_wo_version = 1
}
