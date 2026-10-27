# The Pledges service's own root (lesson s5-l10), planned against a mocked AWS provider and a stand-in for
# the shared layer's published outputs (env/shared.json): no account, no calls. It reads only its own
# secrets and its service identity, its migrate step logs in as pledges_migrator and never as the owner, it
# reads its own queue and publishes to its own topic and nothing else, it calls Payments through Service
# Connect, and it scales between the shared layer's counts.

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
          "pledges_security_group_id": "sg-pledges",
          "pledges_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges",
          "pledges_jobs_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_jobs",
          "pledges_read_connection_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_read",
          "pledges_valkey_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/valkey/pledges",
          "pledges_service_client_secret_arn": "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/service-identity/pledges",
          "pledges_service_client_id": "pledges-client",
          "service_token_endpoint": "https://auth.example/oauth2/token",
          "pledges_topic_arn": "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-pledges",
          "pledges_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-pledges",
          "pledges_queue_url": "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-pledges",
          "campaigns_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-campaigns",
          "notifications_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-notifications",
          "payments_queue_arn": "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-payments",
          "pledges_desired_count": 2,
          "pledges_max_count": 6,
          "visibility_timeout_seconds": 60,
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
  image         = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-pledges:abc"
  service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-pledges:abc"
}

run "pledges_service" {
  command = plan

  assert {
    condition = toset(module.service.secret_arns) == toset([
      "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_jobs", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_read", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/valkey/pledges", "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/service-identity/pledges",
    ])
    error_message = "The service reads its own connection strings, its Valkey user and its service identity, and no other secret."
  }

  assert {
    condition     = contains(module.service.migrate_secret_arns, "arn:aws:secretsmanager:us-east-1:123456789012:secret:hicleanarch/sandbox/db/pledges_migrator") && !anytrue([for a in module.service.migrate_secret_arns : endswith(a, "/db/owner")])
    error_message = "The migrate step logs in as pledges_migrator, never as the owner: after the hand-overs nothing migrates as the owner."
  }

  assert {
    condition     = toset(module.service.queue_and_topic) == toset(["arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-pledges", "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-pledges"])
    error_message = "The task reads its own queue and publishes to its own topic, and no other."
  }

  assert {
    condition     = [for e in module.service.container.environment : e.value if e.name == "Services__Payments__BaseAddress"] == ["http://payments:8081"] && length([for e in module.service.container.environment : e if startswith(e.name, "Modules__")]) == 0
    error_message = "Payments is reached through Service Connect, and no Modules:* switch is left (s5-l10)."
  }

  assert {
    condition = (module.service.ecs_service.network_configuration[0].security_groups == toset(["sg-pledges"])
    && module.service.ecs_service.deployment_circuit_breaker[0].rollback)
    error_message = "The service runs in its own security group and rolls a failed deploy back."
  }
}

run "it_scales_between_the_shared_layers_counts" {
  command = plan

  variables {
    launch = { start = "2030-01-01T10:00:00Z", end = "2030-01-01T12:00:00Z", min_count = 4 }
  }

  assert {
    condition     = aws_appautoscaling_target.pledges[0].min_capacity == 2 && aws_appautoscaling_target.pledges[0].max_capacity == 6
    error_message = "Autoscaling keeps between the desired and the most tasks the connection budget allows."
  }

  assert {
    condition     = tostring(aws_appautoscaling_scheduled_action.launch_start[0].scalable_target_action[0].min_capacity) == "4"
    error_message = "A known launch raises the minimum at its start."
  }
}

run "first_apply_of_a_deploy_creates_no_service" {
  command = plan

  variables {
    service_image = ""
  }

  assert {
    condition     = module.service.ecs_service == null && length(aws_appautoscaling_target.pledges) == 0
    error_message = "The first deploy registers the task definitions and runs the migration before any service exists."
  }
}
