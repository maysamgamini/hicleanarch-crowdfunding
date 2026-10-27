# Offline, with a mocked AWS provider: no credentials, no calls. The IAM is read from the policy
# documents' arguments, which are known at plan.
mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
    }
  }
  # The documents' JSON is the provider's to render; the tests read their arguments instead.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{}"
    }
  }
}

variables {
  # What infra/env's task policies grant on the event topics and queues (messaging.tf, payments-service.tf).
  # A task role may do only what both its policies and the boundary allow: an action missing here is denied.
  events_actions = [
    "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes",
    "sns:Publish", "sns:ListSubscriptionsByTopic", "sns:GetSubscriptionAttributes",
  ]
}

run "the_task_boundary_allows_what_the_event_policies_grant_on_the_projects_resources_only" {
  command = plan

  assert {
    condition = alltrue([
      for action in var.events_actions : anytrue([
        for statement in data.aws_iam_policy_document.task_boundary.statement :
        contains(statement.actions, action) && alltrue([for r in statement.resources : r != "*"])
      ])
    ])
    error_message = "The task boundary must allow every action the event policies grant, on the project's own topics and queues."
  }
}

run "the_deploy_role_manages_the_projects_queues_and_no_others" {
  command = plan

  assert {
    condition = anytrue([
      for statement in data.aws_iam_policy_document.deploy_services.statement :
      contains(statement.actions, "sqs:*") && statement.resources == toset(["arn:aws:sqs:us-east-1:123456789012:hicleanarch-*"])
    ])
    error_message = "The deploy role needs sqs:* on the project's queues to apply infra/env's messaging.tf."
  }

  assert {
    condition = alltrue([
      for statement in data.aws_iam_policy_document.deploy_services.statement :
      !contains(statement.actions, "sqs:*") || alltrue([for r in statement.resources : r != "*"])
    ])
    error_message = "sqs:* must not reach queues outside the project."
  }
}
