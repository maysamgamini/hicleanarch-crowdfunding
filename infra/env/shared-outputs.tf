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
    # The monolith's queue, by ARN only: Payments' subscription check looks for its topic's
    # subscription to it (s4-l10). Payments' roles grant nothing on it.
    monolith_queue_arn = aws_sqs_queue.events["monolith"].arn
    # The one service identity Payments' internal endpoint lets in (s4-l04).
    monolith_service_client_id = aws_cognito_user_pool_client.monolith_service.id
    # The PSP budget's bucket names in Valkey.
    psp_budget_prefix = local.psp_budget_prefix

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
  }
}

resource "aws_s3_object" "shared_outputs" {
  bucket       = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}"
  key          = "env/shared.json"
  content      = jsonencode(local.shared_outputs)
  content_type = "application/json"
}
