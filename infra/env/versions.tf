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
    random = {
      source  = "registry.opentofu.org/hashicorp/random"
      version = "~> 3.7" # ephemeral random_password
    }
  }

  # State lives in the bootstrap layer's bucket; S3 itself locks it (use_lockfile).
  # The bucket name includes the account id, so it is passed at init:
  #   tofu init -backend-config="bucket=<state_bucket output of infra/bootstrap>"
  backend "s3" {
    key          = "env/terraform.tfstate"
    region       = "us-east-1"
    use_lockfile = true
    encrypt      = true
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project = var.project
      Layer   = "env"
    }
  }
}
