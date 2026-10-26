data "aws_caller_identity" "current" {}

# State for the ephemeral environments. Locking uses S3 itself (use_lockfile in the backend),
# so no DynamoDB table is needed.
resource "aws_s3_bucket" "state" {
  bucket = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}"
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# Container images outlive the environments, so a destroyed sandbox can be applied again
# without rebuilding.
resource "aws_ecr_repository" "api" {
  name                 = "${var.project}/crowdfunding-api"
  image_tag_mutability = "IMMUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "api" {
  repository = aws_ecr_repository.api.name
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

# The NuGet feed for the BuildingBlocks package (Stage 3).
resource "aws_codeartifact_domain" "course" {
  domain = var.project
}

resource "aws_codeartifact_repository" "packages" {
  repository = "packages"
  domain     = aws_codeartifact_domain.course.domain
}
