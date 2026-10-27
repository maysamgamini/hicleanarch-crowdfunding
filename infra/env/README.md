# infra/env: the sandbox environment

Applied for a lesson or a test run, destroyed afterwards. Everything here is destroyable with no
manual step: no deletion protection, no final snapshot, secrets deleted at once, no buckets.

What it creates: a VPC with a public and a private subnet in each of `zone_count` Availability
Zones (two by default; production runs three) and no NAT gateway; an Application Load Balancer; an
ECS cluster with a Fargate service (the API and an OpenTelemetry collector in each task) and a
one-off migration task definition; Aurora PostgreSQL 17 Serverless v2, which pauses at 0 capacity
units only once no task runs, since the tasks' jobs poll it every few seconds; Valkey with a replica in a second zone and automatic failover; Secrets Manager entries for
the connection strings. Both IAM roles carry the bootstrap layer's task boundary, and the
collector's metrics log group is created here so destroy removes it.

Normally the `sandbox` GitHub workflow runs all of it. By hand:

```bash
tofu -chdir=infra/env init -backend-config="bucket=$(tofu -chdir=infra/bootstrap output -raw state_bucket)"
# 1. everything, with the API service on the image it runs now (empty in a new environment: no service yet)
current=$(tofu -chdir=infra/env output -raw service_image 2>/dev/null || true)
tofu -chdir=infra/env apply -var image=<ecr url>:<tag> -var gateway_image=<gateway url>:<tag> -var service_image="$current"
# 2. run the migration task (see .github/workflows/sandbox.yml), then 3. roll the service onto the release:
tofu -chdir=infra/env apply -var image=<ecr url>:<tag> -var gateway_image=<gateway url>:<tag> -var service_image=<ecr url>:<tag>
STATE_BUCKET=<state bucket> scripts/destroy-payments.sh   # first: Payments runs in this layer's network
tofu -chdir=infra/env destroy
```

The Payments service has its own root, `infra/services/payments`, applied after this layer and
destroyed before it (`scripts/deploy-payments.sh`, `scripts/destroy-payments.sh`). It reads this
layer's outputs from one document, `env/shared.json` in the state bucket (`shared-outputs.tf`).

Both applies are complete plans; neither uses `-target`. The service's image is the only thing the
first apply holds back, so the migration never runs against a half-built stack.

For a known launch, `-var 'launch={start="2026-11-02T15:00:00Z",end="2026-11-02T18:00:00Z",min_count=6}'`
schedules a higher minimum task count for the window, started well before the crowd: target tracking
alone reacts only after the load has arrived. The load balancer also scales gradually; production
reserves load balancer capacity units (LCU reservation) for the same window.

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
