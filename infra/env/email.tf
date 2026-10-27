# Amazon SES, only when a sender address is given. A new account is in SES's sandbox: it sends only
# to verified addresses until production access is granted, so the sandbox mostly keeps emails in memory.
resource "aws_sesv2_email_identity" "sender" {
  count          = var.notifications_from == "" ? 0 : 1
  email_identity = var.notifications_from
}

# Looking users up in Cognito and sending through SES are the Notifications service's alone
# (infra/services/notifications); since s5-l04 no role of the monolith may do either.
