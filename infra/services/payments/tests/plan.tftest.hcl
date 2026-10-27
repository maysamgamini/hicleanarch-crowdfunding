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
          "monolith_service_client_id": "mockmonolith",
          "psp_budget_prefix": "hicleanarch",
          "payments_security_group_id": "sg-0123456789abcdef0",
          "payments_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments-AbCdEf",
          "payments_webhook_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:webhook-AbCdEf",
          "payments_valkey_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-AbCdEf",
          "monolith_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-monolith",
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
