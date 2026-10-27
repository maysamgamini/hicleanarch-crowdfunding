# Plans this root against a mock AWS provider (OpenTofu 1.8+): no credentials and no AWS calls.
# It catches what validate cannot, such as an expression that fails only once values are known.
# Run from this directory: tofu init -backend=false && tofu test

mock_provider "aws" {
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  # The provider checks ARNs even in a plan, so a mock returns well-formed ones.
  mock_resource "aws_iam_policy" {
    defaults = {
      arn = "arn:aws:iam::123456789012:policy/mock"
    }
  }

  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
    }
  }
}

run "plans_with_the_defaults" {
  command = plan
}
