# The Payments service on the platform's service module (lesson s5-l01): the module gives it its
# roles, task definitions, log group, ECS service and running-tasks alarm. Payments adds its secrets,
# its broker settings, its internal port for the card-setup command (s4-l03), and, as the only service
# with a cluster of its own (s4-l07), that cluster's owner for its migrate step.

module "service" {
  source = "../../modules/service"

  project  = var.project
  region   = var.region
  name     = "payments"
  assembly = "CrowdFunding.Payments.Host.dll"
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
  security_group_id = local.shared.payments_security_group_id

  image         = var.image
  service_image = var.service_image
  desired_count = var.desired_count
  cpu           = 512
  memory        = 1024

  secrets = [
    { name = "ConnectionStrings__Payments", valueFrom = local.shared.payments_connection_secret_arn },
    { name = "Payments__WebhookSecret", valueFrom = local.shared.payments_webhook_secret_arn },
    { name = "ConnectionStrings__Valkey", valueFrom = local.shared.payments_valkey_secret_arn },
  ]
  # Payments' cluster is its own, so its migrate step logs in as that cluster's owner, which owns
  # every object of the schema: no hand-over to a migration role is needed there.
  migrate_secrets = [
    { name = "ConnectionStrings__Owner", valueFrom = local.shared.payments_database_owner_secret_arn },
  ]

  # The consumers of Payments' events run elsewhere, so every route of its events is Broker; it reads
  # the payments queue, publishes to its own topic, and checks that topic's subscription to the
  # monolith's queue, which it knows by ARN only (s4-l10).
  environment = [
    { name = "Payments__Budget__Prefix", value = local.shared.psp_budget_prefix },
    # Only the monolith's service identity may call the internal endpoint (s4-l04).
    { name = "Auth__ServiceClients__0", value = local.shared.monolith_service_client_id },
    { name = "Events__Routes__Pledges", value = "Broker" },
    { name = "Events__Routes__Campaigns", value = "Broker" },
    { name = "Events__Routes__Notifications", value = "Broker" },
    { name = "Events__Broker__Region", value = var.region },
    { name = "Events__Broker__Queues__payments__Arn", value = local.shared.payments_queue_arn },
    { name = "Events__Broker__Queues__payments__Url", value = local.shared.payments_queue_url },
    { name = "Events__Broker__Queues__monolith__Arn", value = local.shared.monolith_queue_arn },
    { name = "Events__Broker__Read__0", value = "payments" },
    { name = "Events__Broker__Topics__payments", value = local.shared.payments_topic_arn },
  ]
  queue_arn = local.shared.payments_queue_arn
  topic_arn = local.shared.payments_topic_arn

  # The proxy in each client task waits at most 2 s for a reply on 8081, the remote client's attempt
  # (lesson s4-a04's time budget; the default is 15 s). It retries a failed connection twice on another
  # task: AWS manages that and it cannot be turned off; the card setup is idempotent per pledge.
  internal_port = { per_request_timeout_seconds = 2 }
}

# The resources the root declared itself until s5-l01, now the module's: the same objects, not new ones.
moved {
  from = aws_iam_role.execution
  to   = module.service.aws_iam_role.execution
}

moved {
  from = aws_iam_role_policy_attachment.execution
  to   = module.service.aws_iam_role_policy_attachment.execution
}

moved {
  from = aws_iam_role_policy.execution_secrets
  to   = module.service.aws_iam_role_policy.execution_secrets
}

moved {
  from = aws_iam_role.migrate_execution
  to   = module.service.aws_iam_role.migrate_execution
}

moved {
  from = aws_iam_role_policy_attachment.migrate_execution
  to   = module.service.aws_iam_role_policy_attachment.migrate_execution
}

moved {
  from = aws_iam_role_policy.migrate_execution_secrets
  to   = module.service.aws_iam_role_policy.migrate_execution_secrets
}

moved {
  from = aws_iam_role.task
  to   = module.service.aws_iam_role.task
}

moved {
  from = aws_iam_role_policy.task
  to   = module.service.aws_iam_role_policy.task
}

moved {
  from = aws_cloudwatch_log_group.payments
  to   = module.service.aws_cloudwatch_log_group.service
}

moved {
  from = aws_ecs_task_definition.payments
  to   = module.service.aws_ecs_task_definition.service
}

moved {
  from = aws_ecs_task_definition.migrate
  to   = module.service.aws_ecs_task_definition.migrate
}

moved {
  from = aws_ecs_service.payments
  to   = module.service.aws_ecs_service.service
}

moved {
  from = aws_cloudwatch_metric_alarm.running_tasks
  to   = module.service.aws_cloudwatch_metric_alarm.running_tasks
}
