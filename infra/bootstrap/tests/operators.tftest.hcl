# tofu test (or terraform test), with a mocked AWS provider: no account, no calls. Run from infra/bootstrap.
mock_provider "aws" {
  # Rendered policies are checked by their statements below; the mock only needs valid JSON.
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

  override_data {
    target = data.aws_caller_identity.current
    values = { account_id = "123456789012" }
  }
}

run "the_operators_role_only_reads" {
  command = plan

  assert {
    condition = alltrue([for s in data.aws_iam_policy_document.operators.statement : alltrue([
      for a in s.actions : can(regex("^(logs|cloudwatch|xray):(Get|List|Describe|BatchGet|Filter|StartQuery$|StopQuery$)", a))
    ])])
    error_message = "Every action of the operators' role reads; StartQuery and StopQuery only run and stop a read."
  }

  assert {
    condition     = alltrue([for s in data.aws_iam_policy_document.operators.statement : coalesce(s.effect, "Allow") == "Allow" && !anytrue([for a in s.actions : strcontains(a, "*")])])
    error_message = "No wildcard actions."
  }

  assert {
    condition = alltrue(flatten([for s in data.aws_iam_policy_document.operators.statement : [
      for r in s.resources : startswith(r, "arn:aws:logs:us-east-1:123456789012:log-group:/hicleanarch/") if anytrue([for a in s.actions : contains(["logs:StartQuery", "logs:GetLogEvents", "logs:FilterLogEvents"], a)])
    ]]))
    error_message = "The operators read log lines only from the project's log groups."
  }

  assert {
    condition     = one(one(data.aws_iam_policy_document.operators_assume.statement).condition).variable == "aws:MultiFactorAuthPresent" && contains(one(one(data.aws_iam_policy_document.operators_assume.statement).condition).values, "true")
    error_message = "Assuming the role needs MFA."
  }
}
