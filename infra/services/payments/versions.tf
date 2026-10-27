# The Payments service's own Terraform root (s4-l09): its roles, task definitions, ECS service and log
# group, in a state of its own. It stands beside the shared layer (infra/env: network, cluster, database,
# gateway, Valkey, Cognito, topics and queues) and reads that layer's published outputs, never its state.
# A plan here never shows the monolith's resources, and the role that applies it can change nothing else.
#
#   terraform init -backend-config="bucket=<state_bucket output of infra/bootstrap>"
#
# Deploy with scripts/deploy-payments.sh; the shared layer must be applied first and destroyed last.
terraform {
  required_version = ">= 1.11"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  backend "s3" {
    key          = "services/payments/terraform.tfstate"
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
      Layer   = "services/payments"
      Service = "payments"
    }
  }
}
