#!/usr/bin/env bash
# Runs a Terraform root's migration task once and fails unless it exits 0. The root's outputs name the
# cluster, the task definition, the subnets and the security group (infra/env and infra/services/payments).
# Usage: run-migration.sh <terraform dir>
set -euo pipefail
dir="${1:?usage: run-migration.sh <terraform dir>}"
out() { terraform -chdir="$dir" output -raw "$1"; }

cluster=$(out cluster)
task=$(aws ecs run-task --cluster "$cluster" --launch-type FARGATE \
  --task-definition "$(out migrate_task_definition)" \
  --network-configuration "awsvpcConfiguration={subnets=[$(out task_subnets)],securityGroups=[$(out task_security_group)],assignPublicIp=ENABLED}" \
  --query 'tasks[0].taskArn' --output text)
aws ecs wait tasks-stopped --cluster "$cluster" --tasks "$task"
code=$(aws ecs describe-tasks --cluster "$cluster" --tasks "$task" --query 'tasks[0].containers[0].exitCode' --output text)
test "$code" = "0"
