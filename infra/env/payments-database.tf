# Payments' own Aurora cluster (lesson s4-l07, fence, clone, switch). It is a copy-on-write clone of
# the shared cluster, taken after `move-payments fence`, once the shared cluster's latest restorable
# time is past the fence (scripts/move-payments.sh waits for it). The clone starts with every
# module's data: `move-payments prune` drops all but Payments', and the write-only master password
# below replaces the copied one. Nothing here exists until payments_database_clone is true.
#
# payments_database_clone = false, payments_database = "shared"  before the move, and after a rollback
# payments_database_clone = true,  payments_database = "shared"  the clone, during the pause
# payments_database_clone = true,  payments_database = "own"     the switch: Payments' tasks roll onto the new secret

variable "payments_database_clone" {
  description = "Whether Payments' own cluster (the clone of the shared one) exists."
  type        = bool
  default     = false
}

variable "payments_database" {
  description = "Which cluster Payments' tasks connect to: shared, or own (the clone, once switched)."
  type        = string
  default     = "shared"

  validation {
    condition     = contains(["shared", "own"], var.payments_database)
    error_message = "payments_database is shared or own."
  }

  validation {
    condition     = var.payments_database == "shared" || var.payments_database_clone
    error_message = "payments_database = own needs payments_database_clone = true."
  }
}

locals {
  payments_clone = var.payments_database_clone ? 1 : 0
  # The one secret the switch changes: what ConnectionStrings__Payments reads in Payments' task (ecs.tf).
  payments_connection_secret = var.payments_database == "own" ? aws_secretsmanager_secret.payments_own["payments"].arn : aws_secretsmanager_secret.connection["payments"].arn
}

# Its own parameter group, so a change to Payments' settings never reboots the shared writer.
resource "aws_rds_cluster_parameter_group" "payments" {
  name        = "${var.project}-sandbox-payments"
  family      = "aurora-postgresql${split(".", var.aurora_engine_version)[0]}"
  description = "Payments' own cluster"
}

# Only Payments reaches it: the tasks that run Payments, and the task that runs move-payments.
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
  description                  = "PostgreSQL from Payments' tasks and the move-payments task"
}

resource "aws_vpc_security_group_ingress_rule" "payments_database_from_tasks" {
  security_group_id            = aws_security_group.payments_database.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
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
  count              = local.payments_clone
  cluster_identifier = "${var.project}-sandbox-payments"
  engine             = aws_rds_cluster.main.engine
  engine_mode        = "provisioned"
  engine_version     = aws_rds_cluster.main.engine_version

  # Copy-on-write: the clone shares the shared cluster's pages and copies one only when either side
  # changes it, so its making does not grow with the money journals. The master user is the source's.
  restore_to_point_in_time {
    source_cluster_identifier  = aws_rds_cluster.main.cluster_identifier
    restore_type               = "copy-on-write"
    use_latest_restorable_time = true
  }

  # The copied master password goes: this one is sent to RDS and to the owner's secret, never to the state.
  master_password_wo              = ephemeral.random_password.payments_owner.result
  master_password_wo_version      = 1
  db_subnet_group_name            = aws_db_subnet_group.main.name
  db_cluster_parameter_group_name = aws_rds_cluster_parameter_group.payments.name
  vpc_security_group_ids          = [aws_security_group.payments_database.id]
  storage_encrypted               = true

  deletion_protection = false
  skip_final_snapshot = true
  apply_immediately   = true

  # Never paused: the settlement workers poll it every few seconds anyway, and a resume (15 s, or 30 s
  # and more after a day) would be longer than the card-setup call's 5-second budget.
  serverlessv2_scaling_configuration {
    min_capacity = 0.5
    max_capacity = local.db_max_acu
  }
}

resource "aws_rds_cluster_instance" "payments_writer" {
  count               = local.payments_clone
  identifier          = "${var.project}-sandbox-payments-writer"
  cluster_identifier  = aws_rds_cluster.payments[0].id
  instance_class      = "db.serverless"
  engine              = aws_rds_cluster.payments[0].engine
  engine_version      = aws_rds_cluster.payments[0].engine_version
  apply_immediately   = true
  publicly_accessible = false
}

locals {
  payments_db_host = var.payments_database_clone ? aws_rds_cluster.payments[0].endpoint : ""
  payments_own_connections = {
    owner    = "Host=${local.payments_db_host};Port=5432;Database=crowdfunding;Username=owner;Password=${ephemeral.random_password.payments_owner.result};Timeout=60;${local.db_tls};${local.owner_pool}"
    payments = "Host=${local.payments_db_host};Port=5432;Database=crowdfunding;Username=${local.db_roles.payments};Password=${ephemeral.random_password.payments_app.result};Timeout=60;${local.db_tls};${local.pool}"
  }
}

# The clone's two logins: its master (move-payments, and Payments' own migrate step from then on),
# and Payments' role with the password that `move-payments switch` gives it on the clone.
resource "aws_secretsmanager_secret" "payments_own" {
  for_each                = var.payments_database_clone ? toset(["owner", "payments"]) : toset([])
  name                    = "${var.project}/sandbox/db/payments-own/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "payments_own" {
  for_each                 = aws_secretsmanager_secret.payments_own
  secret_id                = each.value.id
  secret_string_wo         = local.payments_own_connections[each.key]
  secret_string_wo_version = 1
}

data "aws_iam_policy_document" "read_payments_own" {
  count = local.payments_clone
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [for s in aws_secretsmanager_secret.payments_own : s.arn]
  }
}

resource "aws_iam_role_policy" "execution_payments_own" {
  count  = local.payments_clone
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.read_payments_own[0].json
}

# The move-payments task is Payments' own (infra/services/payments/move.tf): it runs Payments' image
# in Payments' security group, with an execution role that reads the secrets below.

output "payments_security_group" {
  description = "Payments' tasks' security group: the move-payments task runs in it."
  value       = aws_security_group.payments.id
}

output "payments_database_endpoint" {
  value = local.payments_db_host
}

output "shared_cluster_identifier" {
  value = aws_rds_cluster.main.cluster_identifier
}

output "payments_cluster_identifier" {
  value = "${var.project}-sandbox-payments"
}

output "payments_own_secret_prefix" {
  value = "${var.project}/sandbox/db/payments-own"
}
