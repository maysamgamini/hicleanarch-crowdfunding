#!/usr/bin/env bash
# Destroys one Stage 5 service's root, as its own deploy role. It runs before the shared layer is
# destroyed, because it reads that layer's outputs and its tasks run in that layer's network.
# Usage: STATE_BUCKET=<bucket> destroy-service.sh <name>
set -euo pipefail
name="${1:?usage: destroy-service.sh <name>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
tf() { "${IAC:-tofu}" -chdir="infra/services/$name" "$@"; }

tf init -input=false -backend-config="bucket=$bucket" >/dev/null
if [ -z "$(tf state list)" ]; then
  echo "$name: nothing to destroy"
  exit 0
fi
tf destroy -input=false -auto-approve -lock-timeout=15m
