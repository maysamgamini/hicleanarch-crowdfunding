# Lesson s4-l10: Payments has left the monolith. Planned and applied against a mocked AWS provider (no
# account, no credentials, no calls): the monolith's execution role, task role and task definitions
# name no Payments secret, no Payments topic and no payments queue, and Payments' schema is migrated on
# Payments' own cluster by its own migrate task, never by the shared cluster's owner.

# Values the AWS provider would compute. Only attributes the provider validates need a real shape;
# every other computed value (secret ARNs among them) is a distinct generated string. Written here, not
# in a shared .tfmock.hcl file: OpenTofu's mock_provider takes no source.
mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = { names = ["us-east-1a", "us-east-1b", "us-east-1c"] }
  }

  mock_data "aws_caller_identity" {
    defaults = { account_id = "123456789012" }
  }

  mock_data "aws_iam_policy_document" {
    defaults = { json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}" }
  }

  mock_resource "aws_sns_topic" {
    defaults = { arn = "arn:aws:sns:us-east-1:123456789012:mock" }
  }

  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::123456789012:role/mock" }
  }

  mock_resource "aws_lb" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/mock/0123456789abcdef" }
  }

  mock_resource "aws_lb_listener" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/mock/0123456789abcdef/0123456789abcdef" }
  }

  mock_resource "aws_lb_target_group" {
    defaults = { arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/mock/0123456789abcdef" }
  }

  mock_resource "aws_cognito_user_pool" {
    defaults = {
      id  = "us-east-1_mock"
      arn = "arn:aws:cognito-idp:us-east-1:123456789012:userpool/us-east-1_mock"
    }
  }
}

# Every topic gets an ARN of its own, as in AWS, so "not the payments topic" means something.
override_resource {
  target = aws_sns_topic.events["campaigns"]
  values = { arn = "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-campaigns" }
}

override_resource {
  target = aws_sns_topic.events["pledges"]
  values = { arn = "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-pledges" }
}

override_resource {
  target = aws_sns_topic.events["payments"]
  values = { arn = "arn:aws:sns:us-east-1:123456789012:hicleanarch-sandbox-payments" }
}

# OpenTofu's mock gives every instance of a type the same generated value, so each secret, queue and

# security group the assertions tell apart gets one of its own, as in AWS.

