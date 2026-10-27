# Delivering Stage 5's services on their own (from s5-l03, when each gets its host), as s4-l09 does for
# Payments: per service an image repository and a deploy role that can change that service and nothing
# else. The shared deploy role (hicleanarch-github-deploy) keeps the shared layer, the gateway and each
# service's one-time hand-over (hicleanarch-handover-<name>, which alone reads the database owner's
# secret), and loses every right over the services' own resources. Campaigns joined this list when it got
# its host (s5-l08), and Pledges when the monolith's host became its own (s5-l10).

locals {
  delivered_services = toset(["notifications", "campaigns", "pledges"])

  service_access = {
    for name in local.delivered_services : name => {
      roles      = "arn:aws:iam::${local.account_id}:role/${var.project}-sandbox-${name}-*"
      task_defs  = ["arn:aws:ecs:${var.region}:${local.account_id}:task-definition/${var.project}-${name}:*", "arn:aws:ecs:${var.region}:${local.account_id}:task-definition/${var.project}-${name}-migrate:*"]
      service    = "arn:aws:ecs:${var.region}:${local.account_id}:service/${var.project}-sandbox/${name}"
      log_group  = "arn:aws:logs:${var.region}:${local.account_id}:log-group:/${var.project}/sandbox/${name}"
      state      = "${aws_s3_bucket.state.arn}/services/${name}/*"
      repository = "arn:aws:ecr:${var.region}:${local.account_id}:repository/${var.project}/crowdfunding-${name}"
    }
  }
}

