# Load tests

`launch.js` is the baseline for lesson s3-l01: readers of one campaign page at up to 200 requests a
second, and one pledge per backer token, while ECS scales the service out and in. Run it with
[k6](https://grafana.com/docs/k6/latest/) against the sandbox:

```bash
k6 run -e BASE_URL=http://<alb dns name> -e TOTALS_URL=https://<cloudfront domain> -e CAMPAIGN_ID=cmp_... -e BACKER_TOKENS=tokens.txt tests/load/launch.js
```

It never runs in CI, and the course never runs it on AWS: its numbers on the sandbox come only from
a learner's own run in their own account.

`round2.js` is the loop's Round 2 profile for [k6](https://grafana.com/docs/k6/latest/): a launch
burst on one campaign, a hot limited tier, totals read through CloudFront, and a funded campaign
settled at its deadline. Its thresholds are the loop's targets, so a run that misses one fails.

Against the sandbox (after `tofu apply` of `infra/env`):

```bash
scripts/load.sh 0.02    # 0.02 × the loop's rates: 60 pledges a second; 1 is the full 3,000
```

The script creates the users the run needs in the sandbox's Cognito pool, runs k6, and writes
`artifacts/load/report.md` and `report.json`: each target next to what was measured. The sandbox
workflow runs it when started with `load_scale` above 0, and then lifts the gateway's per-client
limit for that environment (`edge_limit_per_second`): every request of the run comes from one machine.

Against a local host, give k6 the URLs and a tokens file yourself (`{"creator": "<token>",
"backers": ["<token>", …]}`, tokens signed with the development key) and set `API_URL`,
`TOKENS_FILE` (an absolute path), `SCALE` and `REPORT_DIR`.

## Four services (lesson s5-l15)

`four-services.js` runs on this machine or in your own AWS account, and its report says which.

On this machine, with Docker, the .NET SDK and jq:

```bash
scripts/load-local.sh 0.01               # this checkout: the four services, as compose.yaml runs them
scripts/load-local.sh 0.01 --at s3-l13   # the one host of an earlier tag, from s3-l13 to s4-l09
```

Without `--at` it starts the stack of `scripts/local-up.sh` (PostgreSQL, Valkey, Moto for SNS and SQS,
the four services and the gateway, on ports of their own), an nginx cache standing in for CloudFront
(`cdn/`, the same 2-second rule and collapsed misses) in front of the gateway, and k6, all in Docker. With
`--at` it runs that tag's one host instead. Run it on a quiet machine, with a load average below the CPU
count; the report's setup line records it. It writes `artifacts/load/four-services.md` and `.json`:

- per service, its requests, availability and the P99 of its key endpoint against `slos.json`;
- shape checks: a hot tier sells exactly its places, the displayed total equals the pledges accepted,
  and the cache sends the origin about one totals request every 2 seconds;
- the close pass: deadline to decided and to settled;
- the cost of four services beside the monolith's, estimated from list prices (`cost-design.json`);
- the merge-back evidence (`scripts/merge-back.sh`): which services change together, and the
  synchronous calls between them.

These are local numbers: one machine, one database, every container on it. They show the shape holds; they say nothing
about AWS's latency. The cost is an estimate, with its arithmetic in the report.

In your own AWS account, as an exercise, after `tofu apply` (or `terraform apply`) of `infra/env` and the four services' roots:

```bash
scripts/measure-services.sh 0.01
# When you are done, so nothing keeps billing: each service's root first, then the shared layer.
for s in campaigns notifications pledges; do scripts/destroy-service.sh "$s"; done
scripts/destroy-payments.sh
tofu -chdir=infra/env destroy
```

It runs the same profile through the gateway and CloudFront, then reads Service Connect's request,
5xx and response-time metrics and each queue's oldest message from CloudWatch. Look for each service's
availability against its objective, and CloudFront's hits: origin requests for the totals should stay
near one per edge location every 2 seconds. By `docs/cost.md`'s prices, an hour deployed under this
small load costs about a dollar, most of it Aurora at its 2-ACU ceiling.
