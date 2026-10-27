# The Campaigns service's own root (lesson s5-l08), planned against a mocked AWS provider and a stand-in
# for the shared layer's published outputs (env/shared.json): no account, no calls. It reads only its own
# secrets, its migrate step logs in as its migration role and never as the owner, it may read its own queue
# and publish to its own topic and nothing else, and it runs in its own security group with Service
# Connect and the deployment circuit breaker.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = { account_id = "123456789012" }
  }

  mock_data "aws_iam_policy_document" {
    defaults = { json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}" }
  }

  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::123456789012:role/mock" }
  }

  mock_data "aws_s3_object" {
    defaults = {
      body = <<-JSON
        {
          "region": "us-east-1",
          "cluster_name": "hicleanarch-sandbox",
          "cluster_arn": "arn:aws:ecs:us-east-1:123456789012:cluster/hicleanarch-sandbox",
          "vpc_cidr": "10.0.0.0/16",
          "task_subnet_ids": ["subnet-a", "subnet-b"],
          "service_connect_namespace_arn": "arn:aws:servicediscovery:us-east-1:123456789012:namespace/ns-mock",
          "collector_image": "public.ecr.aws/aws-observability/aws-otel-collector:v0.49.0",
          "cognito_issuer": "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_mock",
          "user_pool_client_id": "client-mock",
          "alarm_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-alarms",
          "campaigns_security_group_id": "sg-campaigns",
          "campaigns_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns",
          "campaigns_read_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_read",
          "campaigns_valkey_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/valkey/campaigns",
          "campaigns_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-campaigns",
          "campaigns_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-campaigns",
          "campaigns_queue_url": "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-campaigns",
          "pledges_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-pledges",
          "notifications_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-notifications",
          "payments_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-payments",
          "migrator_secret_arns": {
            "notifications": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications_migrator",
            "campaigns": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_migrator",
            "pledges": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_migrator"
          }
        }
      JSON
    }
  }
}

variables {
  image         = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:abc"
  service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:abc"
}

run "campaigns_service" {
  command = plan

  assert {
    condition = toset(module.service.secret_arns) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_read", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/valkey/campaigns",
    ])
    error_message = "The service reads its own connection strings and its own Valkey user, and no other secret."
  }

  assert {
    condition = toset(module.service.migrate_secret_arns) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_read", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/valkey/campaigns", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_migrator",
    ])
    error_message = "The migrate step reads the service's secrets and its migration role's, never the owner's."
  }

  assert {
    condition = toset(module.service.queue_and_topic) == toset([
      "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-campaigns", "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-campaigns",
    ])
    error_message = "The task reads its own queue and publishes to its own topic, and no other."
  }

  assert {
    condition = (module.service.ecs_service.network_configuration[0].security_groups == toset(["sg-campaigns"])
    && module.service.ecs_service.deployment_circuit_breaker[0].rollback)
    error_message = "The service runs in its own security group and rolls a failed deploy back."
  }

  assert {
    condition     = module.service.task_definitions_kept
    error_message = "Task definitions are kept (skip_destroy): no deploy role may deregister them."
  }
}

run "first_apply_of_a_deploy_creates_no_service" {
  command = plan

  variables {
    service_image = ""
  }

  assert {
    condition     = module.service.ecs_service == null
    error_message = "The first deploy registers the task definitions and runs the migration before any service exists."
  }
}

# Lesson s5-l14: a deployment rolls back on either detector. The circuit breaker for tasks that never
# become healthy; the deployment alarms for tasks that are healthy but answer 5xx or fail their events.
run "a_release_rolls_back_on_either_detector" {
  command = plan

  assert {
    condition = (module.service.ecs_service.deployment_circuit_breaker[0].rollback
      && module.service.ecs_service.alarms[0].enable && module.service.ecs_service.alarms[0].rollback
    && module.service.ecs_service.alarms[0].alarm_names == toset(["hicleanarch-sandbox-campaigns-deployment-5xx", "hicleanarch-sandbox-campaigns-deployment-handler-failures"]))
    error_message = "ECS watches the 5xx share and, for a consumer, the handler failures, and rolls back on either."
  }

  assert {
    condition     = [for e in module.service.container.environment : e.value if e.name == "SERVICE_VERSION"] == ["abc"]
    error_message = "GET /version answers the image tag the service runs."
  }
}