resource "aws_ecr_repository" "service" {
  for_each             = local.delivered_services
  name                 = "${var.project}/crowdfunding-${each.key}"
  image_tag_mutability = "IMMUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "service" {
  for_each   = aws_ecr_repository.service
  repository = each.value.name
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

# The same trust as the other deploy roles: workflows of this repository's main branch only.
resource "aws_iam_role" "deploy_service" {
  for_each           = local.delivered_services
  name               = "${var.project}-github-deploy-${each.key}"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
}

# Payments' deploy role (payments.tf), narrowed to each service's own names.
data "aws_iam_policy_document" "deploy_service" {
  for_each = local.delivered_services

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
    resources = [aws_ecr_repository.service[each.key].arn]
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
    resources = [local.service_access[each.key].state]
  }
  statement {
    sid       = "SharedOutputs"
    actions   = ["s3:GetObject", "s3:GetObjectTagging"]
    resources = [local.shared_outputs_document]
  }

  # Its task definitions (the service and its migrate step), registered in its own families only.
  # Deregistering has no resource-level permission in ECS and is not granted (skip_destroy).
  statement {
    sid       = "ItsTaskDefinitions"
    actions   = ["ecs:RegisterTaskDefinition"]
    resources = local.service_access[each.key].task_defs
  }
  statement {
    sid       = "ReadTaskDefinitions"
    actions   = ["ecs:DescribeTaskDefinition", "ecs:ListTaskDefinitions"]
    resources = ["*"]
  }
  statement {
    sid       = "ItsService"
    actions   = ["ecs:CreateService", "ecs:UpdateService"]
    resources = [local.service_access[each.key].service]
    condition {
      test     = "ArnLikeIfExists"
      variable = "ecs:task-definition"
      values   = local.service_access[each.key].task_defs
    }
  }
  statement {
    sid       = "ItsServiceLifecycle"
    actions   = ["ecs:DeleteService", "ecs:DescribeServices"]
    resources = [local.service_access[each.key].service]
  }
  # The contract gate's check (s5-l11): which version every other service of the cluster runs. Read only.
  statement {
    sid       = "ReadTheOtherServices"
    actions   = ["ecs:DescribeServices"]
    resources = ["arn:aws:ecs:${var.region}:${local.account_id}:service/${var.project}-sandbox/*"]
  }
  statement {
    sid       = "ReadItsTags"
    actions   = ["ecs:ListTagsForResource"]
    resources = concat(local.service_access[each.key].task_defs, [local.service_access[each.key].service])
  }
  # Its migrate step, as its own migration role, in the sandbox's cluster.
  statement {
    sid       = "RunItsMigration"
    actions   = ["ecs:RunTask"]
    resources = local.service_access[each.key].task_defs
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
    resources = concat(local.service_access[each.key].task_defs, [local.service_access[each.key].service, "arn:aws:ecs:${var.region}:${local.account_id}:task/${var.project}-sandbox/*"])
    condition {
      test     = "StringEquals"
      variable = "ecs:CreateAction"
      values   = ["RegisterTaskDefinition", "CreateService", "RunTask"]
    }
  }

  statement {
    sid       = "ItsLogGroup"
    actions   = ["logs:*"]
    resources = [local.service_access[each.key].log_group, "${local.service_access[each.key].log_group}:*"]
  }
  statement {
    sid       = "ListLogGroups"
    actions   = ["logs:DescribeLogGroups"]
    resources = ["*"]
  }

  # Its roles, under the task boundary, passed to ECS tasks only.
  statement {
    sid = "ReadItsRoles"
    actions = [
      "iam:GetRole", "iam:DeleteRole", "iam:TagRole", "iam:UntagRole", "iam:UpdateAssumeRolePolicy",
      "iam:ListRolePolicies", "iam:ListAttachedRolePolicies", "iam:ListInstanceProfilesForRole", "iam:GetRolePolicy",
    ]
    resources = [local.service_access[each.key].roles]
  }
  statement {
    sid       = "ChangeItsRolesUnderTheBoundary"
    actions   = ["iam:CreateRole", "iam:PutRolePolicy", "iam:DeleteRolePolicy", "iam:DetachRolePolicy", "iam:PutRolePermissionsBoundary"]
    resources = [local.service_access[each.key].roles]
    condition {
      test     = "StringEquals"
      variable = "iam:PermissionsBoundary"
      values   = [aws_iam_policy.task_boundary.arn]
    }
  }
  statement {
    sid       = "AttachOnlyTheEcsExecutionPolicy"
    actions   = ["iam:AttachRolePolicy"]
    resources = [local.service_access[each.key].roles]
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
    resources = [local.service_access[each.key].roles]
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
  statement {
    sid    = "NeverTheGitHubRoles"
    effect = "Deny"
    actions = [
      "iam:UpdateAssumeRolePolicy", "iam:DeleteRole", "iam:Put*", "iam:Attach*", "iam:Detach*",
      "iam:DeleteRolePolicy", "iam:TagRole", "iam:UntagRole",
    ]
    resources = concat(
      [aws_iam_role.deploy.arn, aws_iam_role.deploy_payments.arn, aws_iam_role.packages.arn],
      [for role in aws_iam_role.deploy_service : role.arn],
    )
  }
  statement {
    sid    = "NeverThePolicies"
    effect = "Deny"
    actions = [
      "iam:CreatePolicyVersion", "iam:DeletePolicy", "iam:DeletePolicyVersion", "iam:SetDefaultPolicyVersion",
      "iam:TagPolicy", "iam:UntagPolicy",
    ]
    resources = [aws_iam_policy.task_boundary.arn, aws_iam_policy.deploy_services.arn, aws_iam_policy.deploy_shared.arn, aws_iam_policy.deploy_shared_services.arn]
  }
}

resource "aws_iam_role_policy" "deploy_service" {
  for_each = local.delivered_services
  role     = aws_iam_role.deploy_service[each.key].id
  policy   = data.aws_iam_policy_document.deploy_service[each.key].json
}

# The shared deploy role loses each service's own resources, as it lost Payments' (payments.tf).
data "aws_iam_policy_document" "deploy_shared_services" {
  statement {
    sid    = "NotTheServices"
    effect = "Deny"
    actions = [
      "ecs:CreateService", "ecs:UpdateService", "ecs:DeleteService", "ecs:RegisterTaskDefinition", "ecs:RunTask",
      "ecs:StartTask", "ecs:TagResource", "ecs:UntagResource",
    ]
    resources = flatten([for a in local.service_access : concat(a.task_defs, [a.service])])
  }
  statement {
    sid       = "NotTheServicesImages"
    effect    = "Deny"
    actions   = ["ecr:*"]
    resources = [for a in local.service_access : a.repository]
  }
  statement {
    sid       = "NotTheServicesRoles"
    effect    = "Deny"
    actions   = ["iam:*"]
    resources = [for a in local.service_access : a.roles]
  }
  statement {
    sid       = "NotTheServicesLogs"
    effect    = "Deny"
    actions   = ["logs:*"]
    resources = flatten([for a in local.service_access : [a.log_group, "${a.log_group}:*"]])
  }
}

resource "aws_iam_policy" "deploy_shared_services" {
  name   = "${var.project}-github-deploy-shared-services"
  policy = data.aws_iam_policy_document.deploy_shared_services.json
}

resource "aws_iam_role_policy_attachment" "deploy_shared_services" {
  role       = aws_iam_role.deploy.name
  policy_arn = aws_iam_policy.deploy_shared_services.arn
}

output "service_ecr_repository_urls" {
  value = { for name, repository in aws_ecr_repository.service : name => repository.repository_url }
}

output "service_deploy_role_arns" {
  description = "Set each as the AWS_<NAME>_DEPLOY_ROLE_ARN variable of the GitHub repository."
  value       = { for name, role in aws_iam_role.deploy_service : name => role.arn }
}
