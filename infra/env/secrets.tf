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

resource "random_password" "pledges_jobs" {
  length  = 32
  special = false
}

resource "random_password" "payments" {
  length  = 32
  special = false
}

resource "random_password" "notifications" {
  length  = 32
  special = false
}

# The fake PSP signs its webhooks with this; a real PSP's signing secret would replace it.
resource "random_password" "webhook" {
  length  = 40
  special = false
}

resource "aws_secretsmanager_secret" "webhook" {
  name                    = "${var.project}/sandbox/psp/webhook-secret"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "webhook" {
  secret_id     = aws_secretsmanager_secret.webhook.id
  secret_string = random_password.webhook.result
}

locals {
  db_host = aws_rds_cluster.main.endpoint
  # Every task keeps a pool per module role; Aurora at its smallest capacity allows few connections.
  pool = "Maximum Pool Size=10"
  connection_strings = {
    owner     = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=owner;Password=${random_password.owner.result};Timeout=60"
    campaigns = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=campaigns_app;Password=${random_password.campaigns.result};Timeout=60;${local.pool}"
    pledges   = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=pledges_app;Password=${random_password.pledges.result};Timeout=60;${local.pool}"
    # Pledges' jobs (the close, the totals aggregator): the module's rights without its two-second transaction limit.
    pledges_jobs  = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=pledges_jobs;Password=${random_password.pledges_jobs.result};Timeout=60;${local.pool}"
    payments      = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=payments_app;Password=${random_password.payments.result};Timeout=60;${local.pool}"
    notifications = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=notifications_app;Password=${random_password.notifications.result};Timeout=60;${local.pool}"
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
