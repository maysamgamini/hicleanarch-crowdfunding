#!/usr/bin/env bash
# Destroys the Payments service's root, as Payments' own deploy role. It runs before the shared layer
# is destroyed, because it reads that layer's outputs and its tasks run in that layer's network.
# Usage: STATE_BUCKET=<bucket> destroy-payments.sh
set -euo pipefail
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { terraform -chdir=infra/services/payments "$@"; }

tf init -input=false -backend-config="bucket=$bucket" >/dev/null
if [ -z "$(tf state list)" ]; then
  echo "Payments: nothing to destroy"
  exit 0
fi
tf destroy -input=false -auto-approve -lock-timeout=15m
