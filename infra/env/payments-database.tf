# Payments' own Aurora cluster. Lesson s4-l07 made it as a copy-on-write clone of the shared cluster,
# fenced, pruned and switched; since s4-l10 it is simply Payments' cluster, and a new environment creates
# it empty: Payments' own migrate task (infra/services/payments) creates Payments' schema and login on it,
# as the cluster's master. What the move left on the shared cluster (Payments' old schema and its fenced
# login) was dropped a week after the fence by the monolith's drop-moved-payments (CQA-2102); since s5-l10
# the sandbox is rebuilt without the monolith, so nothing is left to drop. No service's role reads the
# secrets below but Payments'.

# CQA-2101: an environment that still runs Payments on the shared cluster must not get here. At s4-l09
# Payments' data is on the shared cluster until s4-l07's move has switched it; this layer would create
# an empty cluster beside it and point Payments at it. The document this layer last handed the services
# (shared-outputs.tf) says where Payments connected: no document is a new environment, and a connection
# secret under db/payments-own/ is Payments' own cluster. scripts/deploy-shared.sh also refuses an
# environment whose state has the shared cluster and no Payments cluster (one never deployed at s4-l09).
data "aws_s3_objects" "previous_shared_outputs" {
  bucket = local.state_bucket
  prefix = "env/shared.json"
}

data "aws_s3_object" "previous_shared_outputs" {
  count  = contains(data.aws_s3_objects.previous_shared_outputs.keys, "env/shared.json") ? 1 : 0
  bucket = local.state_bucket
  key    = "env/shared.json"
}

locals {
  state_bucket            = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}"
  previous_shared_outputs = [for o in data.aws_s3_object.previous_shared_outputs : jsondecode(o.body)]
  payments_moved = alltrue([for o in local.previous_shared_outputs :
  strcontains(lookup(o, "payments_connection_secret_arn", ""), "/db/payments-own/")])
}

locals {
  # What ConnectionStrings__Payments reads in Payments' task: its own cluster, always.
  payments_connection_secret = aws_secretsmanager_secret.payments_own["payments"].arn
  # Payments' login role on its cluster; the shared cluster no longer has one (secrets.tf).
  payments_role = "payments_app"
}

# The clone of s4-l07 lived at count index 0; an environment that went through the move keeps it.
moved {
  from = aws_rds_cluster.payments[0]
  to   = aws_rds_cluster.payments
}

moved {
  from = aws_rds_cluster_instance.payments_writer[0]
  to   = aws_rds_cluster_instance.payments_writer
}

# Its own parameter group, so a change to Payments' settings never reboots the shared writer.
resource "aws_rds_cluster_parameter_group" "payments" {
  name        = "${var.project}-sandbox-payments"
  family      = "aurora-postgresql${split(".", var.aurora_engine_version)[0]}"
  description = "Payments' own cluster"
}

# Only Payments reaches it: its service's tasks and its migrate task, all in Payments' security group.
resource "aws_security_group" "payments_database" {
  name        = "${var.project}-payments-database"
  description = "PostgreSQL for Payments only"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "payments_database_from_payments" {
  security_group_id            = aws_security_group.payments_database.id
  referenced_security_group_id = aws_security_group.payments.id
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
  description                  = "PostgreSQL from Payments' tasks"
}

ephemeral "random_password" "payments_owner" {
  length  = 32
  special = false
}

ephemeral "random_password" "payments_app" {
  length  = 32
  special = false
}

resource "aws_rds_cluster" "payments" {
  cluster_identifier = "${var.project}-sandbox-payments"
  engine             = aws_rds_cluster.main.engine
  engine_mode        = "provisioned"
  engine_version     = aws_rds_cluster.main.engine_version
  database_name      = "crowdfunding"
  master_username    = "owner"

  # Write-only: sent to RDS and to the owner's secret, never to the state.
  master_password_wo              = ephemeral.random_password.payments_owner.result
  master_password_wo_version      = 1
  db_subnet_group_name            = aws_db_subnet_group.main.name
  db_cluster_parameter_group_name = aws_rds_cluster_parameter_group.payments.name
  vpc_security_group_ids          = [aws_security_group.payments_database.id]
  storage_encrypted               = true

  # It holds Payments' only copy of its data (CQA-2100). An apply that would delete it fails at AWS
  # instead, and a deliberate delete leaves a snapshot. The sandbox's teardown turns the protection off
  # and makes way for the new final snapshot first (scripts/release-payments-database.sh).
  deletion_protection       = true
  skip_final_snapshot       = false
  final_snapshot_identifier = "${var.project}-sandbox-payments-final"
  apply_immediately         = true

  # Never paused: the settlement workers poll it every few seconds anyway, and a resume (15 s, or 30 s
  # and more after a day) would be longer than the card-setup call's 5-second budget.
  serverlessv2_scaling_configuration {
    min_capacity = 0.5
    max_capacity = local.db_max_acu
  }

  # A cluster made by s4-l07's clone records where it was restored from; changing that would replace
  # it, with every payment in it. The clone already holds what a new cluster would be created with.
  lifecycle {
    ignore_changes = [restore_to_point_in_time, snapshot_identifier]

    precondition {
      condition     = local.payments_moved
      error_message = "Payments still runs on the shared cluster here (env/shared.json): this apply would give it a new, empty cluster and leave its data behind. Finish s4-l07's move at s4-l09 first (scripts/move-payments.sh, to resume), then deploy this."
    }
  }
}

resource "aws_rds_cluster_instance" "payments_writer" {
  identifier          = "${var.project}-sandbox-payments-writer"
  cluster_identifier  = aws_rds_cluster.payments.id
  instance_class      = "db.serverless"
  engine              = aws_rds_cluster.payments.engine
  engine_version      = aws_rds_cluster.payments.engine_version
  apply_immediately   = true
  publicly_accessible = false
}

locals {
  payments_db_host = aws_rds_cluster.payments.endpoint
  payments_own_connections = {
    owner    = "Host=${local.payments_db_host};Port=5432;Database=crowdfunding;Username=owner;Password=${ephemeral.random_password.payments_owner.result};Timeout=60;${local.db_tls};${local.owner_pool}"
    payments = "Host=${local.payments_db_host};Port=5432;Database=crowdfunding;Username=${local.payments_role};Password=${ephemeral.random_password.payments_app.result};Timeout=60;${local.db_tls};${local.pool}"
  }
}

# The cluster's two logins: its master, read only by Payments' migrate task, and Payments' role, which
# that task creates with this password (an environment that went through s4-l07 got it at the switch).
resource "aws_secretsmanager_secret" "payments_own" {
  for_each                = toset(["owner", "payments"])
  name                    = "${var.project}/sandbox/db/payments-own/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "payments_own" {
  for_each                 = aws_secretsmanager_secret.payments_own
  secret_id                = each.value.id
  secret_string_wo         = local.payments_own_connections[each.key]
  secret_string_wo_version = 1
}

output "payments_security_group" {
  description = "Payments' tasks' security group: its service and its migrate task run in it."
  value       = aws_security_group.payments.id
}

output "payments_database_endpoint" {
  value = local.payments_db_host
}

output "payments_cluster_identifier" {
  value = aws_rds_cluster.payments.cluster_identifier
}
