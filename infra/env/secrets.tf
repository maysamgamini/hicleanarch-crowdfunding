# Connection strings, one per database role. The owner's is used only by the migration task;
# each module's role logs in with its own. Secrets are deleted at once on destroy, so the next
# apply can reuse their names.
#
# The passwords are ephemeral: generated during the apply and sent only to write-only arguments
# (master_password_wo, secret_string_wo), so they never reach the state file. A write-only value is
# sent again only when its version changes. The migration task creates each module's role with the
# password in its secret, once, so a new version here needs an ALTER ROLE as well. The hosts in the
# strings keep their names if the cluster is replaced (identifier plus the account's own suffix).

locals {
  # Module key: its login role.
  db_roles = {
    campaigns     = "campaigns_app"
    pledges       = "pledges_app"
    pledges_jobs  = "pledges_jobs" # the close and the totals aggregator: no two-second transaction limit
    payments      = "payments_app"
    notifications = "notifications_app"
  }
}

ephemeral "random_password" "owner" {
  length  = 32
  special = false
}

ephemeral "random_password" "db" {
  for_each = local.db_roles
  length   = 32
  special  = false
}

# The fake PSP signs its webhooks with this; a real PSP's signing secret would replace it.
ephemeral "random_password" "webhook" {
  length  = 40
  special = false
}

resource "aws_secretsmanager_secret" "webhook" {
  name                    = "${var.project}/sandbox/psp/webhook-secret"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "webhook" {
  secret_id                = aws_secretsmanager_secret.webhook.id
  secret_string_wo         = ephemeral.random_password.webhook.result
  secret_string_wo_version = 1
}

locals {
  db_host = aws_rds_cluster.main.endpoint
  # TLS with the server's certificate and host name checked against the RDS CA bundle, which the
  # image carries (see the Dockerfile). Npgsql's default, Prefer, encrypts but checks nothing.
  db_tls = "SSL Mode=VerifyFull;Root Certificate=/usr/local/share/rds/global-bundle.pem"
  # Every pool has a cap, so the connections add up to a known number: see the budget in README.md.
  pool       = "Maximum Pool Size=${local.module_pool_size}"
  owner_pool = "Maximum Pool Size=${local.owner_pool_size}"

  connection_strings = merge(
    { owner = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=owner;Password=${ephemeral.random_password.owner.result};Timeout=60;${local.db_tls};${local.owner_pool}" },
    { for key, role in local.db_roles : key => "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=${role};Password=${ephemeral.random_password.db[key].result};Timeout=60;${local.db_tls};${local.pool}" },
  )
}

resource "aws_secretsmanager_secret" "connection" {
  for_each                = toset(concat(["owner"], keys(local.db_roles)))
  name                    = "${var.project}/sandbox/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "connection" {
  for_each                 = aws_secretsmanager_secret.connection
  secret_id                = each.value.id
  secret_string_wo         = local.connection_strings[each.key]
  secret_string_wo_version = 1
}
