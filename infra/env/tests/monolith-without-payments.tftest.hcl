# Lesson s4-l10: Payments has left the monolith. Planned and applied against a mocked AWS provider (no
# account, no credentials, no calls): the monolith's execution role, task role and task definitions
# name no Payments secret, no Payments topic and no payments queue, and Payments' schema is migrated on
# Payments' own cluster by its own migrate task, never by the shared cluster's owner.

mock_provider "aws" {
  source = "./tests/mocks"
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
    error_message = "The monolith's task role reads its own queue and no other."
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
