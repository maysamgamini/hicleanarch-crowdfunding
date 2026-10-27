#!/usr/bin/env bash
# Deploys the shared layer and the monolith (infra/env), as the deploy role. Two complete applies,
# never a targeted one:
#   prepare  everything, with the API service kept on the image it runs now; then the migration task
#            runs the release (it prepares every module's schema the monolith compiles)
#   roll     the API service moves to the release; waits until it is stable. The sandbox workflow
#            deploys Payments (deploy-payments.sh) between the two, so roll turns on the monolith's
#            and the gateway's use of it (payments_deployed); PAYMENTS_DEPLOYED=false keeps them off.
# Usage: STATE_BUCKET=<bucket> deploy-monolith.sh prepare|roll <image> <gateway image>
set -euo pipefail
step="${1:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
image="${2:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
gateway="${3:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { terraform -chdir=infra/env "$@"; }

export TF_VAR_image="$image" TF_VAR_gateway_image="$gateway"
tf init -input=false -backend-config="bucket=$bucket" >/dev/null

case "$step" in
  prepare)
    current=$(tf output -raw service_image 2>/dev/null || true)
    deployed=$(tf output -raw payments_deployed 2>/dev/null || echo false)
    tf apply -input=false -auto-approve -lock-timeout=15m -var "service_image=$current" -var "payments_deployed=$deployed"
    "$(dirname "$0")/run-migration.sh" infra/env
    ;;
  roll)
    tf apply -input=false -auto-approve -lock-timeout=15m -var "service_image=$image" -var "payments_deployed=${PAYMENTS_DEPLOYED:-true}"
    aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw service)"
    ;;
  *)
    echo "unknown step: $step" >&2
    exit 2
    ;;
esac
