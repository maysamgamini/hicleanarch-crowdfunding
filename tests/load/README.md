# Load tests

`launch.js` is the baseline for lesson s3-l01: readers of one campaign page at up to 200 requests a
second, and one pledge per backer token, while ECS scales the service out and in. Run it with
[k6](https://grafana.com/docs/k6/latest/) against the sandbox:

```bash
k6 run -e BASE_URL=http://<alb dns name> -e CAMPAIGN_ID=cmp_... -e BACKER_TOKENS=tokens.txt tests/load/launch.js
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
