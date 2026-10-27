output "api_url" {
  description = "Where clients call the API: CloudFront, over HTTPS."
  value       = "https://${aws_cloudfront_distribution.public.domain_name}"
}

output "cluster" {
  value = aws_ecs_cluster.main.name
}

output "task_subnets" {
  description = "Comma-separated, as aws ecs run-task wants them."
  value       = join(",", aws_subnet.public[*].id)
}

# The one-off tasks' network place (the schemas' hand-overs, scripts/hand-over.sh).
output "task_security_group" {
  value = aws_security_group.tasks.id
}

output "user_pool_id" {
  value = aws_cognito_user_pool.users.id
}

output "user_pool_client_id" {
  value = aws_cognito_user_pool_client.api.id
}

output "database_reader_endpoint" {
  description = "Where queries read; a moment behind the writer."
  value       = aws_rds_cluster.main.reader_endpoint
}

# The route switches the last apply set: scripts/deploy-shared.sh keeps them when a deploy does not set
# them, so a gateway release (shared.yml) never sends Campaigns' or Notifications' paths back to the
# catch-all, which answers 404 (CQA-2116).
output "campaigns_serves_routes" {
  value = var.campaigns_serves_routes
}

output "notifications_serves_preferences" {
  value = var.notifications_serves_preferences
}
