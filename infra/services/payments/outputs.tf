output "cluster" {
  value = module.service.cluster
}

output "service" {
  value = module.service.service
}

output "service_image" {
  description = "What the Payments service runs; the next deploy keeps it until its migration has run."
  value       = module.service.service_image
}

output "migrate_task_definition" {
  value = module.service.migrate_task_definition
}

output "task_subnets" {
  description = "Comma-separated, as aws ecs run-task wants them."
  value       = module.service.task_subnets
}

output "task_security_group" {
  value = module.service.task_security_group
}

# Lesson s5-l13: its objectives' pages and dashboard, and the windows the tests evaluate.
output "signals" {
  value = {
    pages        = module.service.page_alarm_names
    dashboard    = module.service.signals_dashboard
    burn_windows = module.service.burn_windows
  }
}

output "service_task_definition" {
  description = "The task definition the service runs; the next deploy's first apply keeps it (CQA-2103)."
  value       = module.service.service_task_definition
}

output "release_secret_arns" {
  description = "This release's own secrets; the next deploy keeps them on the execution role for one more release (CQA-2124)."
  value       = module.service.release_secret_arns
}
