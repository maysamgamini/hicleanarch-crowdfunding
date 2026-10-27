# Who is calling (Stage 4): the monolith calls Payments' internal endpoint with a machine token. Cognito
# issues it to a confidential app client allowed only the client_credentials grant, for the one scope
# below. Cognito bills every successful token request (an AWS charge, no free tier), so the caller keeps
# a token until five minutes before it expires: one request per task every 55 minutes.

resource "random_id" "auth_domain" {
  byte_length = 4
}

# The token endpoint (/oauth2/token) lives on a user pool domain. A prefix must be unique in the region.
resource "aws_cognito_user_pool_domain" "auth" {
  domain       = "${var.project}-sandbox-${random_id.auth_domain.hex}"
  user_pool_id = aws_cognito_user_pool.users.id
}

# One scope, for commands. Nothing reads Payments over the network yet: a read scope is created when its
# first caller exists, not before.
resource "aws_cognito_resource_server" "payments" {
  identifier   = "payments"
  name         = "Payments"
  user_pool_id = aws_cognito_user_pool.users.id

  scope {
    scope_name        = "commands"
    scope_description = "Send commands to Payments (start a card setup)."
  }
}

# The monolith as a service: a secret, the client-credentials grant and only the commands scope. Its
# tokens live an hour. An empty explicit_auth_flows would give Cognito's defaults (SRP and custom
# sign-in), so it names refresh alone, which signs nobody in: no user can sign in through this client.
resource "aws_cognito_user_pool_client" "monolith_service" {
  name                                 = "${var.project}-monolith-service"
  user_pool_id                         = aws_cognito_user_pool.users.id
  generate_secret                      = true
  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["client_credentials"]
  allowed_oauth_scopes                 = aws_cognito_resource_server.payments.scope_identifiers
  explicit_auth_flows                  = ["ALLOW_REFRESH_TOKEN_AUTH"]
  supported_identity_providers         = ["COGNITO"]
  access_token_validity                = 60
  prevent_user_existence_errors        = "ENABLED"

  token_validity_units {
    access_token = "minutes"
  }
}

# The client secret reaches the task through the execution role, never an image or a file. Cognito
# generates it, so it is in the state already (as the client's client_secret); the state bucket is encrypted.
resource "aws_secretsmanager_secret" "monolith_service_client" {
  name                    = "${var.project}/sandbox/service-identity/monolith"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "monolith_service_client" {
  secret_id     = aws_secretsmanager_secret.monolith_service_client.id
  secret_string = aws_cognito_user_pool_client.monolith_service.client_secret
}

data "aws_iam_policy_document" "read_service_identity" {
  statement {
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_secretsmanager_secret.monolith_service_client.arn]
  }
}

resource "aws_iam_role_policy" "execution_service_identity" {
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.read_service_identity.json
}

locals {
  service_token_endpoint = "https://${aws_cognito_user_pool_domain.auth.domain}.auth.${var.region}.amazoncognito.com/oauth2/token"

  # The api container is both sides while Payments runs in it: the caller (ServiceIdentity) and the
  # receiver, which accepts service tokens only from the clients in Auth:ServiceClients.
  service_identity_environment = [
    { name = "ServiceIdentity__ClientId", value = aws_cognito_user_pool_client.monolith_service.id },
    { name = "ServiceIdentity__TokenEndpoint", value = local.service_token_endpoint },
    { name = "Auth__ServiceClients__0", value = aws_cognito_user_pool_client.monolith_service.id },
  ]
  service_identity_secrets = [
    { name = "ServiceIdentity__ClientSecret", valueFrom = aws_secretsmanager_secret.monolith_service_client.arn },
  ]
}

output "service_token_endpoint" {
  value = local.service_token_endpoint
}

output "monolith_service_client_id" {
  value = aws_cognito_user_pool_client.monolith_service.id
}
