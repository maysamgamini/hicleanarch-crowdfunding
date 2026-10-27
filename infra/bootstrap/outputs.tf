output "state_bucket" {
  description = "Bucket for the state of infra/env."
  value       = aws_s3_bucket.state.bucket
}

output "ecr_repository_url" {
  value = aws_ecr_repository.api.repository_url
}

output "codeartifact_repository" {
  value = "${aws_codeartifact_domain.course.domain}/${aws_codeartifact_repository.packages.repository}"
}

output "deploy_role_arn" {
  description = "Set it as the AWS_DEPLOY_ROLE_ARN variable of the GitHub repository."
  value       = aws_iam_role.deploy.arn
}

output "task_boundary_arn" {
  description = "The permissions boundary every task role of the environment must carry."
  value       = aws_iam_policy.task_boundary.arn
}
