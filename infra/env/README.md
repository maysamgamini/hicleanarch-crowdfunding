# infra/env: the sandbox environment

Applied for a lesson or a test run, destroyed afterwards. Everything here is destroyable with no
manual step: no deletion protection, no final snapshot, secrets deleted at once, no buckets.

What it creates: a VPC with two public and two private subnets and no NAT gateway; an
Application Load Balancer; an ECS cluster with a Fargate service (the API and an OpenTelemetry
collector in each task) and a one-off migration task definition; Aurora PostgreSQL 17 Serverless
v2 that pauses at 0 capacity units; Secrets Manager entries for the connection strings.

Normally the `sandbox` GitHub workflow runs all of it. By hand:

```bash
terraform -chdir=infra/env init -backend-config="bucket=$(terraform -chdir=infra/bootstrap output -raw state_bucket)"
terraform -chdir=infra/env apply -var image=<ecr url>:<tag>
# run the migration task, then use the API at the api_url output
terraform -chdir=infra/env destroy
```
