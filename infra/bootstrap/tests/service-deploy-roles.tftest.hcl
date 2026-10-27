# Each Stage 5 service's deploy role (services.tf), applied against a mocked AWS provider: no account,
# no calls. A role reaches only its own service's names; the shared deploy role is denied all of them;
# no service's role can register or run another's task definitions or the hand-over.

mock_provider "aws" {
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  mock_resource "aws_iam_policy" {
    defaults = {
      arn = "arn:aws:iam::123456789012:policy/mock"
    }
  }

  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::123456789012:role/mock"
    }
  }

  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
    }
  }
}

run "service_deploy_roles" {
  command = apply

  assert {
    condition     = local.delivered_services == toset(["notifications", "campaigns", "pledges"])
    error_message = "Notifications (s5-l03), Campaigns (s5-l08) and Pledges (s5-l10) each have their own repository and deploy role."
  }

  assert {
    condition = alltrue(flatten([for name, doc in data.aws_iam_policy_document.deploy_service : [
      for st in doc.statement : [
        # Tasks in the sandbox's cluster: ECS names a task by its cluster, not its family, so watching
        # and tagging the migrate task it started reach the cluster's tasks.
        for r in st.resources : r == "*" || r == aws_s3_bucket.state.arn || r == local.shared_outputs_document
        || r == "arn:aws:ecs:${var.region}:${local.account_id}:task/${var.project}-sandbox/*"
        || strcontains(r, "/${name}") || strcontains(r, "-${name}") || strcontains(r, "crowdfunding-${name}")
        || r == aws_ecr_repository.service[name].arn
      ] if st.effect != "Deny" && !contains(["ReadTheOtherServices", "ScalingServiceLinkedRole"], st.sid)
    ]]))
    error_message = "A service's deploy role may only allow its own service's names (or account-wide reads, the state bucket's listing, the shared outputs and the cluster's tasks)."
  }

  assert {
    condition = alltrue([for name, doc in data.aws_iam_policy_document.deploy_service :
      one([for st in doc.statement : st.actions if st.sid == "ReadTheOtherServices"]) == toset(["ecs:DescribeServices"])
    ])
    error_message = "The contract gate reads which version each service runs: a deploy role may describe the cluster's other services, and do nothing else to them."
  }

  assert {
    condition = alltrue([for name, doc in data.aws_iam_policy_document.deploy_service :
      !anytrue([for st in doc.statement : anytrue([for r in st.resources : strcontains(r, "handover")]) if st.effect != "Deny"])
    ])
    error_message = "No service's deploy role may touch the one-time hand-over, which reads the owner's secret."
  }

  assert {
    condition = alltrue([for name, doc in data.aws_iam_policy_document.deploy_service :
      one([for st in doc.statement : st.resources if st.sid == "RunItsMigration"]) == toset(local.service_access[name].task_defs)
    ])
    error_message = "A service's deploy role runs only its own task definitions: its migrate step, as its own migration role."
  }

  assert {
    condition = alltrue([for name in local.delivered_services :
      contains(one([for st in data.aws_iam_policy_document.deploy_shared_services.statement : st.resources if st.sid == "NotTheServicesRoles"]), local.service_access[name].roles)
    ])
    error_message = "The shared deploy role is denied every service's roles."
  }

  assert {
    condition     = toset(keys(aws_ecr_repository.service)) == local.delivered_services && alltrue([for r in aws_ecr_repository.service : r.image_tag_mutability == "IMMUTABLE"])
    error_message = "Each service has its own repository of immutable tags."
  }
}

# Lesson s5-l13: each service's deploy role changes its own pages, window alarms and signals dashboard,
# and the shared deploy role none of them.
run "each_service_owns_its_signals" {
  command = apply

  assert {
    condition = alltrue([for name, doc in data.aws_iam_policy_document.deploy_service :
      one([for st in doc.statement : st.resources if st.sid == "ItsAlarms"]) == toset([
        "arn:aws:cloudwatch:${var.region}:${local.account_id}:alarm:${var.project}-sandbox-${name}-*",
        "arn:aws:cloudwatch:${var.region}:${local.account_id}:alarm:${var.project}-sandbox-${name}_running_tasks",
      ])
      && one([for st in doc.statement : st.resources if st.sid == "ItsDashboard"]) == toset(["arn:aws:cloudwatch::${local.account_id}:dashboard/${var.project}-sandbox-${name}-signals"])
    ])
    error_message = "A service's deploy role puts alarms and a dashboard under its own name only."
  }

  assert {
    condition = alltrue(flatten([for name in local.delivered_services : [
      for r in concat(local.service_access[name].alarms, [local.service_access[name].dashboard]) :
      contains(one([for st in data.aws_iam_policy_document.deploy_shared_services.statement : st.resources if st.sid == "NotTheServicesSignals"]), r)
    ]]))
    error_message = "The shared deploy role is denied every service's alarms and dashboard."
  }

  assert {
    condition = one([for st in data.aws_iam_policy_document.deploy_payments.statement : st.resources if st.sid == "ItsAlarms"]) == toset(local.payments_alarms) && alltrue([
      for r in concat(local.payments_alarms, [local.payments_dashboard]) :
      contains(one([for st in data.aws_iam_policy_document.deploy_shared.statement : st.resources if st.sid == "NotPaymentsSignals"]), r)
    ])
    error_message = "Payments' deploy role owns its alarms and dashboard; the shared deploy role is denied them."
  }

  assert {
    condition = alltrue([for name, doc in data.aws_iam_policy_document.deploy_service :
      anytrue([for st in doc.statement : st.sid == "ScaleItsService"]) == contains(local.autoscaled_services, name)
    ])
    error_message = "A service whose root scales its ECS service (Pledges) may register its scalable target, policies and scheduled actions; the others may not."
  }

  assert {
    condition = alltrue([for st in data.aws_iam_policy_document.deploy_service["pledges"].statement :
      toset([for c in st.condition : c.variable]) == toset(["application-autoscaling:service-namespace", "application-autoscaling:scalable-dimension"])
      if st.sid == "ScaleItsService"
    ])
    error_message = "Pledges' scaling rights hold only for an ECS service's desired count."
  }

  assert {
    condition = alltrue([for st in data.aws_iam_policy_document.deploy_service["pledges"].statement :
      alltrue([for c in st.condition : toset(c.values) == toset(["ecs.application-autoscaling.amazonaws.com"])]) if st.sid == "ScalingServiceLinkedRole"
    ])
    error_message = "The deploy role may create only Application Auto Scaling's role for ECS."
  }
}
