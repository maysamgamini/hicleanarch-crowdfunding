# What the shared layer hands to the services' own Terraform roots (s4-l09). Payments' stack reads this
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
    # Each producing module's topic, by its plan name (campaigns, payments, pledges).
    event_topic_arns = { for t, topic in aws_sns_topic.events : t => topic.arn }
    # The one service identity Payments' internal endpoint lets in (s4-l04).
    monolith_service_client_id = aws_cognito_user_pool_client.monolith_service.id
    # One budget at the PSP while both hosts run Payments' code (s4-l05).
    psp_budget_prefix = local.psp_budget_prefix

    payments_security_group_id = aws_security_group.payments.id
    # The shared cluster, or Payments' own once the move has switched (s4-l07).
    payments_connection_secret_arn = local.payments_connection_secret
    payments_webhook_secret_arn    = aws_secretsmanager_secret.webhook.arn
    payments_valkey_secret_arn     = aws_secretsmanager_secret.valkey["payments"].arn
    payments_queue_arn             = aws_sqs_queue.events["payments"].arn
    payments_queue_url             = aws_sqs_queue.events["payments"].id
    payments_topic_arn             = aws_sns_topic.events["payments"].arn
    # Who may create Payments' schema and role: its own cluster's master once it has one (s4-l07),
    # the shared cluster's owner until then. Read only by Payments' migration task.
    payments_database_owner_secret_arn = var.payments_database == "own" ? aws_secretsmanager_secret.payments_own["owner"].arn : aws_secretsmanager_secret.connection["owner"].arn
    # For the move-payments task only (s4-l07): the shared cluster's owner fences and prunes, and the
    # clone's secrets exist while the clone does.
    shared_owner_secret_arn       = aws_secretsmanager_secret.connection["owner"].arn
    shared_payments_secret_arn    = aws_secretsmanager_secret.connection["payments"].arn
    payments_own_owner_secret_arn = var.payments_database_clone ? aws_secretsmanager_secret.payments_own["owner"].arn : ""
    payments_own_secret_arn       = var.payments_database_clone ? aws_secretsmanager_secret.payments_own["payments"].arn : ""
  }
}

resource "aws_s3_object" "shared_outputs" {
  bucket       = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}"
  key          = "env/shared.json"
  content      = jsonencode(local.shared_outputs)
  content_type = "application/json"
}
