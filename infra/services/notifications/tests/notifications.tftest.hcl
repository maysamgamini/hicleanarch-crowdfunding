# The Notifications service's own root (lesson s5-l03), planned against a mocked AWS provider and a
# stand-in for the shared layer's published outputs (env/shared.json): no account, no calls. It reads
# only its own secret, its migrate step logs in as its migration role and never as the owner, it may
# read its own queue, look users up and send email and nothing else, and it runs in its own security
# group with Service Connect and the deployment circuit breaker.

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
          "user_pool_id": "us-east-1_mock",
          "user_pool_arn": "arn:aws:cognito-idp:us-east-1:123456789012:userpool/us-east-1_mock",
          "alarm_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-alarms",
          "notifications_from": "crowdfunding@example.com",
          "ses_identity_arns": ["arn:aws:ses:us-east-1:123456789012:identity/crowdfunding@example.com"],
          "notifications_security_group_id": "sg-notifications",
          "notifications_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications",
          "migrator_secret_arns": {
            "notifications": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications_migrator",
            "campaigns": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/campaigns_migrator",
            "pledges": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_migrator"
          },
          "notifications_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-notifications",
          "notifications_queue_url": "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-notifications"
        }
      JSON
    }
  }
}

variables {
  image         = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-notifications:abc"
  service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-notifications:abc"
}

run "notifications_service" {
  command = plan

  assert {
    condition     = toset(module.service.secret_arns) == toset(["arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications"])
    error_message = "The service reads its own connection string and no other secret."
  }

  assert {
    condition = toset(module.service.migrate_secret_arns) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications",
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/notifications_migrator",
    ])
    error_message = "The migrate step reads its own secret and its migration role's, never the owner's (CQA-1624, CQA-1750)."
  }

  assert {
    condition = (one([for st in data.aws_iam_policy_document.task.statement : st.resources if st.sid == "LookUpAddresses"]) == toset(["arn:aws:cognito-idp:us-east-1:123456789012:userpool/us-east-1_mock"])
    && one([for st in data.aws_iam_policy_document.task.statement : st.resources if st.sid == "SendEmail"]) == toset(["arn:aws:ses:us-east-1:123456789012:identity/crowdfunding@example.com"]))
    error_message = "The task looks users up in its own pool and sends from its own identity."
  }

  assert {
    condition     = toset(module.service.queue_and_topic) == toset(["arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-notifications"])
    error_message = "The task reads its own queue and publishes to no topic."
  }

  assert {
    condition = (module.service.ecs_service.network_configuration[0].security_groups == toset(["sg-notifications"])
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
