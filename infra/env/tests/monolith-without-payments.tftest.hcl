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

  # CQA-2104: the roles, not two named documents. Every policy on the monolith's execution and task roles
  # (the gateway shares the execution role) is one of these five; a sixth anywhere in this layer fails.
  # Each reference to either role's name or id is counted, so a policy written another way fails too.
  assert {
    condition = sort(flatten([for f in fileset(path.module, "*.tf") : [
      for m in regexall("resource \"(aws_iam_[a-z_]+)\" \"([a-z0-9_]+)\" \\{\\s*roles?\\s*=\\s*\\[?\\s*aws_iam_role\\.(?:task|execution)\\.", file("${path.module}/${f}")) :
      "${m[0]}.${m[1]}"]])) == sort([
      "aws_iam_role_policy.execution_secrets", "aws_iam_role_policy.execution_service_identity",
      "aws_iam_role_policy.task_events", "aws_iam_role_policy.task_telemetry", "aws_iam_role_policy_attachment.execution",
    ])
    error_message = "The monolith's roles carry exactly these five policies, which this test reads; add any new one here and check it."
  }

  assert {
    condition = length(flatten([for f in fileset(path.module, "*.tf") :
    regexall("aws_iam_role\\.(?:task|execution)\\.(?:name|id)\\b", file("${path.module}/${f}"))])) == 5
    error_message = "Something else names the monolith's roles by name or id: a policy this test does not read."
  }

  assert {
    condition = (aws_iam_role_policy.execution_secrets.policy == data.aws_iam_policy_document.read_connection_strings.json
      && aws_iam_role_policy.execution_service_identity.policy == data.aws_iam_policy_document.read_service_identity.json
      && aws_iam_role_policy.task_events.policy == data.aws_iam_policy_document.events.json
      && aws_iam_role_policy.task_telemetry.policy == data.aws_iam_policy_document.telemetry.json
    && aws_iam_role_policy_attachment.execution.policy_arn == "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy")
    error_message = "Each of the five policies is the document this test reads, and the managed one is ECS's execution policy."
  }

  # No statement of those documents that grants an SNS, SQS or Secrets Manager action names, or matches
  # with a wildcard, a Payments topic, queue or secret. "*" in an ARN is any run of characters, "?" one.
  assert {
    condition = alltrue(flatten([for s in concat(
      data.aws_iam_policy_document.read_connection_strings.statement, data.aws_iam_policy_document.read_service_identity.statement,
      data.aws_iam_policy_document.events.statement, data.aws_iam_policy_document.telemetry.statement,
      ) : [for r in s.resources : [for arn in [
        aws_sns_topic.events["payments"].arn, "${aws_sns_topic.events["payments"].arn}:0123abcd-subscription",
        aws_sqs_queue.events["payments"].arn, aws_secretsmanager_secret.payments_own["owner"].arn,
        aws_secretsmanager_secret.payments_own["payments"].arn, aws_secretsmanager_secret.webhook.arn,
        aws_secretsmanager_secret.valkey["payments"].arn,
        ] : !can(regex("^${replace(replace(replace(r, ".", "\\."), "?", "."), "*", ".*")}$", arn))
    ] if length([for a in s.actions : a if can(regex("^(sns|sqs|secretsmanager|\\*)", a))]) > 0]]))
    error_message = "A policy on the monolith's roles reaches a Payments topic, queue or secret, by name or by wildcard."
  }

  assert {
    condition     = toset(data.aws_iam_policy_document.events.statement[2].resources) == toset([aws_sqs_queue.events["monolith"].arn])
    error_message = "The monolith's task role reads its own queue and no other: not Payments' (s4-l10), Notifications' (s5-l04) or Campaigns' (s5-l09)."
  }

  assert {
    condition     = toset(data.aws_iam_policy_document.events.statement[0].resources) == toset([aws_sns_topic.events["pledges"].arn])
    error_message = "The monolith's task role publishes to the topic of the one module it runs, Pledges (s5-l09)."
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

  # CQA-2105: and Payments' tasks do not reach the shared cluster, where the other modules' data lives.
  assert {
    condition = length(flatten([for f in fileset(path.module, "*.tf") : [
      for rule in regexall("resource \"aws_vpc_security_group_ingress_rule\" \"[a-z0-9_]+\" \\{[^}]*\\}", file("${path.module}/${f}")) : rule
    if strcontains(rule, "aws_security_group.database.id") && strcontains(rule, "aws_security_group.payments.id")]])) == 0
    error_message = "No ingress rule of the shared cluster may admit Payments' security group."
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

# The gateway reaches Notifications' 8080 from its own security group, as it reaches Payments'; and it
# sends the preference there only once the service is deployed.
run "the_gateway_alone_reaches_notifications_and_only_once_it_serves" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.notifications_from_gateway.referenced_security_group_id == aws_security_group.gateway.id
    error_message = "Notifications' 8080 is open to the gateway's security group, where the gateway's tasks run."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.notifications_from_gateway.referenced_security_group_id != aws_security_group.tasks.id
    error_message = "The monolith's tasks have no business with Notifications' endpoint."
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__notifications__Destinations__notifications__Address"] == ["http://${aws_lb.internal.dns_name}/"]
    error_message = "Before Notifications is deployed, the preference route goes to the monolith, never to a service with no tasks."
  }
}

