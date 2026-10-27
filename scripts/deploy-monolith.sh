#!/usr/bin/env bash
# Deploys the shared layer and the monolith (infra/env), as the deploy role. Two complete applies,
# never a targeted one:
#   prepare  everything, with the API service kept on the image it runs now; then the migration task
#            runs the release (it prepares every module's schema the monolith compiles)
#   roll     first the release's `open-sinks --module Notifications` (s5-l04): it fails while a row of the
#            monolith's outboxes still owes Notifications an in-process delivery, which the release,
#            running no Notifications, would refuse to start over; then the API service moves to the
#            release and the step waits until it is stable. The sandbox workflow
#            deploys Payments (deploy-payments.sh) between the two: the monolith calls it, and since
#            s4-l10 it runs nowhere else.
# Usage: STATE_BUCKET=<bucket> deploy-monolith.sh prepare|roll <image> <gateway image>
set -euo pipefail
step="${1:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
image="${2:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
gateway="${3:?usage: deploy-monolith.sh prepare|roll <image> <gateway image>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { "${IAC:-tofu}" -chdir=infra/env "$@"; }

export TF_VAR_image="$image" TF_VAR_gateway_image="$gateway"
tf init -input=false -backend-config="bucket=$bucket" >/dev/null

# CQA-2101: Payments' data must already be on its own cluster. payments-database.tf refuses an
# environment whose last env/shared.json says otherwise; one deployed only before s4-l09 has no such
# document, but its state has the shared cluster and no Payments cluster.
state=$(tf state list 2>/dev/null || true)
if grep -qx 'aws_rds_cluster.main' <<<"$state" && ! grep -Eqx 'aws_rds_cluster\.payments(\[0\])?' <<<"$state"; then
  echo "Payments' data is still on the shared cluster here: finish s4-l07's move at s4-l09 first." >&2
  exit 1
fi

case "$step" in
  prepare)
    current=$(tf output -raw service_image 2>/dev/null || true)
    tf apply -input=false -auto-approve -lock-timeout=15m -var "service_image=$current"
    "$(dirname "$0")/run-migration.sh" infra/env
    ;;
  roll)
    "$(dirname "$0")/run-migration.sh" infra/env "" open-sinks --module Notifications
    # s5-l08: the release that stops running Campaigns here starts only once nothing owes it an in-process delivery.
    if [ "${TF_VAR_campaigns_mode:-InProcess}" = Remote ]; then
      "$(dirname "$0")/run-migration.sh" infra/env "" open-sinks --module Campaigns
    fi
    tf apply -input=false -auto-approve -lock-timeout=15m -var "service_image=$image"
    aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw service)"
    ;;
  *)
    echo "unknown step: $step" >&2
    exit 2
    ;;
esac
