terraform {
  required_version = "~> 1.13" # Terraform or OpenTofu, 1.13 or a later 1.x

  # The registry is named, so Terraform and OpenTofu resolve the same provider address and both
  # check the one .terraform.lock.hcl. With the short "hashicorp/aws", each tool would pick its own
  # registry and rewrite the other's entries out of the lock file.
  required_providers {
    aws = {
      source  = "registry.opentofu.org/hashicorp/aws"
      version = "~> 6.0"
    }
  }
}
