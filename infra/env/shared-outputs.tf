# What the shared layer hands to the services' own OpenTofu roots (s4-l09). Payments' stack reads this
# one document, not this layer's state: the state holds values Payments must not see, such as the
# monolith's service client secret, and reading it would need the right to read all of it.
#
# The object sits next to the state, in the bootstrap layer's bucket (named after the account), and is
# deleted with the environment, so "is the sandbox up?" is "does this object exist?". A key added here
# is read in infra/services/payments as local.shared.<key>; a test checks every key it reads is here.

locals {
  shared_outputs = {
    region                        = var.region
    cluster_name                  = aws_ecs_cluster.main.name
    cluster_arn                   = aws_ecs_cluster.main.arn
    vpc_cidr                      = aws_vpc.main.cidr_block
    task_subnet_ids               = aws_subnet.public[*].id
    service_connect_namespace_arn = aws_service_discovery_http_namespace.services.arn
    collector_image               = var.collector_image
    cognito_issuer                = local.cognito_issuer
    user_pool_client_id           = aws_cognito_user_pool_client.api.id
    alarm_topic_arn               = aws_sns_topic.alarms.arn
    # The Pledges service's queue (the monolith's until s5-l10), by ARN for the other services' subscription
    # checks: they look for their topics' subscriptions to it. Their roles grant nothing on it.
    pledges_queue_arn = aws_sqs_queue.events["pledges"].arn
    # The one service identity Payments' internal endpoint lets in (s4-l04): the Pledges service's.
    pledges_service_client_id = aws_cognito_user_pool_client.pledges_service.id
    # The PSP budget's bucket names in Valkey.
    psp_budget_prefix = local.psp_budget_prefix
    # The fleet that shares it, and Payments' part of that fleet: each task's share while Valkey is down.
    psp_budget_tasks   = local.psp_budget_tasks
    payments_max_count = var.payments_max_count

    payments_security_group_id = aws_security_group.payments.id
    # Payments' own cluster (payments-database.tf).
    payments_connection_secret_arn = local.payments_connection_secret
    payments_webhook_secret_arn    = aws_secretsmanager_secret.webhook.arn
    payments_valkey_secret_arn     = aws_secretsmanager_secret.valkey["payments"].arn
    payments_queue_arn             = aws_sqs_queue.events["payments"].arn
    payments_queue_url             = aws_sqs_queue.events["payments"].id
    payments_topic_arn             = aws_sns_topic.events["payments"].arn
    # Who creates Payments' schema and login: its own cluster's master, read only by Payments' migrate
    # task (s4-l10). Never the shared cluster's owner: the monolith's migrate step prepares no Payments.
    payments_database_owner_secret_arn = aws_secretsmanager_secret.payments_own["owner"].arn

    # The Notifications service (s5-l03): its network place, its own secrets and queue, what it may read.
    notifications_security_group_id     = aws_security_group.notifications.id
    notifications_connection_secret_arn = aws_secretsmanager_secret.connection["notifications"].arn
    notifications_queue_arn             = aws_sqs_queue.events["notifications"].arn
    notifications_queue_url             = aws_sqs_queue.events["notifications"].id
    notifications_from                  = var.notifications_from
    user_pool_id                        = aws_cognito_user_pool.users.id
    user_pool_arn                       = aws_cognito_user_pool.users.arn
    ses_identity_arns                   = [for identity in aws_sesv2_email_identity.sender : identity.arn]

    # The Campaigns service (s5-l08): its network place, its secrets, its topic and queue, and the consumers'
    # queues its subscription check looks for on its topic.
    campaigns_security_group_id          = aws_security_group.campaigns.id
    campaigns_connection_secret_arn      = aws_secretsmanager_secret.connection["campaigns"].arn
    campaigns_read_connection_secret_arn = aws_secretsmanager_secret.read_connection["campaigns_read"].arn
    campaigns_valkey_secret_arn          = aws_secretsmanager_secret.valkey["campaigns"].arn
    campaigns_topic_arn                  = aws_sns_topic.events["campaigns"].arn
    campaigns_queue_arn                  = aws_sqs_queue.events["campaigns"].arn
    campaigns_queue_url                  = aws_sqs_queue.events["campaigns"].id

    # The Pledges service (s5-l10): its network place, its secrets, its topic and queue, its service identity,
    # the consumers' queues its subscription check looks for, and its size within the writer's connection budget.
    pledges_security_group_id          = aws_security_group.pledges.id
    pledges_connection_secret_arn      = aws_secretsmanager_secret.connection["pledges"].arn
    pledges_jobs_connection_secret_arn = aws_secretsmanager_secret.connection["pledges_jobs"].arn
    pledges_read_connection_secret_arn = aws_secretsmanager_secret.read_connection["pledges_read"].arn
    pledges_valkey_secret_arn          = aws_secretsmanager_secret.valkey["pledges"].arn
    pledges_topic_arn                  = aws_sns_topic.events["pledges"].arn
    pledges_queue_url                  = aws_sqs_queue.events["pledges"].id
    pledges_service_client_secret_arn  = aws_secretsmanager_secret.pledges_service_client.arn
    service_token_endpoint             = local.service_token_endpoint
    pledges_desired_count              = var.desired_count
    pledges_max_count                  = var.max_count
    visibility_timeout_seconds         = local.visibility_timeout_seconds

    # Each schema's migration role on the shared cluster (s5-l01), for the service that will own it.
    migrator_secret_arns = { for module, secret in aws_secretsmanager_secret.migrator : module => secret.arn }
  }
}

resource "aws_s3_object" "shared_outputs" {
  bucket       = local.state_bucket
  key          = "env/shared.json"
  content      = jsonencode(local.shared_outputs)
  content_type = "application/json"
}
