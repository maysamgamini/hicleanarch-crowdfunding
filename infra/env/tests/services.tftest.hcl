# Lesson s5-l10: four services and no monolith. Applied against a mocked AWS provider (no account, no
# credentials, no calls): the shared layer keeps the network, the cluster, the database, Valkey, the broker
# and the gateway, and runs no program that reads a module's data; each service's own root reads its own.
# (Lessons s4-l10, s5-l04 and s5-l09 took Payments, Notifications and Campaigns out of the monolith; their
# runs in this file's history checked the monolith's roles, which are gone.)

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
  target = aws_secretsmanager_secret.valkey["pledges"]
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:valkey-pledges-AbCdEf" }
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
  target = aws_secretsmanager_secret.pledges_service_client
  values = { arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:pledges-service-client-AbCdEf" }
}

override_resource {
  target = aws_sqs_queue.events["pledges"]
  values = {
    arn = "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-pledges"
    id  = "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-pledges"
  }
}

override_resource {
  target = aws_sqs_queue.events["campaigns"]
  values = {
    arn = "arn:aws:sqs:us-east-1:123456789012:hicleanarch-sandbox-campaigns"
    id  = "https://sqs.us-east-1.amazonaws.com/123456789012/hicleanarch-sandbox-campaigns"
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

override_resource {
  target = aws_security_group.pledges
  values = { id = "sg-00000000000000002" }
}

override_resource {
  target = aws_security_group.gateway
  values = { id = "sg-00000000000000003" }
}

run "the_shared_layer_runs_no_monolith" {
  command = apply

  # No program of this layer is left but the gateway: no API task definition, no migrate task, no internal
  # load balancer in front of a monolith.
  assert {
    condition = length(flatten([for f in fileset(path.module, "*.tf") : regexall(
    "resource \"(aws_ecs_task_definition\" \"(api|migrate)|aws_ecs_service\" \"api|aws_lb\" \"internal)\"", file("${path.module}/${f}"))])) == 0
    error_message = "The shared layer defines no monolith: no api or migrate task definition, no api service, no internal load balancer."
  }

  # The execution role starts the gateway's tasks and reads the gateway's Valkey user, nothing else.
  assert {
    condition     = toset(data.aws_iam_policy_document.read_connection_strings.statement[0].resources) == toset([aws_secretsmanager_secret.valkey["gateway"].arn])
    error_message = "The shared layer's execution role reads the gateway's Valkey user and no module's secret."
  }

  # CQA-2104, now for the one role left: exactly its two policies, and nothing else names it.
  assert {
    condition = sort(flatten([for f in fileset(path.module, "*.tf") : [
      for m in regexall("resource \"(aws_iam_[a-z_]+)\" \"([a-z0-9_]+)\" \\{\\s*roles?\\s*=\\s*\\[?\\s*aws_iam_role\\.(?:task|execution)\\.", file("${path.module}/${f}")) :
    "${m[0]}.${m[1]}"]])) == sort(["aws_iam_role_policy.execution_secrets", "aws_iam_role_policy_attachment.execution"])
    error_message = "The execution role carries exactly these two policies; the monolith's task role is gone."
  }

  # The gateway's catch-all goes to the Pledges service through Service Connect.
  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__pledges__Destinations__pledges__Address"] == ["http://pledges:8080/"]
    error_message = "Every path no other service serves goes to the Pledges service."
  }

  # Payments' internal port answers the Pledges service only.
  assert {
    condition     = aws_vpc_security_group_ingress_rule.payments_internal_from_pledges.referenced_security_group_id == aws_security_group.pledges.id
    error_message = "Only the Pledges service calls Payments' internal endpoint."
  }

  # Pledges' place: 8080 from the gateway, its Valkey user on pledges:* alone, the monolith's api user gone.
  assert {
    condition = (aws_vpc_security_group_ingress_rule.pledges_from_gateway.referenced_security_group_id == aws_security_group.gateway.id
      && aws_elasticache_user.valkey["pledges"].access_string == "on ~pledges:* resetchannels -@all +@connection +@read +@write +@scripting -@dangerous -scan -randomkey -script|flush -function|flush"
    && !contains(keys(aws_elasticache_user.valkey), "api"))
    error_message = "The Pledges service is reached from the gateway only and keeps its own keys; no api Valkey user is left."
  }

  # The queue that was the monolith's is Pledges', and the other services find it there.
  assert {
    condition     = local.shared_outputs.pledges_queue_arn == aws_sqs_queue.events["pledges"].arn && !contains(keys(aws_sqs_queue.events), "monolith")
    error_message = "The monolith's queue became the pledges queue (s5-l10)."
  }

  assert {
    condition     = !contains(keys(aws_secretsmanager_secret.connection), "payments")
    error_message = "The shared cluster has no Payments login any more, so no secret for one."
  }

  # Payments' own migrate task (infra/services/payments) migrates Payments as its cluster's master.
  assert {
    condition = (local.shared_outputs.payments_database_owner_secret_arn == aws_secretsmanager_secret.payments_own["owner"].arn
    && local.shared_outputs.payments_connection_secret_arn == aws_secretsmanager_secret.payments_own["payments"].arn)
    error_message = "Payments' migrate task logs in as its own cluster's master, and its service as Payments' login on that cluster."
  }

  # CQA-2105: Payments' tasks do not reach the shared cluster, where the other modules' data lives.
  assert {
    condition = length(flatten([for f in fileset(path.module, "*.tf") : [
      for rule in regexall("resource \"aws_vpc_security_group_ingress_rule\" \"[a-z0-9_]+\" \\{[^}]*\\}", file("${path.module}/${f}")) : rule
    if strcontains(rule, "aws_security_group.database.id") && strcontains(rule, "aws_security_group.payments.id")]])) == 0
    error_message = "No ingress rule of the shared cluster may admit Payments' security group."
  }

  # After the hand-overs nothing migrates as the owner: no service is handed the owner's secret.
  assert {
    condition     = !contains(values(local.shared_outputs), aws_secretsmanager_secret.connection["owner"].arn)
    error_message = "No service is handed the shared cluster's owner."
  }
}

# The gateway reaches Notifications' 8080 from its own security group; it sends the preference there only once
# the service is deployed, and to the catch-all before.
run "the_gateway_alone_reaches_notifications_and_only_once_it_serves" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.notifications_from_gateway.referenced_security_group_id == aws_security_group.gateway.id
    error_message = "Notifications' 8080 is open to the gateway's security group, where the gateway's tasks run."
  }

  assert {
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__notifications__Destinations__notifications__Address"] == ["http://pledges:8080/"]
    error_message = "Before Notifications is deployed, the preference route goes to the catch-all, never to a service with no tasks."
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

# Lesson s5-l08: Campaigns' place in the shared layer; s5-l09: only its own service holds its secrets.
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
    condition = [for e in jsondecode(aws_ecs_task_definition.gateway.container_definitions)[0].environment : e.value
    if e.name == "ReverseProxy__Clusters__campaigns__Destinations__campaigns__Address"] == ["http://pledges:8080/"]
    error_message = "Before the Campaigns service serves them, Campaigns' paths go to the catch-all."
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
