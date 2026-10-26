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
