# CQA-2100: Payments' cluster holds Payments' only copy of its data, so no ordinary deploy may lose it.
# At s4-l07 to s4-l09 the move's two variables lived only on move-payments.sh's command line, and the
# next deploy-monolith.sh apply, which passed neither, destroyed the cluster. Applied twice against a
# mocked AWS provider (no account, no calls): once as the environment stands after the move, then as
# deploy-monolith.sh applies it; the cluster, its writer and its secrets must come through the second.
# A mock cannot tell a replaced cluster from a kept one, so the protection is what is asserted: at AWS
# it makes any apply that would delete or replace the cluster fail instead.

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

# The environment after the move: Payments runs on its own cluster.
run "the_moved_environment" {
  command = apply

  variables {
    service_image = "monolith:running"
  }

  assert {
    condition     = aws_rds_cluster.payments.deletion_protection && !aws_rds_cluster.payments.skip_final_snapshot
    error_message = "Payments' cluster is protected from deletion, and a deliberate delete leaves a final snapshot."
  }

  assert {
    condition     = aws_rds_cluster.payments.final_snapshot_identifier == "hicleanarch-sandbox-payments-final"
    error_message = "The final snapshot has the name scripts/release-payments-database.sh makes way for."
  }
}

# The next ordinary deploy, with only what deploy-monolith.sh passes: the cluster is the same one.
run "an_ordinary_deploy_keeps_payments_cluster" {
  command = apply

  variables {
    image         = "monolith:release"
    service_image = "monolith:running"
  }

  assert {
    condition     = aws_rds_cluster.payments.deletion_protection && aws_rds_cluster.payments.cluster_identifier == "hicleanarch-sandbox-payments"
    error_message = "An ordinary deploy keeps Payments' cluster, still protected: nothing a deploy passes may turn it off."
  }

  assert {
    condition     = aws_rds_cluster_instance.payments_writer.cluster_identifier == aws_rds_cluster.payments.id
    error_message = "Payments' writer stays on Payments' cluster."
  }

  assert {
    condition = (local.shared_outputs.payments_connection_secret_arn == aws_secretsmanager_secret.payments_own["payments"].arn
    && length(aws_secretsmanager_secret.payments_own) == 2)
    error_message = "Payments keeps connecting to its own cluster, and both of its cluster's secrets stay."
  }
}