# CQA-2103: the first apply of a deploy keeps the service on the task definition it runs, so the old
# image never starts with the release's settings; the second apply passes none, and the service takes
# the release's.
run "the_first_apply_keeps_the_running_task_definition" {
  command = plan

  variables {
    image                   = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:new"
    service_image           = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:abc"
    service_task_definition = "arn:aws:ecs:us-east-1:123456789012:task-definition/hicleanarch-campaigns:41"
  }

  assert {
    condition     = module.service.ecs_service.task_definition == "arn:aws:ecs:us-east-1:123456789012:task-definition/hicleanarch-campaigns:41"
    error_message = "The first apply must keep the service on the task definition it runs."
  }
}

run "the_second_apply_moves_to_the_release" {
  command = apply

  variables {
    image         = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:new"
    service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-campaigns:new"
  }

  assert {
    condition     = output.service_task_definition != "arn:aws:ecs:us-east-1:123456789012:task-definition/hicleanarch-campaigns:41" && output.service_task_definition != null
    error_message = "With no task definition to keep, the service takes the release's, and the next deploy reads it from the output."
  }

  assert {
    condition     = [for e in module.service.container.environment : e.value if e.name == "SERVICE_VERSION"] == ["new"]
    error_message = "The release's task definition reports the release's tag."
  }
}

# CQA-2115: the start-up subscription check of a producer needs the ARN of every queue its topic feeds
# (events/broker-subscriptions.json, which messaging.tf builds the subscriptions from). Without one, the
# service refuses to start in AWS, while the tests and the local stack, which give every host every queue,
# never show it. The ARN is an address only; the task role still names no other service's queue.
run "names_every_consumer_queue_its_subscription_check_reads" {
  command = plan

  assert {
    condition = alltrue([
      for s in jsondecode(file("../../../events/broker-subscriptions.json")).subscriptions :
      length([for e in module.service.container.environment : e if e.name == "Events__Broker__Queues__${s.queue}__Arn" && endswith(e.value, "-${s.queue}")]) == 1
      if s.topic == "campaigns"
    ])
    error_message = "The task definition lacks the ARN of a queue the campaigns topic feeds, which its subscription check reads."
  }

  assert {
    condition     = length([for e in module.service.container.environment : e if e.name == "Events__Broker__Topics__campaigns"]) == 1
    error_message = "The task definition names no campaigns topic, which its subscription check and its publisher need."
  }
}

# CQA-2118: the service pages on what tests/load/slos.json says it promises (decision service-slos), the
# file the load tests and scripts/measure-services.sh read: availability, the latency endpoint's P99 as a
# threshold 99% of requests answer within, and the age its queue's oldest message may reach.
run "pages_on_the_objectives_slos_json_names" {
  command = plan

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.target == jsondecode(file("../../../tests/load/slos.json")).services.campaigns.availability if w.objective == "availability"])
    error_message = "The availability pages are not on slos.json's availability for campaigns."
  }

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.target == 0.99 && w.threshold_ms == jsondecode(file("../../../tests/load/slos.json")).services.campaigns.latency.p99Ms if w.objective == "latency"])
    error_message = "The latency pages are not on slos.json's P99 for campaigns."
  }

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.max_age_seconds == jsondecode(file("../../../tests/load/slos.json")).services.campaigns.lagSeconds if w.objective == "lag-events"])
    error_message = "The lag pages are not on slos.json's lagSeconds for campaigns."
  }

  assert {
    condition     = length([for w in module.service.burn_windows : w if contains(["availability", "latency", "lag-events"], w.objective)]) == 12
    error_message = "Each of the three objectives has its four windows."
  }
}

# CQA-2124: a release that drops a secret (as s5-l09 dropped the monolith's Campaigns secrets) must not take
# it from the execution role while the revision the first apply keeps, or a rollback, still names it. The
# deploy script passes the previous release's secrets, and the role keeps them for one more release.
run "the_execution_role_keeps_the_previous_releases_secrets_for_one_release" {
  command = plan

  variables {
    service_task_definition = "arn:aws:ecs:us-east-1:123456789012:task-definition/hicleanarch-campaigns:7"
    kept_secret_arns        = ["arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_old"]
  }

  assert {
    condition     = contains(module.service.secret_arns, "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_old") && length(module.service.secret_arns) == 4
    error_message = "The execution role reads this release's secrets and the previous release's, and no other."
  }

  assert {
    condition     = !contains(output.release_secret_arns, "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_old") && length(output.release_secret_arns) == 3
    error_message = "The next deploy keeps only this release's own secrets, so a dropped one leaves a release later."
  }

  assert {
    condition     = !anytrue([for s in module.service.container.secrets : endswith(s.valueFrom, "campaigns_old")])
    error_message = "The release's task definition does not name the kept secret."
  }
}
