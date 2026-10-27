# The Pledges service (lesson s5-l10): the old monolith's host, which runs Pledges alone. The gateway's
# catch-all sends it every path no other service serves; it sends Payments StartCardSetup over HTTP with its
# own service identity, publishes its events to its own topic and reads its own queue. Its own events
# (the totals' aggregator) stay in process. The service module gives it its roles, task definitions, log
# group and ECS service; its migrate step logs in as pledges_migrator, which owns its schema after the hand-over.
module "service" {
  source = "../../modules/service"

  project  = var.project
  region   = var.region
  name     = "pledges"
  assembly = "CrowdFunding.Pledges.Host.dll"
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
  security_group_id = local.shared.pledges_security_group_id

  image         = var.image
  service_image = var.service_image
  desired_count = local.shared.pledges_desired_count
  # The old monolith's size (CQA-2106): its pools, its jobs and the Service Connect proxy.
  cpu    = 1024
  memory = 2048

  secrets = [
    { name = "ConnectionStrings__Pledges", valueFrom = local.shared.pledges_connection_secret_arn },
    { name = "ConnectionStrings__PledgesJobs", valueFrom = local.shared.pledges_jobs_connection_secret_arn },
    { name = "ConnectionStrings__PledgesRead", valueFrom = local.shared.pledges_read_connection_secret_arn },
    { name = "ConnectionStrings__Valkey", valueFrom = local.shared.pledges_valkey_secret_arn },
    { name = "ServiceIdentity__ClientSecret", valueFrom = local.shared.pledges_service_client_secret_arn },
  ]
  migrate_secrets = [
    { name = "ConnectionStrings__Migrator", valueFrom = local.shared.migrator_secret_arns.pledges },
  ]

  environment = [
    # StartCardSetup goes to Payments' internal port through Service Connect (s4-l03, s4-l05).
    { name = "Services__Payments__BaseAddress", value = "http://payments:8081" },
    { name = "ServiceIdentity__ClientId", value = local.shared.pledges_service_client_id },
    { name = "ServiceIdentity__TokenEndpoint", value = local.shared.service_token_endpoint },
    { name = "Pledges__FundingCancel__Open", value = tostring(var.funding_cancel_open) },
    # Every other module runs elsewhere: Payments' events in and out, and Pledges' events for Campaigns and
    # Notifications, go through the broker; Pledges' own stay in process.
    { name = "Events__Routes__Payments", value = "Broker" },
    { name = "Events__Routes__Campaigns", value = "Broker" },
    { name = "Events__Routes__Notifications", value = "Broker" },
    { name = "Events__Broker__Region", value = var.region },
    { name = "Events__Broker__VisibilityTimeoutSeconds", value = tostring(local.shared.visibility_timeout_seconds) },
    { name = "Events__Broker__Topics__pledges", value = local.shared.pledges_topic_arn },
    { name = "Events__Broker__Queues__pledges__Arn", value = local.shared.pledges_queue_arn },
    { name = "Events__Broker__Queues__pledges__Url", value = local.shared.pledges_queue_url },
    { name = "Events__Broker__Queues__campaigns__Arn", value = local.shared.campaigns_queue_arn },
    { name = "Events__Broker__Queues__notifications__Arn", value = local.shared.notifications_queue_arn },
    { name = "Events__Broker__Queues__payments__Arn", value = local.shared.payments_queue_arn },
    { name = "Events__Broker__Read__0", value = "pledges" },
  ]
  queue_arn = local.shared.pledges_queue_arn
  topic_arn = local.shared.pledges_topic_arn
}
