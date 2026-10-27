# Plans this root against a mock AWS provider (OpenTofu 1.8+): no credentials and no AWS calls.
# It catches what validate cannot, such as an expression that fails only once values are known.
# Run from this directory: tofu init -backend=false && tofu test

mock_provider "aws" {
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
    }
  }

  # The shared layer's outputs document (infra/env/shared-outputs.tf), every key this root reads.
  mock_data "aws_s3_object" {
    defaults = {
      body = <<-JSON
        {
          "region": "us-east-1",
          "cluster_name": "hicleanarch",
          "cluster_arn": "arn:aws:ecs:us-east-1:123456789012:cluster/hicleanarch",
          "vpc_cidr": "10.40.0.0/16",
          "task_subnet_ids": ["subnet-0123456789abcdef0", "subnet-0123456789abcdef1"],
          "service_connect_namespace_arn": "arn:aws:servicediscovery:us-east-1:123456789012:namespace/ns-mock",
          "collector_image": "public.ecr.aws/aws-observability/aws-otel-collector:v0.49.0",
          "cognito_issuer": "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_Mock",
          "user_pool_client_id": "mockclient",
          "alarm_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-alarms",
          "event_topic_arns": {
            "campaigns": "arn:aws:sns:us-east-1:123456789012:hicleanarch-campaigns",
            "payments": "arn:aws:sns:us-east-1:123456789012:hicleanarch-payments",
            "pledges": "arn:aws:sns:us-east-1:123456789012:hicleanarch-pledges"
          },
          "pledges_service_client_id": "mockpledges",
          "psp_budget_prefix": "hicleanarch",
          "psp_budget_tasks": 2,
          "payments_max_count": 2,
          "payments_security_group_id": "sg-0123456789abcdef0",
          "payments_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments-AbCdEf",
          "payments_webhook_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:webhook-AbCdEf",
          "payments_valkey_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-AbCdEf",
          "pledges_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-pledges",
          "notifications_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-notifications",
          "campaigns_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-campaigns",
          "payments_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-payments",
          "payments_queue_url": "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-payments",
          "payments_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-payments",
          "payments_database_owner_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:owner-AbCdEf",
          "shared_owner_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:owner-AbCdEf",
          "shared_payments_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments-AbCdEf",
          "payments_own_owner_secret_arn": "",
          "payments_own_secret_arn": ""
        }
      JSON
    }
  }

  # The provider checks ARNs even in a plan, so the mocks return well-formed ones.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::123456789012:role/mock"
    }
  }
}

run "plans_with_the_defaults" {
  command = plan
}

run "plans_a_running_service" {
  command = plan

  variables {
    service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-payments:mock"
  }
}

# The service module gives Payments its own access and nothing else's (lesson s5-l01).
run "its_roles_name_only_its_own_secrets_queue_and_topic" {
  command = plan

  assert {
    condition = toset(module.service.secret_arns) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments-AbCdEf",
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:webhook-AbCdEf",
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-AbCdEf",
    ])
    error_message = "The execution role reads a secret that is not Payments'."
  }

  assert {
    condition     = contains(module.service.migrate_secret_arns, "arn:aws:secretsmanager:us-east-1:123456789012:secret:owner-AbCdEf") && length(module.service.migrate_secret_arns) == 4
    error_message = "The migrate task reads more than Payments' secrets and its cluster's owner."
  }

  assert {
    condition     = alltrue([for arn in module.service.queue_and_topic : strcontains(arn, ":hicleanarch-payments")]) && length(module.service.queue_and_topic) == 2
    error_message = "The task role names another service's queue or topic."
  }
}

run "refuses_more_tasks_than_share_the_psp_budget" {
  command = plan

  variables {
    service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-payments:mock"
    desired_count = 3
  }

  expect_failures = [terraform_data.psp_budget_share]
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
      if s.topic == "payments"
    ])
    error_message = "The task definition lacks the ARN of a queue the payments topic feeds, which its subscription check reads."
  }

  assert {
    condition     = length([for e in module.service.container.environment : e if e.name == "Events__Broker__Topics__payments"]) == 1
    error_message = "The task definition names no payments topic, which its subscription check and its publisher need."
  }
}

# CQA-2118: the service pages on what tests/load/slos.json says it promises (decision service-slos), the
# file the load tests and scripts/measure-services.sh read: availability, the latency endpoint's P99 as a
# threshold 99% of requests answer within, and the age its queue's oldest message may reach.
run "pages_on_the_objectives_slos_json_names" {
  command = plan

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.target == jsondecode(file("../../../tests/load/slos.json")).services.payments.availability if w.objective == "availability"])
    error_message = "The availability pages are not on slos.json's availability for payments."
  }

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.target == 0.99 && w.threshold_ms == jsondecode(file("../../../tests/load/slos.json")).services.payments.latency.p99Ms if w.objective == "latency"])
    error_message = "The latency pages are not on slos.json's P99 for payments."
  }

  assert {
    condition     = alltrue([for w in module.service.burn_windows : w.max_age_seconds == jsondecode(file("../../../tests/load/slos.json")).services.payments.lagSeconds if w.objective == "lag-events"])
    error_message = "The lag pages are not on slos.json's lagSeconds for payments."
  }

  assert {
    condition     = length([for w in module.service.burn_windows : w if contains(["availability", "latency", "lag-events"], w.objective)]) == 12
    error_message = "Each of the three objectives has its four windows."
  }
}

# CQA-2123: the rollback alarm reads the 5xx count under TargetDiscoveryName, where Service Connect
# publishes it, and the requests under DiscoveryName.
run "the_rollback_alarm_reads_each_metric_where_it_is_published" {
  command = plan

  assert {
    condition = (module.service.deployment_5xx_metrics.errors == { HTTPCode_Target_5XX_Count = tomap({ TargetDiscoveryName = "payments-public" }) }
    && module.service.deployment_5xx_metrics.requests.RequestCount.DiscoveryName == "payments-public")
    error_message = "The rollback alarm reads a metric under dimensions it is not published under."
  }
}
