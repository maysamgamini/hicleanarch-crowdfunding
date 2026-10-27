## Four services at 0.01 × the loop's rates (local)

Served by: four services behind the gateway, events through SNS and SQS (Moto), as compose.yaml runs them. Setup: Darwin arm64, 18 CPUs, load average 4.08 at the start; compose.yaml's stack in Docker (PostgreSQL 17, Valkey 8, Moto for SNS and SQS, the gateway and the four services of commit cf10145 (s5-l15's code), Release builds) and an nginx cache.

These are local measurements: one machine, one database, an nginx cache standing in for CloudFront; they show whether the design's shape holds, not what AWS would do.

| Service | Requests | Availability (objective) | P99 of its key endpoint (objective) | Met |
|---|---|---|---|---|
| pledges | 2708 | 100% (99.99%) | 16.1 ms (< 200 ms, POST /v1/campaigns/{id}/pledges) | yes |
| campaigns | 652 | 100% (99.9%) | 6 ms (< 300 ms, GET /v1/campaigns/{id}) | yes |
| payments | 1 | 100% (99.9%) | 46.7 ms (< 500 ms, GET /v1/campaigns/{id}/settlement) | yes |
| notifications | 61 | 100% (99.9%) | 19.8 ms (< 300 ms, PUT /v1/me/notifications/{kind}) | yes |

| Shape check | Expected | Observed | Holds |
|---|---|---|---|
| Hot tier: places sold, with far more backers than places | 10 | 10 | yes |
| Hot tier: backers in the displayed total | 10 | 10 | yes |
| Hot tier: places left in the displayed total | 0 | 0 | yes |
| Launch: backers in the displayed total = pledges accepted | 1801 | 1801 | yes |
| Launch: amount in the displayed total = accepted × 6,000 | 10806000 | 10806000 | yes |
| CDN path: origin requests for one campaign's totals, about one every 2 s | ≤ 32 | 21 | yes |

Totals: 601 reads, 21 reached the origin (counted at the nginx cache); the displayed total settled 2 s after the hot tier, and its P99 age was 4541 ms.

Close pass: 100 pledges; decided 25.9 s and settled 29.9 s after the deadline (target < 64 s).

Thresholds passed; shape holds.

### What the tasks cost (design estimate)

| Deployable | Math | Per hour |
|---|---|---|
| pledges | 2 × (1 vCPU × $0.0404784 + 2 GB × $0.004446 + one IPv4 address at $0.005) | $0.1087 |
| payments | 1 × (1 vCPU × $0.0404784 + 2 GB × $0.004446 + one IPv4 address at $0.005) | $0.0544 |
| campaigns | 2 × (0.5 vCPU × $0.0404784 + 1 GB × $0.004446 + one IPv4 address at $0.005) | $0.0594 |
| notifications | 2 × (0.5 vCPU × $0.0404784 + 1 GB × $0.004446 + one IPv4 address at $0.005) | $0.0594 |
| **Four services** | | **$0.2819** |
| The monolith | 2 × (0.5 vCPU × $0.0404784 + 1 GB × $0.004446 + one IPv4 address at $0.005) | $0.0594 |

The split adds about $0.2225 an hour, $160 for 30 days: list prices, not a bill.

### Merge-back evidence (from the repository)

Commits in s3-l16..HEAD that changed src: 67.

| Pair | Changed both / changed either | Most change both? |
|---|---|---|
| campaigns and notifications | 8 / 19 | no |
| campaigns and payments | 6 / 36 | no |
| campaigns and pledges | 8 / 35 | no |
| notifications and payments | 6 / 37 | no |
| notifications and pledges | 9 / 35 | no |
| payments and pledges | 15 / 44 | no |

Synchronous calls between services:

- pledges → payments: StartCardSetup (POST /internal/payments/card-setups)

