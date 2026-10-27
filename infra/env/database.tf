# Aurora PostgreSQL Serverless v2 at its smallest: it pauses at 0 capacity units after five minutes
# with no connection and resumes on the next one. While any task runs it never pauses: every task's
# jobs ask the database every few seconds (settlement every 5 s, the hold sweeper every 10 s, the
# close every 30 s, the outbox sweep, the monitors and the totals' drift pass every minute). It
# pauses once no task runs: after the API service is removed (an apply with service_image empty)
# or the environment destroyed. Everything is set so that destroy is complete.

resource "aws_db_subnet_group" "main" {
  name       = "${var.project}-sandbox"
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_rds_cluster" "main" {
  cluster_identifier = "${var.project}-sandbox"
  engine             = "aurora-postgresql"
  engine_mode        = "provisioned"
  engine_version     = var.aurora_engine_version
  database_name      = "crowdfunding"
  master_username    = "owner"
  # Write-only: the password goes to RDS and to the owner's secret, never to the state (secrets.tf).
  master_password_wo         = ephemeral.random_password.owner.result
  master_password_wo_version = 1
  db_subnet_group_name       = aws_db_subnet_group.main.name
  vpc_security_group_ids     = [aws_security_group.database.id]
  storage_encrypted          = true

  deletion_protection = false
  skip_final_snapshot = true
  apply_immediately   = true

  serverlessv2_scaling_configuration {
    min_capacity             = 0
    max_capacity             = local.db_max_acu
    seconds_until_auto_pause = 300
  }
}

resource "aws_rds_cluster_instance" "writer" {
  identifier          = "${var.project}-sandbox-writer"
  cluster_identifier  = aws_rds_cluster.main.id
  instance_class      = "db.serverless"
  engine              = aws_rds_cluster.main.engine
  engine_version      = aws_rds_cluster.main.engine_version
  apply_immediately   = true
  publicly_accessible = false
}

locals {
  db_max_acu = 2
  # Serverless v2 fixes max_connections from the maximum ACU: AWS's table gives 189 at 1 ACU and 823
  # at 4, so about 400 at 2 (SHOW max_connections gives the exact number). Raise this with db_max_acu.
  db_max_connections = 400
  # Kept for the rds_superuser and the reserved superuser slots.
  db_reserved_connections = 5

  module_pool_size = 10 # each of the five module roles, per task, on the writer
  read_pool_size   = 5  # each of the two read roles, per task, on the reader endpoint
  owner_pool_size  = 2  # the migration task runs one statement at a time

  # The most connections the program can open on the writer: every task at max_count with full pools,
  # plus the migration task. With no reader, the read pools land on the writer too.
  db_writer_connections = var.max_count * (length(local.db_roles) * local.module_pool_size + (var.reader_count == 0 ? 2 * local.read_pool_size : 0)) + local.owner_pool_size + length(local.db_roles)
}
