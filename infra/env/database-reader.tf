# One reader in another Availability Zone. Queries use the cluster's reader endpoint, which spreads
# connections over the readers; Aurora also promotes a reader if the writer fails. In the sandbox it
# is Serverless v2 like the writer, and pauses with it.

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
  read_connection_strings = {
    campaigns_read = "Host=${local.db_reader_host};Port=5432;Database=crowdfunding;Username=campaigns_app;Password=${random_password.campaigns.result};Timeout=60"
    pledges_read   = "Host=${local.db_reader_host};Port=5432;Database=crowdfunding;Username=pledges_app;Password=${random_password.pledges.result};Timeout=60"
  }
}

resource "aws_secretsmanager_secret" "read_connection" {
  for_each                = local.read_connection_strings
  name                    = "${var.project}/sandbox/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "read_connection" {
  for_each      = local.read_connection_strings
  secret_id     = aws_secretsmanager_secret.read_connection[each.key].id
  secret_string = each.value
}