run "the_preference_goes_to_notifications_once_it_is_deployed" {
  command = apply

  variables {
    notifications_serves_preferences = true
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__notifications__Destinations__notifications__Address"] == ["http://notifications:8080/"]
    error_message = "Once deployed, the gateway sends the preference to the Notifications service through Service Connect."
  }
}

# Lesson s5-l08: Campaigns' place beside the monolith. Lesson s5-l09: the monolith holds nothing of Campaigns.
run "campaigns_runs_only_in_its_own_service" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.campaigns_from_gateway.referenced_security_group_id == aws_security_group.gateway.id
    error_message = "Campaigns' 8080 is open to the gateway's security group only."
  }

  assert {
    condition     = aws_elasticache_user.valkey["campaigns"].access_string == "on ~campaigns:* resetchannels -@all +@connection +@read +@write +@scripting -@dangerous -scan -randomkey -script|flush -function|flush"
    error_message = "The Campaigns service's Valkey user reaches only campaigns:* keys."
  }

  assert {
    condition     = !strcontains(aws_elasticache_user.valkey["api"].access_string, "campaigns")
    error_message = "The monolith's Valkey user reaches no campaigns key (s5-l09)."
  }

  assert {
    condition = !anytrue([for arn in [aws_secretsmanager_secret.connection["campaigns"].arn, aws_secretsmanager_secret.read_connection["campaigns_read"].arn, aws_secretsmanager_secret.valkey["campaigns"].arn] :
    contains(data.aws_iam_policy_document.read_connection_strings.statement[0].resources, arn)])
    error_message = "The monolith's execution role reads no Campaigns secret."
  }

  assert {
    condition = alltrue([for td in [aws_ecs_task_definition.api, aws_ecs_task_definition.migrate] :
    length([for e in jsondecode(td.container_definitions)[0].secrets : e if startswith(e.name, "ConnectionStrings__Campaigns")]) == 0])
    error_message = "Neither of the monolith's containers gets a Campaigns connection string."
  }

  assert {
    condition = alltrue([for st in data.aws_iam_policy_document.events.statement : alltrue([for r in st.resources :
    r != aws_sqs_queue.events["campaigns"].arn && (st.actions != toset(["sns:Publish", "sns:ListSubscriptionsByTopic"]) || r != aws_sns_topic.events["campaigns"].arn)])])
    error_message = "The monolith's task role neither reads the campaigns queue nor publishes to the campaigns topic."
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.api.container_definitions)[0].environment : e.value
    if e.name == "Modules__Campaigns__Mode"] == ["Remote"]
    error_message = "For the previous image the first apply runs, the monolith's one Campaigns setting is Mode = Remote, for one release."
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__campaigns__Destinations__campaigns__Address"] == ["http://${aws_lb.internal.dns_name}/"]
    error_message = "Before the Campaigns service serves them, Campaigns' paths go to the monolith."
  }
}

run "the_gateway_sends_campaigns_paths_to_the_service_once_it_serves_them" {
  command = apply

  variables {
    campaigns_serves_routes = true
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__campaigns__Destinations__campaigns__Address"] == ["http://campaigns:8080/"]
    error_message = "Once it serves them, the gateway sends Campaigns' paths to the Campaigns service."
  }
}
