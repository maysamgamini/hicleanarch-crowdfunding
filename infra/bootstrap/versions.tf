terraform {
  required_version = "~> 1.13" # Terraform or OpenTofu, 1.13 or a later 1.x; CI runs both (.terraform-version, .opentofu-version)

  # The registry is named, so Terraform and OpenTofu resolve the same provider address and both
  # check the one .terraform.lock.hcl. With the short "hashicorp/aws", each tool would pick its own
  # registry and rewrite the other's entries out of the lock file.
  required_providers {
    aws = {
      source  = "registry.opentofu.org/hashicorp/aws"
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
