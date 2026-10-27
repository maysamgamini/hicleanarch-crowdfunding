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

# The state holds every secret OpenTofu knows; it may travel over TLS only.
data "aws_iam_policy_document" "state_tls_only" {
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]
    resources = [
      aws_s3_bucket.state.arn,
      "${aws_s3_bucket.state.arn}/*",
    ]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "state" {
  bucket = aws_s3_bucket.state.id
  policy = data.aws_iam_policy_document.state_tls_only.json

  depends_on = [aws_s3_bucket_public_access_block.state]
}

# Container images outlive the environments, so a destroyed sandbox can be applied again
# without rebuilding.
# The monolith's images, kept for their history: since s5-l10 no workflow pushes here (the Pledges service
# has its own repository, services.tf). A repository is destroyed only with the bootstrap layer.
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

# What the sandbox workflow does outside IAM: apply and destroy infra/env, push the images, run the
# migration task, create test users. Only the services infra/env uses, in the course's Region; the
# project's own secrets, topics, log groups, image repositories and state bucket. No KMS rights:
# every key the sandbox encrypts with is an AWS managed key, which the services use on its behalf.
# The IAM rights (deploy_iam below) let it create only the project's own task roles, and only with
# the task boundary attached, so no role it creates can do more than a task needs.
data "aws_iam_policy_document" "deploy_services" {
  statement {
    sid = "SandboxServices"
    actions = [
      "ecs:*", "elasticloadbalancing:*", "rds:*", "elasticache:*", "cognito-idp:*",
      "application-autoscaling:*", "cloudwatch:*", "ses:*",
      # Stage 4: the Cloud Map namespace Service Connect names the services in.
      "servicediscovery:*",
    ]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "aws:RequestedRegion"
      values   = [var.region]
    }
  }
  # The network: a VPC and what it holds, nothing that runs (no instances, no NAT, no endpoints).
  statement {
    sid = "SandboxNetwork"
    actions = [
      "ec2:Describe*", "ec2:GetManagedPrefixListEntries", "ec2:CreateTags", "ec2:DeleteTags",
      "ec2:CreateVpc", "ec2:DeleteVpc", "ec2:ModifyVpcAttribute",
      "ec2:CreateSubnet", "ec2:DeleteSubnet", "ec2:ModifySubnetAttribute",
      "ec2:CreateInternetGateway", "ec2:DeleteInternetGateway", "ec2:AttachInternetGateway", "ec2:DetachInternetGateway",
      "ec2:CreateRouteTable", "ec2:DeleteRouteTable", "ec2:CreateRoute", "ec2:DeleteRoute", "ec2:ReplaceRoute",
      "ec2:AssociateRouteTable", "ec2:DisassociateRouteTable", "ec2:ReplaceRouteTableAssociation",
      "ec2:CreateSecurityGroup", "ec2:DeleteSecurityGroup", "ec2:ModifySecurityGroupRules",
      "ec2:AuthorizeSecurityGroupIngress", "ec2:AuthorizeSecurityGroupEgress",
      "ec2:RevokeSecurityGroupIngress", "ec2:RevokeSecurityGroupEgress",
      "ec2:UpdateSecurityGroupRuleDescriptionsIngress", "ec2:UpdateSecurityGroupRuleDescriptionsEgress",
    ]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "aws:RequestedRegion"
      values   = [var.region]
    }
  }
  # CloudFront is global: its API has no Region of the sandbox's own.
  statement {
    sid       = "SandboxCdn"
    actions   = ["cloudfront:*"]
    resources = ["*"]
  }
  statement {
    sid       = "ProjectSecrets"
    actions   = ["secretsmanager:*"]
    resources = ["arn:aws:secretsmanager:${var.region}:${data.aws_caller_identity.current.account_id}:secret:${var.project}/*"]
  }
  statement {
    sid       = "ProjectAlarmTopics"
    actions   = ["sns:*"]
    resources = ["arn:aws:sns:${var.region}:${data.aws_caller_identity.current.account_id}:${var.project}-*"]
  }
  # Stage 4: the event queues and their dead-letter queues, the project's own only, as its topics.
  statement {
    sid       = "ProjectQueues"
    actions   = ["sqs:*"]
    resources = ["arn:aws:sqs:${var.region}:${data.aws_caller_identity.current.account_id}:${var.project}-*"]
  }
  statement {
    sid       = "ListQueues"
    actions   = ["sqs:ListQueues"]
    resources = ["*"]
  }
  statement {
    sid     = "ProjectLogGroups"
    actions = ["logs:*"]
    resources = [
      "arn:aws:logs:${var.region}:${data.aws_caller_identity.current.account_id}:log-group:/${var.project}/*",
      "arn:aws:logs:${var.region}:${data.aws_caller_identity.current.account_id}:log-group:/aws/ecs/application/metrics*",
    ]
  }
  statement {
    sid       = "ListLogGroups"
    actions   = ["logs:DescribeLogGroups"]
    resources = ["*"]
  }
  statement {
    sid       = "PushImagesSignIn"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }
  statement {
    sid = "PushImages"
    actions = [
      "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload",
      "ecr:PutImage", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:DescribeImages", "ecr:DescribeRepositories",
    ]
    resources = [aws_ecr_repository.api.arn, aws_ecr_repository.gateway.arn, aws_ecr_repository.payments.arn]
  }
  # The environment's state: read, write, and the lock file S3 itself keeps (use_lockfile).
  statement {
    sid       = "StateBucket"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.state.arn]
  }
  statement {
    sid       = "StateObjects"
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.state.arn}/env/*"]
  }
}

resource "aws_iam_policy" "deploy_services" {
  name   = "${var.project}-github-deploy-services"
  policy = data.aws_iam_policy_document.deploy_services.json
}

resource "aws_iam_role_policy_attachment" "deploy_services" {
  role       = aws_iam_role.deploy.name
  policy_arn = aws_iam_policy.deploy_services.arn
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
  # Events between services (Stage 4): what infra/env's task policies grant (messaging.tf,
  # payments-service.tf), on the project's own queues, topics and subscriptions only. A subscription's
  # ARN is its topic's plus an id, so the topic pattern covers it. tests/boundary.tftest.hcl checks the list.
  statement {
    sid       = "ProjectQueues"
    actions   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"]
    resources = ["arn:aws:sqs:${var.region}:${data.aws_caller_identity.current.account_id}:${var.project}-*"]
  }
  statement {
    sid       = "ProjectTopics"
    actions   = ["sns:Publish", "sns:ListSubscriptionsByTopic", "sns:GetSubscriptionAttributes"]
    resources = ["arn:aws:sns:${var.region}:${data.aws_caller_identity.current.account_id}:${var.project}-*"]
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
  # Nor the boundary, nor the deploy role's own service policy.
  statement {
    effect = "Deny"
    actions = [
      "iam:CreatePolicyVersion", "iam:DeletePolicy", "iam:DeletePolicyVersion", "iam:SetDefaultPolicyVersion",
      "iam:TagPolicy", "iam:UntagPolicy",
    ]
    resources = [aws_iam_policy.task_boundary.arn, aws_iam_policy.deploy_services.arn]
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
