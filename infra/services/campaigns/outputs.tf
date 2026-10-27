output "cluster" {
  value = module.service.cluster
}

output "service" {
  value = module.service.service
}

output "service_image" {
  description = "What the service runs; the next deploy keeps it until its migration has run."
  value       = module.service.service_image
}

output "migrate_task_definition" {
  value = module.service.migrate_task_definition
}

output "task_subnets" {
  value = module.service.task_subnets
}

output "task_security_group" {
  value = module.service.task_security_group
}
