# A migration role per schema on the shared cluster (lesson s5-l01). When a module gets a host of its
# own, a one-time hand-over (ServiceHost's `hand-over`, run once as the owner) gives that role the
# schema and every object in it, object by object; from then on the service's migrate step logs in as
# that role and changes its schema without the cluster's owner, and the monolith's migrate step skips
# the schema. The roles are created by the hand-over (with their SCRAM verifier, never the password in
# plain text); their secrets exist from here, read only by their service's migrate and hand-over tasks.
# The master login stays with this layer and bootstrap.
locals {
  migration_roles = {
    notifications = "notifications_migrator"
    campaigns     = "campaigns_migrator"
    pledges       = "pledges_migrator"
  }
}

ephemeral "random_password" "migrator" {
  for_each = local.migration_roles
  length   = 32
  special  = false
}

resource "aws_secretsmanager_secret" "migrator" {
  for_each                = local.migration_roles
  name                    = "${var.project}/sandbox/db/${each.value}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "migrator" {
  for_each                 = local.migration_roles
  secret_id                = aws_secretsmanager_secret.migrator[each.key].id
  secret_string_wo         = "Host=${local.db_host};Port=5432;Database=crowdfunding;Username=${each.value};Password=${ephemeral.random_password.migrator[each.key].result};Timeout=60;${local.db_tls};${local.owner_pool}"
  secret_string_wo_version = 1
}
