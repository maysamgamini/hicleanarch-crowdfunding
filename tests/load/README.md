# Load tests

`launch.js` is the baseline for lesson s3-l01: readers of one campaign page at up to 200 requests a
second, and one pledge per backer token, while ECS scales the service out and in. Run it with
[k6](https://grafana.com/docs/k6/latest/) against the sandbox:

```bash
k6 run -e BASE_URL=http://<alb dns name> -e CAMPAIGN_ID=cmp_... -e BACKER_TOKENS=tokens.txt tests/load/launch.js
```

It never runs in CI. Its results are recorded in the lessons only as measured on the sandbox.
