# Delivering the Payments service on its own (s4-l09): its image repository and a deploy role that can
# change Payments and nothing else. The deploy role above (hicleanarch-github-deploy) keeps the shared
# layer and the monolith, and loses every right over Payments' own resources.

locals {
  account_id = data.aws_caller_identity.current.account_id

  payments_roles          = "arn:aws:iam::${local.account_id}:role/${var.project}-sandbox-payments-*"
  payments_families       = ["${var.project}-payments", "${var.project}-payments-migrate", "${var.project}-payments-move"]
  payments_task_defs      = [for f in local.payments_families : "arn:aws:ecs:${var.region}:${local.account_id}:task-definition/${f}:*"]
  payments_service        = "arn:aws:ecs:${var.region}:${local.account_id}:service/${var.project}-sandbox/payments"
  payments_log_group      = "arn:aws:logs:${var.region}:${local.account_id}:log-group:/${var.project}/sandbox/payments"
  payments_state          = "${aws_s3_bucket.state.arn}/services/payments/*"
  shared_outputs_document = "${aws_s3_bucket.state.arn}/env/shared.json"
}

resource "aws_ecr_repository" "payments" {
  name                 = "${var.project}/crowdfunding-payments"
  image_tag_mutability = "IMMUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "payments" {
  repository = aws_ecr_repository.payments.name
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

# The same trust as the other deploy role: workflows of this repository's main branch only.
resource "aws_iam_role" "deploy_payments" {
  name               = "${var.project}-github-deploy-payments"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
}

data "aws_iam_policy_document" "deploy_payments" {
  # Its image: push and read, in its own repository only.
  statement {
    sid       = "PushImagesSignIn"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }
  statement {
    sid = "PushItsImages"
    actions = [
      "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload",
      "ecr:PutImage", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:DescribeImages", "ecr:DescribeRepositories",
    ]
    resources = [aws_ecr_repository.payments.arn]
  }

  # Its state, and the one document of the shared layer it reads (never the shared layer's state).
  statement {
    sid       = "StateBucket"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.state.arn]
  }
  statement {
    sid       = "ItsState"
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = [local.payments_state]
  }
  statement {
    sid       = "SharedOutputs"
    actions   = ["s3:GetObject", "s3:GetObjectTagging"]
    resources = [local.shared_outputs_document]
  }

  # Its task definitions: registered only in its own families, tagged as they are created. Describing
  # and listing have no resource-level permission in ECS. Deregistering has none either, and is not
  # granted: the task definitions are kept (skip_destroy), so this role cannot retire the monolith's.
  statement {
    sid       = "ItsTaskDefinitions"
    actions   = ["ecs:RegisterTaskDefinition"]
    resources = local.payments_task_defs
  }
  statement {
    sid       = "ReadTaskDefinitions"
    actions   = ["ecs:DescribeTaskDefinition", "ecs:ListTaskDefinitions"]
    resources = ["*"]
  }
  # Its ECS service, which may run only its own task definition.
  statement {
    sid       = "ItsService"
    actions   = ["ecs:CreateService", "ecs:UpdateService"]
    resources = [local.payments_service]
    condition {
      test     = "ArnLikeIfExists"
      variable = "ecs:task-definition"
      values   = local.payments_task_defs
    }
  }
  statement {
    sid       = "ItsServiceLifecycle"
    actions   = ["ecs:DeleteService", "ecs:DescribeServices"]
    resources = [local.payments_service]
  }
  statement {
    sid       = "ReadItsTags"
    actions   = ["ecs:ListTagsForResource"]
    resources = concat(local.payments_task_defs, [local.payments_service])
  }
  # Its migration task, in the sandbox's cluster.
  statement {
    sid       = "RunItsMigration"
    actions   = ["ecs:RunTask"]
    resources = local.payments_task_defs
    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = ["arn:aws:ecs:${var.region}:${local.account_id}:cluster/${var.project}-sandbox"]
    }
  }
  statement {
    sid       = "WatchItsMigration"
    actions   = ["ecs:DescribeTasks"]
    resources = ["arn:aws:ecs:${var.region}:${local.account_id}:task/${var.project}-sandbox/*"]
  }
  statement {
    sid       = "TagWhatItCreates"
    actions   = ["ecs:TagResource"]
    resources = concat(local.payments_task_defs, [local.payments_service, "arn:aws:ecs:${var.region}:${local.account_id}:task/${var.project}-sandbox/*"])
    condition {
      test     = "StringEquals"
      variable = "ecs:CreateAction"
      values   = ["RegisterTaskDefinition", "CreateService", "RunTask"]
    }
  }

  # Its log group.
  statement {
    sid       = "ItsLogGroup"
    actions   = ["logs:*"]
    resources = [local.payments_log_group, "${local.payments_log_group}:*"]
  }
  statement {
    sid       = "ListLogGroups"
    actions   = ["logs:DescribeLogGroups"]
    resources = ["*"]
  }

  # Its roles: the same rules as the other deploy role's (deploy_iam), narrowed to Payments' names.
  statement {
    sid = "ReadItsRoles"
    actions = [
      "iam:GetRole", "iam:DeleteRole", "iam:TagRole", "iam:UntagRole", "iam:UpdateAssumeRolePolicy",
      "iam:ListRolePolicies", "iam:ListAttachedRolePolicies", "iam:ListInstanceProfilesForRole", "iam:GetRolePolicy",
    ]
    resources = [local.payments_roles]
  }
  statement {
    sid       = "ChangeItsRolesUnderTheBoundary"
    actions   = ["iam:CreateRole", "iam:PutRolePolicy", "iam:DeleteRolePolicy", "iam:DetachRolePolicy", "iam:PutRolePermissionsBoundary"]
    resources = [local.payments_roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PermissionsBoundary"
      values   = [aws_iam_policy.task_boundary.arn]
    }
  }
  statement {
    sid       = "AttachOnlyTheEcsExecutionPolicy"
    actions   = ["iam:AttachRolePolicy"]
    resources = [local.payments_roles]
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
  statement {
    sid       = "PassItsRolesToEcsTasks"
    actions   = ["iam:PassRole"]
    resources = [local.payments_roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }
  statement {
    sid       = "KeepTheBoundary"
    effect    = "Deny"
    actions   = ["iam:DeleteRolePermissionsBoundary"]
    resources = ["*"]
  }
  # Never a GitHub role, its own included, and never the boundary or a deploy policy.
  statement {
    sid    = "NeverTheGitHubRoles"
    effect = "Deny"
    actions = [
      "iam:UpdateAssumeRolePolicy", "iam:DeleteRole", "iam:Put*", "iam:Attach*", "iam:Detach*",
      "iam:DeleteRolePolicy", "iam:TagRole", "iam:UntagRole",
    ]
    resources = [aws_iam_role.deploy.arn, aws_iam_role.deploy_payments.arn, aws_iam_role.packages.arn]
  }
  statement {
    sid    = "NeverThePolicies"
    effect = "Deny"
    actions = [
      "iam:CreatePolicyVersion", "iam:DeletePolicy", "iam:DeletePolicyVersion", "iam:SetDefaultPolicyVersion",
      "iam:TagPolicy", "iam:UntagPolicy",
    ]
    resources = [aws_iam_policy.task_boundary.arn, aws_iam_policy.deploy_services.arn, aws_iam_policy.deploy_shared.arn]
  }
}

resource "aws_iam_role_policy" "deploy_payments" {
  role   = aws_iam_role.deploy_payments.id
  policy = data.aws_iam_policy_document.deploy_payments.json
}

# The other deploy role keeps the shared layer and the monolith. What the shared layer gained in Stage 4
# (queues, the Service Connect namespace, the outputs document) is added here; what is Payments' own is
# denied, so that role cannot deploy or reconfigure Payments, push its image or change its roles. It
# already has no right to Payments' state: its state rights reach env/* only.
data "aws_iam_policy_document" "deploy_shared" {
  statement {
    sid       = "SharedQueues"
    actions   = ["sqs:*"]
    resources = ["arn:aws:sqs:${var.region}:${local.account_id}:${var.project}-*"]
  }
  statement {
    sid       = "SharedNamespace"
    actions   = ["servicediscovery:*"]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "aws:RequestedRegion"
      values   = [var.region]
    }
  }
  statement {
    sid       = "SharedOutputsTags"
    actions   = ["s3:GetObjectTagging", "s3:PutObjectTagging"]
    resources = [local.shared_outputs_document]
  }
  statement {
    sid    = "NotPaymentsService"
    effect = "Deny"
    actions = [
      "ecs:CreateService", "ecs:UpdateService", "ecs:DeleteService", "ecs:RegisterTaskDefinition", "ecs:RunTask",
      "ecs:StartTask", "ecs:TagResource", "ecs:UntagResource",
    ]
    resources = concat(local.payments_task_defs, [local.payments_service])
  }
  statement {
    sid       = "NotPaymentsImages"
    effect    = "Deny"
    actions   = ["ecr:*"]
    resources = [aws_ecr_repository.payments.arn]
  }
  statement {
    sid       = "NotPaymentsRoles"
    effect    = "Deny"
    actions   = ["iam:*"]
    resources = [local.payments_roles]
  }
  statement {
    sid       = "NotPaymentsLogs"
    effect    = "Deny"
    actions   = ["logs:*"]
    resources = [local.payments_log_group, "${local.payments_log_group}:*"]
  }
}

resource "aws_iam_policy" "deploy_shared" {
  name   = "${var.project}-github-deploy-shared"
  policy = data.aws_iam_policy_document.deploy_shared.json
}

resource "aws_iam_role_policy_attachment" "deploy_shared" {
  role       = aws_iam_role.deploy.name
  policy_arn = aws_iam_policy.deploy_shared.arn
}

output "payments_ecr_repository_url" {
  value = aws_ecr_repository.payments.repository_url
}

output "payments_deploy_role_arn" {
  description = "Set it as the AWS_PAYMENTS_DEPLOY_ROLE_ARN variable of the GitHub repository."
  value       = aws_iam_role.deploy_payments.arn
}
