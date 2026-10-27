terraform {
  required_version = "~> 1.13" # OpenTofu 1.13 or a later 1.x; CI runs the version in .opentofu-version

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # The bootstrap layer keeps its own state locally: it creates the bucket that every
  # other layer keeps its state in, so it cannot use that bucket itself.
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project = var.project
      Layer   = "bootstrap"
    }
  }
}
