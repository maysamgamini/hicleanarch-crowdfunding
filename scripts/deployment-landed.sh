#!/usr/bin/env bash
# Did the release land (lesson s5-l14)? `aws ecs wait services-stable` also succeeds after a rollback:
# the service is stable again, on the previous task definition, and ECS reports that deployment
# COMPLETED too. So this asks ECS what its primary deployment is: COMPLETED, and running an image whose
# tag is this release's. Anything else exits 1 with the reason, and the pipeline records nothing.
# Usage: deployment-landed.sh <cluster> <service> <version>
set -euo pipefail
cluster="${1:?usage: deployment-landed.sh <cluster> <service> <version>}"
service="${2:?usage: deployment-landed.sh <cluster> <service> <version>}"
version="${3:?usage: deployment-landed.sh <cluster> <service> <version>}"

primary=$(aws ecs describe-services --cluster "$cluster" --services "$service" \
  --query 'services[0].deployments[?status==`PRIMARY`] | [0]' --output json)
state=$(jq -r '.rolloutState // "UNKNOWN"' <<<"$primary")
reason=$(jq -r '.rolloutStateReason // ""' <<<"$primary")
task=$(jq -r '.taskDefinition // ""' <<<"$primary")
image=$(aws ecs describe-task-definition --task-definition "$task" \
  --query 'taskDefinition.containerDefinitions[0].image' --output text)
tag="${image##*:}"

if [ "$state" != "COMPLETED" ]; then
  echo "$service: the deployment is $state, not COMPLETED: $reason" >&2
  exit 1
fi
if [ "$tag" != "$version" ]; then
  echo "$service runs $tag, not $version: the release was rolled back. $reason" >&2
  exit 1
fi
echo "$service runs $version."
