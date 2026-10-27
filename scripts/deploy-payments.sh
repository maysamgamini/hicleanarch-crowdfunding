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
# CQA-2103: the service keeps the task definition it runs, not only its image, until its migration has run.
running=$(tf output -raw service_task_definition 2>/dev/null || true)
# CQA-2124: that task definition names the previous release's secrets, and a rollback returns to it, so
# the execution role keeps them, in both applies, until the next deploy. Nothing deployed yet: none.
kept=$(tf output -json release_secret_arns 2>/dev/null || echo '[]')
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$current" -var "service_task_definition=$running" -var "kept_secret_arns=$kept"
"$(dirname "$0")/run-migration.sh" infra/services/payments
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$image" -var "kept_secret_arns=$kept"
aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw service)"
# A rolled-back deployment is stable too (lesson s5-l14): only a release that runs counts.
"$(dirname "$0")/deployment-landed.sh" "$(tf output -raw cluster)" "$(tf output -raw service)" "${image##*:}"
