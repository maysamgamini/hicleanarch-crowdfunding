output "api_url" {
  description = "The load balancer's address."
  value       = "http://${aws_lb.api.dns_name}"
}

output "cluster" {
  value = aws_ecs_cluster.main.name
}

output "service" {
  value = aws_ecs_service.api.name
}

output "migrate_task_definition" {
  value = aws_ecs_task_definition.migrate.arn
}

output "task_subnets" {
  description = "Comma-separated, as aws ecs run-task wants them."
  value       = join(",", aws_subnet.public[*].id)
}

output "task_security_group" {
  value = aws_security_group.tasks.id
}
