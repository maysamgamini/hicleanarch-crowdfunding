# infra/env: the sandbox environment

Applied for a lesson or a test run, destroyed afterwards. Everything here is destroyable with no
manual step: no deletion protection, no final snapshot, secrets deleted at once, no buckets.

What it creates: a VPC with two public and two private subnets and no NAT gateway; an
Application Load Balancer; an ECS cluster with a Fargate service (the API and an OpenTelemetry
collector in each task) and a one-off migration task definition; Aurora PostgreSQL 17 Serverless
v2 that pauses at 0 capacity units; Secrets Manager entries for the connection strings. Both IAM roles carry the bootstrap layer's
task boundary, and the collector's metrics log group is created here so destroy removes it.

Normally the `sandbox` GitHub workflow runs all of it. By hand:

```bash
terraform -chdir=infra/env init -backend-config="bucket=$(terraform -chdir=infra/bootstrap output -raw state_bucket)"
# 1. only what the migration task needs, 2. migrate, 3. everything else (the service takes the new image)
terraform -chdir=infra/env apply -var image=<ecr url>:<tag> \
  -target=aws_ecs_cluster.main -target=aws_ecs_task_definition.migrate -target=aws_security_group.tasks -target=aws_subnet.public
# run the migration task (see .github/workflows/sandbox.yml), then:
terraform -chdir=infra/env apply -var image=<ecr url>:<tag>
terraform -chdir=infra/env destroy
```
