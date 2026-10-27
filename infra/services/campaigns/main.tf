# Campaigns serves its endpoints through the gateway, keeps its catalog cache in Valkey as its own user
# (~campaigns:*), publishes its events to its own topic and reads its own queue (lesson s5-l08). The service
# module gives it its roles, task definitions, log group and ECS service; its migrate step logs in as its
# migration role, which owns its schema after the hand-over.
module "service" {
  source = "../../modules/service"

  project  = var.project
  region   = var.region
  name     = "campaigns"
  assembly = "CrowdFunding.Campaigns.Host.dll"
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
  security_group_id = local.shared.campaigns_security_group_id

  image         = var.image
  service_image = var.service_image
  # CQA-2103: the first apply of a deploy keeps the task definition the service runs.
  service_task_definition = var.service_task_definition
  # CQA-2124: the execution role keeps the previous release's secrets for one more release.
  kept_secret_arns = var.kept_secret_arns
  desired_count    = var.desired_count

  secrets = [
    { name = "ConnectionStrings__Campaigns", valueFrom = local.shared.campaigns_connection_secret_arn },
    { name = "ConnectionStrings__CampaignsRead", valueFrom = local.shared.campaigns_read_connection_secret_arn },
    { name = "ConnectionStrings__Valkey", valueFrom = local.shared.campaigns_valkey_secret_arn },
  ]
  migrate_secrets = [
    { name = "ConnectionStrings__Migrator", valueFrom = local.shared.migrator_secret_arns.campaigns },
  ]

  # Every other module runs elsewhere: what Campaigns publishes for them goes to its topic, and what they
  # publish for it comes through its own queue. Its own events (the catalog's cache invalidation) stay in
  # process. Its subscription check looks for its topic's subscriptions to the consumers' queues.
  environment = [
    { name = "Events__Routes__Pledges", value = "Broker" },
    { name = "Events__Routes__Payments", value = "Broker" },
    { name = "Events__Routes__Notifications", value = "Broker" },
    { name = "Events__Broker__Region", value = var.region },
    { name = "Events__Broker__Topics__campaigns", value = local.shared.campaigns_topic_arn },
    { name = "Events__Broker__Queues__campaigns__Arn", value = local.shared.campaigns_queue_arn },
    { name = "Events__Broker__Queues__campaigns__Url", value = local.shared.campaigns_queue_url },
    { name = "Events__Broker__Queues__pledges__Arn", value = local.shared.pledges_queue_arn },
    { name = "Events__Broker__Queues__notifications__Arn", value = local.shared.notifications_queue_arn },
    { name = "Events__Broker__Queues__payments__Arn", value = local.shared.payments_queue_arn },
    { name = "Events__Broker__Read__0", value = "campaigns" },
  ]

  # What it promises, as tests/load/slos.json (decision service-slos) says, and pages on (CQA-2118); the
  # module's defaults (99.9%, 1,000 ms, 60 s) are not its objectives.
  # For the catalog page, from its cache: 99.9% of its public requests without a
  # 5xx, 99% of them within 300 ms.
  objectives = {
    availability = 0.999
    latency      = { threshold_ms = 300, target = 0.99 }
    lag          = { max_age_seconds = 60, target = 0.99 }
  }

  queue_arn = local.shared.campaigns_queue_arn
  topic_arn = local.shared.campaigns_topic_arn
}
