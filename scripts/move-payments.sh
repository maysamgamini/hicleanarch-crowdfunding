#!/usr/bin/env bash
# Moves Payments' tables from the shared cluster to a clone of it (lesson s4-l07: fence, clone,
# switch). Each step of `move-payments` runs as a one-off ECS task of the release image; Terraform
# makes the clone and rolls Payments onto its new secret. Run it from the repository root with the
# sandbox's AWS credentials, after a deploy, with the same TF_VAR_image, TF_VAR_gateway_image and
# TF_VAR_service_image the deploy used and Terraform initialised against the sandbox's state. It
# needs GNU date (a Linux runner).
#
#   scripts/move-payments.sh             the whole move, to resume
#   scripts/move-payments.sh --rehearse  the move up to the switch, then the rollback, then a check
#                                        that no clone and no clone secret is left
#   scripts/move-payments.sh --rollback  the rollback alone, at any point before resume
#
# Every step records how long it took in the job summary when run in GitHub Actions.
set -euo pipefail
cd "$(dirname "$0")/.."

summary=${GITHUB_STEP_SUMMARY:-/dev/stdout}
tf() { terraform -chdir=infra/env "$@"; }
out() { tf output -raw "$1"; }
apply() { tf apply -input=false -auto-approve -var "payments_database_clone=$1" -var "payments_database=$2"; }
timed() {
  local start
  start=$(date +%s)
  "$@"
  echo "$* took $(( $(date +%s) - start )) s" >> "$summary"
}

last_task=""
step() {
  last_task=$(aws ecs run-task --cluster "$(out cluster)" --launch-type FARGATE \
    --task-definition "$(out move_payments_task_definition)" \
    --network-configuration "awsvpcConfiguration={subnets=[$(out task_subnets)],securityGroups=[$(out payments_security_group)],assignPublicIp=ENABLED}" \
    --overrides "{\"containerOverrides\":[{\"name\":\"move-payments\",\"command\":[\"move-payments\",\"$1\"]}]}" \
    --query 'tasks[0].taskArn' --output text)
  aws ecs wait tasks-stopped --cluster "$(out cluster)" --tasks "$last_task"
  local code
  code=$(aws ecs describe-tasks --cluster "$(out cluster)" --tasks "$last_task" --query 'tasks[0].containers[0].exitCode' --output text)
  echo "move-payments $1: exit $code"
  test "$code" = "0"
}

# The clone must be taken from a restorable time after the fence: the fence step has ended before
# its task stopped, so waiting for that time is enough.
wait_restorable() {
  local fenced restorable
  fenced=$(aws ecs describe-tasks --cluster "$(out cluster)" --tasks "$last_task" --query 'tasks[0].stoppedAt' --output text)
  while true; do
    restorable=$(aws rds describe-db-clusters --db-cluster-identifier "$(out shared_cluster_identifier)" \
      --query 'DBClusters[0].LatestRestorableTime' --output text)
    if [ "$(date -u -d "$restorable" +%s)" -gt "$(date -u -d "$fenced" +%s)" ]; then
      break
    fi
    sleep 15
  done
}

services_stable() {
  aws ecs wait services-stable --cluster "$(out cluster)" --services "$(out service)"
}

rollback() {
  # Tasks already on the clone's secret go back to the shared one first; they start with the flag set.
  if [ "$(out payments_database_endpoint)" != "" ]; then
    timed apply true shared
    timed services_stable
  fi
  timed step rollback
  timed apply false shared
  # Nothing of the clone is left: no cluster, no secrets.
  if aws rds describe-db-clusters --db-cluster-identifier "$(out payments_cluster_identifier)" >/dev/null 2>&1; then
    echo "The clone is still there." >&2
    exit 1
  fi
  for secret in owner payments; do
    if aws secretsmanager describe-secret --secret-id "$(out payments_own_secret_prefix)/$secret" >/dev/null 2>&1; then
      echo "The clone's $secret secret is still there." >&2
      exit 1
    fi
  done
}

case "${1:-}" in
  --rollback)
    rollback
    exit 0
    ;;
  --rehearse | "") ;;
  *)
    echo "usage: $0 [--rehearse | --rollback]" >&2
    exit 2
    ;;
esac

timed step pause
timed step fence
timed wait_restorable
timed apply true shared
timed step prune
timed step verify
timed step switch

if [ "${1:-}" = "--rehearse" ]; then
  rollback
  exit 0
fi

timed apply true own
timed services_stable
timed step resume
echo "Payments runs on its own cluster. In a week: the move_payments task with the step drop-old." >> "$summary"
