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

## Database connections

Every connection string caps its pool, so the most the program can open is a sum (database.tf):

| Who | Pools | Per task | At `max_count` = 6 |
|---|---|---|---|
| API task, writer | 5 module roles × `Maximum Pool Size=10` | 50 | 300 |
| API task, reader endpoint | 2 read roles × `Maximum Pool Size=5` | 10 | 60 |
| Migration task, writer | owner × 2, plus one per module role | 7 | 7 |

Aurora Serverless v2 fixes `max_connections` from the maximum ACU: about 400 at the sandbox's 2 ACU
(AWS's table: 189 at 1 ACU, 823 at 4). The writer's 307 fit, with the reads too (367) when
`reader_count = 0` sends them to the writer. A precondition in autoscaling.tf refuses a `max_count`
whose pools would not fit, and an alarm fires above 80% of `max_connections`. When the tasks
outgrow it, the next step is a pooler such as RDS Proxy, which multiplexes many client connections
onto few database ones; it keeps a connection open, so the sandbox's database would never pause.

## Secrets

Passwords are ephemeral and sent only to write-only arguments, so they are not in the state. They
are still plain connection strings, which Secrets Manager's rotation cannot rotate, and the
migration runs as the cluster's master user. Production gives the migration a role of its own and
stores each secret as JSON (username, password, host), which Secrets Manager rotation understands.
