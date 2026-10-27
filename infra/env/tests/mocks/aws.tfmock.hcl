# Values the AWS provider would compute, for `tofu test` with a mocked provider: no AWS account,
# no credentials, no calls. Only attributes the provider validates need a real shape; every other
# computed value (secret ARNs among them) is a distinct generated string.

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
