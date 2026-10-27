# Plans this root against mock providers (OpenTofu 1.8+): no credentials and no AWS calls.
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

  mock_data "aws_availability_zones" {
    defaults = {
      names = ["us-east-1a", "us-east-1b", "us-east-1c"]
    }
  }

  # The provider checks ARNs and IDs even in a plan, so the mocks return well-formed ones.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::123456789012:role/mock"
    }
  }

  mock_resource "aws_sns_topic" {
    defaults = {
      arn = "arn:aws:sns:us-east-1:123456789012:mock"
    }
  }

  mock_resource "aws_lb" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/mock/0123456789abcdef"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/mock/0123456789abcdef"
    }
  }

  mock_resource "aws_lb_listener" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:listener/app/mock/0123456789abcdef/0123456789abcdef"
    }
  }

  mock_resource "aws_cognito_user_pool" {
    defaults = {
      id = "us-east-1_Mock"
    }
  }
}

# The random provider is not mocked: it runs locally, and OpenTofu cannot yet mock an ephemeral
# resource, whose result (the Valkey passwords) the AWS provider checks for length.

run "plans_with_the_defaults" {
  command = plan
}

# A running service with its autoscaling and a scheduled launch, and Payments switched to its own cluster.
run "plans_a_running_service_after_the_payments_switch" {
  command = plan

  variables {
    service_image           = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch/crowdfunding-api:mock"
    launch                  = { start = "2099-11-02T15:00:00Z", end = "2099-11-02T18:00:00Z", min_count = 6 }
    payments_database_clone = true
    payments_database       = "own"
  }
}

run "refuses_the_own_database_without_the_clone" {
  command = plan

  variables {
    payments_database = "own"
  }

  expect_failures = [var.payments_database]
}
