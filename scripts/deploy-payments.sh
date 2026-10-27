#!/usr/bin/env bash
# Deploys the Payments service (infra/services/payments), as Payments' own deploy role, into a running
# shared layer. The same two applies as the monolith's, with Payments' own migration between them:
# Payments.Host prepares Payments' schema only, before any of its tasks runs the release.
# Usage: STATE_BUCKET=<bucket> deploy-payments.sh <image>
set -euo pipefail
image="${1:?usage: deploy-payments.sh <image>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { "${IAC:-tofu}" -chdir=infra/services/payments "$@"; }

tf init -input=false -backend-config="bucket=$bucket" >/dev/null
current=$(tf output -raw service_image 2>/dev/null || true)
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$current"
"$(dirname "$0")/run-migration.sh" infra/services/payments
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$image"
aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw service)"
