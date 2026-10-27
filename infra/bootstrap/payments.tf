# The Payments service's images (lesson s4-l05), kept like the API's and the gateway's.
resource "aws_ecr_repository" "payments" {
  name                 = "${var.project}/crowdfunding-payments"
  image_tag_mutability = "IMMUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "payments" {
  repository = aws_ecr_repository.payments.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep the last 20 images"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 20
      }
      action = { type = "expire" }
    }]
  })
}

output "payments_ecr_repository_url" {
  value = aws_ecr_repository.payments.repository_url
}
