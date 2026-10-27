# Notifications reads its queue, looks a user's address up in Cognito and sends email through SES (lesson
# s5-l03). It publishes nothing, serves one endpoint (PUT /v1/me/notifications/{kind}, through the gateway)
# and keeps nothing in Valkey. The service module gives it its roles, task definitions, log group and ECS
# service; its migrate step logs in as its migration role, which owns its schema after the hand-over.
module "service" {
  source = "../../modules/service"

  project  = var.project
  region   = var.region
  name     = "notifications"
  assembly = "CrowdFunding.Notifications.Host.dll"
  shared = {
    cluster_name                  = local.shared.cluster_name
    cluster_arn                   = local.shared.cluster_arn
    vpc_cidr                      = local.shared.vpc_cidr
    task_subnet_ids               = local.shared.task_subnet_ids
    service_connect_namespace_arn = local.shared.service_connect_namespace_arn
    collector_image               = local.shared.collector_image
    cognito_issuer                = local.shared.cognito_issuer
    user_pool_client_id           = local.shared.user_pool_client_id
    alarm_topic_arn               = local.shared.alarm_topic_arn
  }
  security_group_id = local.shared.notifications_security_group_id

  image         = var.image
  service_image = var.service_image
  # CQA-2103: the first apply of a deploy keeps the task definition the service runs.
  service_task_definition = var.service_task_definition
  desired_count           = var.desired_count

  secrets = [
    { name = "ConnectionStrings__Notifications", valueFrom = local.shared.notifications_connection_secret_arn },
  ]
  migrate_secrets = [
    { name = "ConnectionStrings__Migrator", valueFrom = local.shared.migrator_secret_arns.notifications },
  ]

  # Its events come from the other services through its own queue (s5-l02): every route into it is
  # Broker, and it reads that queue alone.
  environment = [
    { name = "Notifications__Sender", value = local.shared.notifications_from == "" ? "InMemory" : "Ses" },
    { name = "Notifications__FromAddress", value = local.shared.notifications_from },
    { name = "Notifications__UserPoolId", value = local.shared.user_pool_id },
    { name = "Events__Routes__Notifications", value = "Broker" },
    { name = "Events__Broker__Region", value = var.region },
    { name = "Events__Broker__Queues__notifications__Arn", value = local.shared.notifications_queue_arn },
    { name = "Events__Broker__Queues__notifications__Url", value = local.shared.notifications_queue_url },
    { name = "Events__Broker__Read__0", value = "notifications" },
  ]
  queue_arn = local.shared.notifications_queue_arn

  task_policy_json = data.aws_iam_policy_document.task.json
}

data "aws_iam_policy_document" "task" {
  statement {
    sid       = "LookUpAddresses"
    actions   = ["cognito-idp:ListUsers"]
    resources = [local.shared.user_pool_arn]
  }

  dynamic "statement" {
    for_each = length(local.shared.ses_identity_arns) == 0 ? [] : [1]
    content {
      sid       = "SendEmail"
      actions   = ["ses:SendEmail"]
      resources = local.shared.ses_identity_arns
    }
  }
}
