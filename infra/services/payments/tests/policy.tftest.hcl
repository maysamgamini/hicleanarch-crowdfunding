# CQA-2120: the task role's rendered policy, not the inputs it is built from. aws_iam_policy_document is
# rendered by the provider itself, with no AWS call, so this file uses the real provider, with mock
# credentials, every endpoint pointed at a closed local port (a call that slipped through would fail, not
# reach AWS) and the account and the shared layer's outputs overridden. plan.tftest.hcl mocks the provider
# and so sees only the inputs.

provider "aws" {
  region                      = "us-east-1"
  access_key                  = "mock"
  secret_key                  = "mock"
  skip_credentials_validation = true
  skip_requesting_account_id  = true
  skip_metadata_api_check     = true
  skip_region_validation      = true

  endpoints {
    sts            = "http://127.0.0.1:1"
    s3             = "http://127.0.0.1:1"
    iam            = "http://127.0.0.1:1"
    ecs            = "http://127.0.0.1:1"
    logs           = "http://127.0.0.1:1"
    cloudwatch     = "http://127.0.0.1:1"
    secretsmanager = "http://127.0.0.1:1"
  }
}

override_data {
  target = data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = module.service.data.aws_caller_identity.current
  values = { account_id = "123456789012" }
}

override_data {
  target = data.aws_s3_object.shared
  values = {
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

variables {
  service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-payments:mock"
}

run "the_task_policy_names_only_payments_queue_and_topic" {
  command = plan

  assert {
    # Every ARN of the rendered policy: its own queue, its own topic and that topic's subscriptions, and
    # "*" for telemetry only.
    condition = toset(flatten([for st in jsondecode(module.service.task_policy).Statement : st.Resource])) == toset([
      "*",
      "arn:aws:sqs:us-east-1:123456789012:hicleanarch-payments",
      "arn:aws:sns:us-east-1:123456789012:hicleanarch-payments",
      "arn:aws:sns:us-east-1:123456789012:hicleanarch-payments:*",
    ])
    error_message = "The task policy names a resource that is not Payments' own: ${module.service.task_policy}"
  }

  assert {
    condition = alltrue([for st in jsondecode(module.service.task_policy).Statement :
      st.Resource != "*" || alltrue([for a in flatten([st.Action]) : startswith(a, "xray:") || startswith(a, "logs:") || a == "cloudwatch:PutMetricData"])
    ])
    error_message = "Only telemetry may name every resource."
  }

  assert {
    condition = toset(flatten([for st in jsondecode(module.service.task_policy).Statement : [for a in flatten([st.Action]) : a if startswith(a, "sqs:") || startswith(a, "sns:")]])) == toset([
      "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility",
      "sns:Publish", "sns:ListSubscriptionsByTopic", "sns:GetSubscriptionAttributes",
    ])
    error_message = "The task may read its queue, publish to its topic and check that topic's subscriptions, and nothing more."
  }

  assert {
    # The consumers' queues are addresses in the task definition, never resources of the policy (CQA-2115).
    condition     = !anytrue([for q in ["pledges", "notifications", "campaigns"] : strcontains(module.service.task_policy, "hicleanarch-${q}")])
    error_message = "The task policy names another service's queue."
  }
}

run "the_execution_roles_read_only_payments_secrets" {
  command = plan

  assert {
    condition = toset(flatten([for st in jsondecode(module.service.execution_secrets_policy).Statement : st.Resource])) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments-AbCdEf",
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:webhook-AbCdEf",
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-AbCdEf",
    ])
    error_message = "The execution role's rendered policy reads a secret that is not Payments': ${module.service.execution_secrets_policy}"
  }
}
