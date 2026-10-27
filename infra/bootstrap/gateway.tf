# The gateway's images, kept like the API's: tags never change, old images expire.
resource "aws_ecr_repository" "gateway" {
  name                 = "${var.project}/crowdfunding-gateway"
  image_tag_mutability = "IMMUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "gateway" {
  repository = aws_ecr_repository.gateway.name
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

output "gateway_ecr_repository_url" {
  value = aws_ecr_repository.gateway.repository_url
}
