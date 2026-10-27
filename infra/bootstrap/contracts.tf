# Publishing the modules' Contracts packages (Stage 4) has a role of its own, like the BuildingBlocks':
# it may be taken only by workflows started from a contracts-*-v* tag of this repository, and it may
# only publish CrowdFunding.<Module>.Contracts packages to the course's repository, after checking that
# the BuildingBlocks version they depend on is there.
data "aws_iam_policy_document" "contracts_assume" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repository}:ref:refs/tags/contracts-*-v*"]
    }
  }
}

resource "aws_iam_role" "contracts" {
  name               = "${var.project}-github-contracts"
  assume_role_policy = data.aws_iam_policy_document.contracts_assume.json
}

locals {
  # NuGet has no namespace, hence the empty segment. Both spellings, because IAM compares names case-sensitively.
  package_arn_prefix = "arn:aws:codeartifact:${var.region}:${data.aws_caller_identity.current.account_id}:package/${aws_codeartifact_domain.course.domain}/${aws_codeartifact_repository.packages.repository}/nuget/"
}

data "aws_iam_policy_document" "contracts_publish" {
  statement {
    actions   = ["codeartifact:GetAuthorizationToken"]
    resources = [aws_codeartifact_domain.course.arn]
  }
  statement {
    actions   = ["sts:GetServiceBearerToken"]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "sts:AWSServiceName"
      values   = ["codeartifact.amazonaws.com"]
    }
  }
  statement {
    actions   = ["codeartifact:GetRepositoryEndpoint", "codeartifact:ReadFromRepository"]
    resources = [aws_codeartifact_repository.packages.arn]
  }
  statement {
    actions = ["codeartifact:DescribePackageVersion"]
    resources = [
      "${local.package_arn_prefix}/CrowdFunding.BuildingBlocks.Application",
      "${local.package_arn_prefix}/crowdfunding.buildingblocks.application",
    ]
  }
  statement {
    actions = ["codeartifact:PublishPackageVersion", "codeartifact:PutPackageMetadata"]
    resources = [
      "${local.package_arn_prefix}/CrowdFunding.*.Contracts",
      "${local.package_arn_prefix}/crowdfunding.*.contracts",
    ]
  }
}

resource "aws_iam_role_policy" "contracts_publish" {
  role   = aws_iam_role.contracts.id
  policy = data.aws_iam_policy_document.contracts_publish.json
}

output "contracts_role_arn" {
  description = "Set it as the AWS_CONTRACTS_ROLE_ARN variable of the GitHub repository."
  value       = aws_iam_role.contracts.arn
}
