# The Notifications service's own OpenTofu root (lesson s5-l03): its roles, task definitions, ECS service and log group, in a
# state of its own, applied by its own deploy role (infra/bootstrap/services.tf). It reads the shared
# layer's published outputs, never its state; its security group, the database and Valkey rules and the
# one-time hand-over of its schema stay with the shared layer (infra/env/notifications-network.tf).
#
#   tofu init -backend-config="bucket=<state_bucket output of infra/bootstrap>"
#
# Deploy with scripts/deploy-service.sh notifications; the shared layer must be applied first and destroyed last.
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

  backend "s3" {
    key          = "services/notifications/terraform.tfstate"
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
      Layer   = "services/notifications"
      Service = "notifications"
    }
  }
}
