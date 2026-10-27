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

# GitHub Actions signs in to AWS with short-lived credentials (OpenID Connect): no access keys
# are stored in GitHub. Only workflows of this repository's main branch may take the role.
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

data "aws_iam_policy_document" "github_assume" {
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
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repository}:ref:refs/heads/main"]
    }
  }
}

resource "aws_iam_role" "deploy" {
  name               = "${var.project}-github-deploy"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
}

# The sandbox creates most kinds of resources, so the role gets PowerUserAccess, which grants
# no IAM rights. The IAM rights below let it create only the project's own task roles, and only
# with the task boundary attached, so no role it creates can do more than a task needs.
resource "aws_iam_role_policy_attachment" "deploy_power_user" {
  role       = aws_iam_role.deploy.name
  policy_arn = "arn:aws:iam::aws:policy/PowerUserAccess"
}

# The most any task role may do, whatever policies are attached to it.
data "aws_iam_policy_document" "task_boundary" {
  statement {
    actions = [
      "ecr:GetAuthorizationToken", "ecr:BatchCheckLayerAvailability", "ecr:GetDownloadUrlForLayer", "ecr:BatchGetImage",
      "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams",
      "xray:PutTraceSegments", "xray:PutTelemetryRecords", "xray:GetSamplingRules", "xray:GetSamplingTargets",
      "cloudwatch:PutMetricData",
      "ses:SendEmail", "ses:SendRawEmail", "cognito-idp:ListUsers",
    ]
    resources = ["*"]
  }
  # The project's own secrets only, so a role created under the boundary cannot read anything else.
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = ["arn:aws:secretsmanager:${var.region}:${data.aws_caller_identity.current.account_id}:secret:${var.project}/*"]
  }
}

resource "aws_iam_policy" "task_boundary" {
  name   = "${var.project}-task-boundary"
  policy = data.aws_iam_policy_document.task_boundary.json
}

locals {
  # The environment's roles only. The GitHub roles (hicleanarch-github-*) do not match, so the deploy
  # role can neither change its own trust policy nor take over the packages role.
  project_roles = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/${var.project}-sandbox-*"
}

data "aws_iam_policy_document" "deploy_iam" {
  # Reading, tagging and deleting the project's roles.
  statement {
    actions = [
      "iam:GetRole", "iam:DeleteRole", "iam:TagRole", "iam:UntagRole", "iam:UpdateAssumeRolePolicy",
      "iam:ListRolePolicies", "iam:ListAttachedRolePolicies", "iam:ListInstanceProfilesForRole", "iam:GetRolePolicy",
    ]
    resources = [local.project_roles]
  }
  # Creating a role, or changing its policies, only while the task boundary is on it.
  statement {
    actions   = ["iam:CreateRole", "iam:PutRolePolicy", "iam:DeleteRolePolicy", "iam:DetachRolePolicy", "iam:PutRolePermissionsBoundary"]
    resources = [local.project_roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PermissionsBoundary"
      values   = [aws_iam_policy.task_boundary.arn]
    }
  }
  # Attaching a managed policy: the boundary must be on the role, and the policy must be the ECS one.
  statement {
    actions   = ["iam:AttachRolePolicy"]
    resources = [local.project_roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PermissionsBoundary"
      values   = [aws_iam_policy.task_boundary.arn]
    }
    condition {
      test     = "ArnEquals"
      variable = "iam:PolicyARN"
      values   = ["arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"]
    }
  }
  # The project's roles may be handed to ECS tasks, and to nothing else.
  statement {
    actions   = ["iam:PassRole"]
    resources = [local.project_roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }
  statement {
    actions   = ["iam:CreateServiceLinkedRole"]
    resources = ["*"]
  }
  statement {
    effect    = "Deny"
    actions   = ["iam:DeleteRolePermissionsBoundary"]
    resources = ["*"]
  }
  # Never the GitHub roles themselves, whatever another statement or a later policy allows: a workflow
  # must not be able to widen who may take a role (the delegation pattern in the IAM guide).
  statement {
    effect = "Deny"
    actions = [
      "iam:UpdateAssumeRolePolicy", "iam:DeleteRole", "iam:Put*", "iam:Attach*", "iam:Detach*",
      "iam:DeleteRolePolicy", "iam:TagRole", "iam:UntagRole",
    ]
    resources = [aws_iam_role.deploy.arn, aws_iam_role.packages.arn]
  }
  # Nor the boundary itself.
  statement {
    effect = "Deny"
    actions = [
      "iam:CreatePolicyVersion", "iam:DeletePolicy", "iam:DeletePolicyVersion", "iam:SetDefaultPolicyVersion",
      "iam:TagPolicy", "iam:UntagPolicy",
    ]
    resources = [aws_iam_policy.task_boundary.arn]
  }
}

# Publishing packages has a role of its own: it may be taken only by workflows started from a
# bb-v* tag of this repository, and it may do nothing but publish to the course's repository.
data "aws_iam_policy_document" "packages_assume" {
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
      values   = ["repo:${var.github_repository}:ref:refs/tags/bb-v*"]
    }
  }
}

resource "aws_iam_role" "packages" {
  name               = "${var.project}-github-packages"
  assume_role_policy = data.aws_iam_policy_document.packages_assume.json
}

data "aws_iam_policy_document" "packages_publish" {
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
    # Only the BuildingBlocks packages (NuGet has no namespace, hence the empty segment). Both
    # spellings, because IAM compares names case-sensitively.
    actions = ["codeartifact:PublishPackageVersion", "codeartifact:PutPackageMetadata"]
    resources = [
      "arn:aws:codeartifact:${var.region}:${data.aws_caller_identity.current.account_id}:package/${aws_codeartifact_domain.course.domain}/${aws_codeartifact_repository.packages.repository}/nuget//CrowdFunding.BuildingBlocks.*",
      "arn:aws:codeartifact:${var.region}:${data.aws_caller_identity.current.account_id}:package/${aws_codeartifact_domain.course.domain}/${aws_codeartifact_repository.packages.repository}/nuget//crowdfunding.buildingblocks.*",
    ]
  }
}

resource "aws_iam_role_policy" "packages_publish" {
  role   = aws_iam_role.packages.id
  policy = data.aws_iam_policy_document.packages_publish.json
}

resource "aws_iam_role_policy" "deploy_iam" {
  role   = aws_iam_role.deploy.id
  policy = data.aws_iam_policy_document.deploy_iam.json
}
