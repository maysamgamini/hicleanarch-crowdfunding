terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7" # ephemeral random_password
    }
  }

  # State lives in the bootstrap layer's bucket; S3 itself locks it (use_lockfile).
  # The bucket name includes the account id, so it is passed at init:
  #   terraform init -backend-config="bucket=<state_bucket output of infra/bootstrap>"
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
