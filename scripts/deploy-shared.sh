#!/usr/bin/env bash
# Deploys the shared layer (infra/env), as the shared deploy role: the network, the cluster, the database,
# Valkey, the broker and the gateway. One complete apply, never a targeted one. Since s5-l10 no monolith is
# left: each service deploys itself (scripts/deploy-service.sh <name>), after this layer and its one-time
# hand-over (scripts/hand-over.sh <name>).
# Usage: STATE_BUCKET=<bucket> deploy-shared.sh <gateway image>
set -euo pipefail
gateway="${1:?usage: deploy-shared.sh <gateway image>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { "${IAC:-tofu}" -chdir=infra/env "$@"; }

export TF_VAR_gateway_image="$gateway"
tf init -input=false -backend-config="bucket=$bucket" >/dev/null

# CQA-2101: Payments' data must already be on its own cluster. payments-database.tf refuses an
# environment whose last env/shared.json says otherwise; one deployed only before s4-l09 has no such
# document, but its state has the shared cluster and no Payments cluster.
state=$(tf state list 2>/dev/null || true)
if grep -qx 'aws_rds_cluster.main' <<<"$state" && ! grep -Eqx 'aws_rds_cluster\.payments(\[0\])?' <<<"$state"; then
  echo "Payments' data is still on the shared cluster here: finish s4-l07's move at s4-l09 first." >&2
  exit 1
fi

tf apply -input=false -auto-approve -lock-timeout=15m
aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw gateway_service)"
