output "cluster" {
  value = var.shared.cluster_name
}

output "service" {
  value = var.name
}

output "service_image" {
  description = "What the service runs; the next deploy keeps it until its migration has run."
  value       = var.service_image
}

output "migrate_task_definition" {
  value = aws_ecs_task_definition.migrate.arn
}

output "task_subnets" {
  description = "Comma-separated, as aws ecs run-task wants them."
  value       = join(",", var.shared.task_subnet_ids)
}

output "task_security_group" {
  value = var.security_group_id
}

# What the roots' tests check.
output "secret_arns" {
  description = "The secrets the service's execution role may read."
  value       = data.aws_iam_policy_document.secrets.statement[0].resources
}

output "migrate_secret_arns" {
  description = "The secrets the migrate task's execution role may read."
  value       = data.aws_iam_policy_document.migrate_secrets.statement[0].resources
}

output "task_policy" {
  value = data.aws_iam_policy_document.task.json
}

output "container" {
  value = jsondecode(aws_ecs_task_definition.service.container_definitions)[0]
}

output "migrate_container" {
  value = jsondecode(aws_ecs_task_definition.migrate.container_definitions)[0]
}

output "log_group" {
  value = aws_cloudwatch_log_group.service.name
}

output "queue_and_topic" {
  description = "The only queue and topic the task role names."
  value       = compact([var.queue_arn, var.topic_arn])
}