override_resource {
  target = aws_secretsmanager_secret.connection["owner"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:connection-owner-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.connection["campaigns"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:connection-campaigns-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.connection["pledges"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:connection-pledges-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.connection["pledges_jobs"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:connection-pledges-jobs-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.connection["notifications"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:connection-notifications-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.read_connection["campaigns_read"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:read_connection-campaigns-read-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.read_connection["pledges_read"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:read_connection-pledges-read-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.valkey["api"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-api-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.valkey["gateway"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-gateway-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.valkey["payments"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-payments-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.payments_own["owner"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments_own-owner-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.payments_own["payments"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:payments_own-payments-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.webhook
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:webhook-AbCdEf" }
}

override_resource {
  target = aws_secretsmanager_secret.monolith_service_client
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:monolith-service-client-AbCdEf" }
}

override_resource {
  target = aws_sqs_queue.events["monolith"]
  values = {
    arn = "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-monolith"
    id  = "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-monolith"
  }
}

override_resource {
  target = aws_sqs_queue.events["notifications"]
  values = {
    arn = "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-notifications"
    id  = "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-notifications"
  }
}

override_resource {
  target = aws_sqs_queue.events["payments"]
  values = {
    arn = "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-payments"
    id  = "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-payments"
  }
}

override_resource {
  target = aws_security_group.tasks
  values = { id = "sg-00000000000000000" }
}

override_resource {
  target = aws_security_group.payments
  values = { id = "sg-00000000000000001" }
}

variables {
  service_image = "monolith:test"
}

run "the_monolith_holds_nothing_of_payments" {
  command = apply

  # The execution role (the monolith's, and the gateway's) reads no Payments secret.
  assert {
    condition = length(setintersection(
      toset(data.aws_iam_policy_document.read_connection_strings.statement[0].resources),
      toset([
        aws_secretsmanager_secret.payments_own["owner"].arn,
        aws_secretsmanager_secret.payments_own["payments"].arn,
        aws_secretsmanager_secret.webhook.arn,
        aws_secretsmanager_secret.valkey["payments"].arn,
      ]),
    )) == 0
    error_message = "The monolith's execution role must read none of Payments' secrets."
  }

  assert {
    condition     = !contains(keys(aws_secretsmanager_secret.connection), "payments")
    error_message = "The shared cluster has no Payments login any more, so no secret for one."
  }

  # The task role: no Payments topic (to publish, list or read its subscriptions), no payments queue.
  assert {
    condition = alltrue([for s in data.aws_iam_policy_document.events.statement : alltrue([for r in s.resources :
      !startswith(r, aws_sns_topic.events["payments"].arn) && r != aws_sqs_queue.events["payments"].arn
    ])])
    error_message = "The monolith's task role must name neither the Payments topic nor the payments queue."
  }

  assert {
    condition     = toset(data.aws_iam_policy_document.events.statement[2].resources) == toset([aws_sqs_queue.events["monolith"].arn])
    error_message = "The monolith's task role reads its own queue and no other: not Payments' (s4-l10), not Notifications' (s5-l04)."
  }

  assert {
    condition = toset(data.aws_iam_policy_document.events.statement[0].resources) == toset([
      aws_sns_topic.events["campaigns"].arn, aws_sns_topic.events["pledges"].arn,
    ])
    error_message = "The monolith's task role publishes to the topics of the modules it runs, Campaigns and Pledges."
  }

  # The task definitions: no Payments secret in either container, no Payments switch, no payments queue to read.
  assert {
    condition = alltrue([for td in [aws_ecs_task_definition.api, aws_ecs_task_definition.migrate] : alltrue([
      for s in jsondecode(td.container_definitions)[0].secrets :
      !startswith(s.name, "ConnectionStrings__Payments") && !startswith(s.name, "Payments__") && !contains([
        aws_secretsmanager_secret.payments_own["owner"].arn, aws_secretsmanager_secret.payments_own["payments"].arn,
        aws_secretsmanager_secret.webhook.arn, aws_secretsmanager_secret.valkey["payments"].arn,
      ], s.valueFrom)
    ])])
    error_message = "Neither of the monolith's containers may get a Payments secret."
  }

  assert {
    condition = alltrue([for e in jsondecode(aws_ecs_task_definition.api.container_definitions)[0].environment :
      !contains(["Modules__Payments__Mode", "Modules__Payments__Calls", "Payments__Budget__Prefix", "Auth__ServiceClients__0",
      "Events__Broker__Topics__payments", "Events__Broker__Queues__payments__Url"], e.name)
      && !(startswith(e.name, "Events__Broker__Read__") && e.value != "monolith")
    ])
    error_message = "The monolith has no Payments switch, budget or service client, reads only its own queue and publishes to no Payments topic."
  }

  assert {
    condition = contains([for e in jsondecode(aws_ecs_task_definition.api.container_definitions)[0].environment : "${e.name}=${e.value}"],
    "Modules__Payments__BaseAddress=http://payments:8081")
    error_message = "The monolith sends Payments' command to Payments' service, through Service Connect."
  }

  assert {
    condition     = length(setintersection(toset([for r in aws_vpc_security_group_ingress_rule.payments_database_from_payments[*] : r.referenced_security_group_id]), toset([aws_security_group.tasks.id]))) == 0
    error_message = "Only Payments' tasks reach Payments' cluster."
  }

  # Payments' own migrate task (infra/services/payments) migrates Payments as its cluster's master.
  assert {
    condition = (local.shared_outputs.payments_database_owner_secret_arn == aws_secretsmanager_secret.payments_own["owner"].arn
    && local.shared_outputs.payments_connection_secret_arn == aws_secretsmanager_secret.payments_own["payments"].arn)
    error_message = "Payments' migrate task logs in as its own cluster's master, and its service as Payments' login on that cluster."
  }

  assert {
    condition     = !contains(values(local.shared_outputs), aws_secretsmanager_secret.connection["owner"].arn)
    error_message = "Nothing of Payments is handed the shared cluster's owner."
  }
}

# Lesson s5-l04: Notifications runs only as its own service; the monolith can neither send email nor read users.
run "the_monolith_holds_nothing_of_notifications" {
  command = apply

  assert {
    condition     = !contains(data.aws_iam_policy_document.read_connection_strings.statement[0].resources, aws_secretsmanager_secret.connection["notifications"].arn)
    error_message = "The monolith's execution role must not read Notifications' connection string."
  }

  assert {
    condition = alltrue([for td in [aws_ecs_task_definition.api, aws_ecs_task_definition.migrate] :
    length([for s in jsondecode(td.container_definitions)[0].secrets : s if s.name == "ConnectionStrings__Notifications"]) == 0])
    error_message = "The monolith's containers must not get Notifications' connection string."
  }

  assert {
    condition = length([for e in jsondecode(aws_ecs_task_definition.api.container_definitions)[0].environment : e
    if startswith(e.name, "Notifications__") || startswith(e.name, "Events__Broker__Queues__notifications")]) == 0
    error_message = "The monolith must neither be configured to send email nor know the notifications queue."
  }

  assert {
    # s5-l04 kept Mode = Remote one release, for the image before it; s5-l05 removes it.
    condition = length([for e in jsondecode(aws_ecs_task_definition.api.container_definitions)[0].environment : e
    if startswith(e.name, "Modules__Notifications")]) == 0
    error_message = "The monolith has no Notifications setting left."
  }

  assert {
    condition = length([for a in flatten([for d in [data.aws_iam_policy_document.events, data.aws_iam_policy_document.telemetry] : [for st in d.statement : st.actions]]) : a
    if startswith(a, "ses:") || startswith(a, "cognito-idp:")]) == 0
    error_message = "No policy of the monolith's task role may send email or read users."
  }
}
