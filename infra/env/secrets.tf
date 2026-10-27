# Connection strings, one per database role. The owner's is used only by the migration task;
# each module's role logs in with its own. Secrets are deleted at once on destroy, so the next
# apply can reuse their names.

resource "random_password" "campaigns" {
  length  = 32
  special = false
}

resource "random_password" "pledges" {
  length  = 32
  special = false
}

locals {
  db_host = aws_rds_cluster.main.endpoint
  connection_strings = {
    owner     = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=owner;Password=${random_password.owner.result};Timeout=60"
    campaigns = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=campaigns_app;Password=${random_password.campaigns.result};Timeout=60"
    pledges   = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=pledges_app;Password=${random_password.pledges.result};Timeout=60"
  }
}

resource "aws_secretsmanager_secret" "connection" {
  for_each                = local.connection_strings
  name                    = "${var.project}/sandbox/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "connection" {
  for_each      = local.connection_strings
  secret_id     = aws_secretsmanager_secret.connection[each.key].id
  secret_string = each.value
}
