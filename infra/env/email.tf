# Amazon SES, only when a sender address is given. A new account is in SES's sandbox: it sends only
# to verified addresses until production access is granted, so the sandbox mostly keeps emails in memory.
resource "aws_sesv2_email_identity" "sender" {
  count          = var.notifications_from == "" ? 0 : 1
  email_identity = var.notifications_from
}

data "aws_iam_policy_document" "notify" {
  # Notifications looks up a user's email address by their id in the user pool.
  statement {
    actions   = ["cognito-idp:ListUsers"]
    resources = [aws_cognito_user_pool.users.arn]
  }

  dynamic "statement" {
    for_each = aws_sesv2_email_identity.sender
    content {
      actions   = ["ses:SendEmail"]
      resources = [statement.value.arn]
    }
  }
}

resource "aws_iam_role_policy" "task_notify" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.notify.json
}
