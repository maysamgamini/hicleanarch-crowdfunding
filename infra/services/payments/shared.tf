data "aws_caller_identity" "current" {}

# The shared layer's outputs (infra/env/shared-outputs.tf). If the environment is not up, this read
# fails and nothing here is planned: Payments cannot be applied without the network it runs in.
data "aws_s3_object" "shared" {
  bucket = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}"
  key    = "env/shared.json"
}

locals {
  shared = jsondecode(data.aws_s3_object.shared.body)

  # The bootstrap layer's boundary: the deploy role may create a role only with it attached.
  task_boundary = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/${var.project}-task-boundary"
}
