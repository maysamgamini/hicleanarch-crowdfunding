#!/usr/bin/env bash
# Runs an OpenTofu root's migration task once and fails unless it exits 0. The root's outputs name the
# cluster, the task definition, the subnets and the security group (infra/env, infra/services/*). A
# second argument runs that task definition instead (scripts/hand-over.sh; empty for the root's own),
# and any further arguments replace the container's command: the container named by CONTAINER, migrate
# unless set (scripts/hand-over.sh sets hand-over, the hand-over task's).
# Usage: [CONTAINER=<name>] run-migration.sh <root dir> [task definition] [command...]
set -euo pipefail
dir="${1:?usage: run-migration.sh <root dir> [task definition] [command...]}"
definition="${2:-}"
shift $(( $# < 2 ? $# : 2 ))
out() { "${IAC:-tofu}" -chdir="$dir" output -raw "$1"; }
overrides=()
if (( $# > 0 )); then
  command=$(printf '"%s",' "$@")
  overrides=(--overrides "{\"containerOverrides\":[{\"name\":\"${CONTAINER:-migrate}\",\"command\":[${command%,}]}]}")
fi

cluster=$(out cluster)
task=$(aws ecs run-task --cluster "$cluster" --launch-type FARGATE \
  --task-definition "${definition:-$(out migrate_task_definition)}" \
  --network-configuration "awsvpcConfiguration={subnets=[$(out task_subnets)],securityGroups=[$(out task_security_group)],assignPublicIp=ENABLED}" \
  ${overrides[@]+"${overrides[@]}"} \
  --query 'tasks[0].taskArn' --output text)
aws ecs wait tasks-stopped --cluster "$cluster" --tasks "$task"
code=$(aws ecs describe-tasks --cluster "$cluster" --tasks "$task" --query 'tasks[0].containers[0].exitCode' --output text)
test "$code" = "0"
