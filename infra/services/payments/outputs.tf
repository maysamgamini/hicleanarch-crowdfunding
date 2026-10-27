output "cluster" {
  value = local.shared.cluster_name
}

output "service" {
  value = local.service_name
}

output "service_image" {
  description = "What the Payments service runs; the next deploy keeps it until its migration has run."
  value       = var.service_image
}

output "migrate_task_definition" {
  value = aws_ecs_task_definition.migrate.arn
}

output "task_subnets" {
  description = "Comma-separated, as aws ecs run-task wants them."
  value       = join(",", local.shared.task_subnet_ids)
}

output "task_security_group" {
  value = local.shared.payments_security_group_id
}
