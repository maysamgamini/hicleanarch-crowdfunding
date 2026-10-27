# Cognito signs creators and backers in. The API never calls it per request: it checks each
# access token against the pool's published keys, its client_id and its groups.

resource "aws_cognito_user_pool" "users" {
  name                = "${var.project}-sandbox"
  deletion_protection = "INACTIVE"
  username_attributes = ["email"]

  password_policy {
    minimum_length    = 12
    require_lowercase = true
    require_uppercase = true
    require_numbers   = true
    require_symbols   = false
  }
}

# A public client (no secret), as a browser or mobile app is. Password sign-in is enabled only
# so the sandbox smoke test can get a token; a real front end signs in through Cognito's hosted
# pages or SRP.
resource "aws_cognito_user_pool_client" "api" {
  name                = "${var.project}-api"
  user_pool_id        = aws_cognito_user_pool.users.id
  generate_secret     = false
  explicit_auth_flows = ["ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"]
}

resource "aws_cognito_user_group" "creator" {
  name         = "creator"
  user_pool_id = aws_cognito_user_pool.users.id
}

resource "aws_cognito_user_group" "backer" {
  name         = "backer"
  user_pool_id = aws_cognito_user_pool.users.id
}

locals {
  cognito_issuer = "https://cognito-idp.${var.region}.amazonaws.com/${aws_cognito_user_pool.users.id}"
}
