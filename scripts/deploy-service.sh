#!/usr/bin/env bash
# Deploys one Stage 5 service (infra/services/<name>), as that service's own deploy role, into a
# running shared layer. Two applies, with the service's own migrate step between them: the release's
# image with `migrate`, logged in as the service's migration role, changes its schema only, before any
# of its tasks runs the release. The schema must have been handed over once (scripts/hand-over.sh).
# Usage: STATE_BUCKET=<bucket> deploy-service.sh <name> <image>
set -euo pipefail
name="${1:?usage: deploy-service.sh <name> <image>}"
image="${2:?usage: deploy-service.sh <name> <image>}"
bucket="${STATE_BUCKET:?set STATE_BUCKET to the state_bucket output of infra/bootstrap}"
dir="infra/services/$name"
test -d "$dir" || { echo "no OpenTofu root $dir" >&2; exit 2; }
tf() { "${IAC:-tofu}" -chdir="$dir" "$@"; }

tf init -input=false -backend-config="bucket=$bucket" >/dev/null
current=$(tf output -raw service_image 2>/dev/null || true)
# CQA-2103: the service keeps the task definition it runs, not only its image, until its migration has run.
running=$(tf output -raw service_task_definition 2>/dev/null || true)
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$current" -var "service_task_definition=$running"
"$(dirname "$0")/run-migration.sh" "$dir"
tf apply -input=false -auto-approve -lock-timeout=15m -var "image=$image" -var "service_image=$image"
aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw service)"
# A rolled-back deployment is stable too (lesson s5-l14): only a release that runs counts.
"$(dirname "$0")/deployment-landed.sh" "$(tf output -raw cluster)" "$(tf output -raw service)" "${image##*:}"
