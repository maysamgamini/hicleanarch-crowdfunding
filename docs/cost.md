# What the sandbox costs per hour

Derived from the Terraform in `infra/env` (the shape after lesson s3-l12: the monolith, the
gateway, an Aurora writer and reader, Valkey, CloudFront) and AWS's us-east-1 on-demand list prices,
read on 2026-10-27. These are estimates from prices, not a bill. The sandbox run records what an
hour actually cost; put the measured figure next to these in lesson s3-l13.

## Prices used

| Item | Price | Source |
|---|---|---|
| Fargate, Linux/x86 | $0.000011244 per vCPU-second and $0.000001235 per GB-second ($0.0404784 per vCPU-hour, $0.004446 per GB-hour) | [Fargate pricing](https://aws.amazon.com/fargate/pricing/) |
| Aurora Serverless v2, Aurora Standard | $0.12 per ACU-hour; storage $0.10 per GB-month; I/O $0.20 per million | [Aurora pricing](https://aws.amazon.com/rds/aurora/pricing/) |
| Aurora at 0 ACUs (paused) | no instance charge; storage still charged | [Aurora auto-pause](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/aurora-serverless-v2-auto-pause.html) |
| Application Load Balancer | $0.0225 per ALB-hour, $0.008 per LCU-hour | [Elastic Load Balancing pricing](https://aws.amazon.com/elasticloadbalancing/pricing/) |
| ElastiCache for Valkey, `cache.t4g.micro` | $0.0128 per node-hour | AWS Price List API, `AmazonElastiCache` us-east-1, publication 2026-10-14 |
| Public IPv4 address | $0.005 per address-hour, in use or idle | [VPC pricing](https://aws.amazon.com/vpc/pricing/) |
| Secrets Manager | $0.40 per secret per month, prorated hourly (about $0.00056 an hour); $0.05 per 10,000 calls | [Secrets Manager pricing](https://aws.amazon.com/secrets-manager/pricing/) |
| CloudFront | free each month: 1 TB out, 10,000,000 requests; then $0.0100 per 10,000 HTTPS requests and $0.085 per GB (US, next 9 TB) | [CloudFront pay-as-you-go](https://aws.amazon.com/cloudfront/pricing/pay-as-you-go/) |
| CloudWatch | free: 10 alarm metrics, 3 dashboards, 5 GB of logs; then $0.10 per alarm metric-month, $0.50 per GB ingested | [CloudWatch pricing](https://aws.amazon.com/cloudwatch/pricing/) |
| Cognito user pools | free: 10,000 monthly active users per account; then $0.015 per MAU (Essentials) | [Cognito pricing](https://aws.amazon.com/cognito/pricing/) |

## What runs

| Resource | Terraform | Count | Size |
|---|---|---|---|
| The monolith | `aws_ecs_service.api` | 2, autoscaling up to 6 | 0.5 vCPU, 1 GB (the API and its OpenTelemetry collector share it) |
| The gateway | `aws_ecs_service.gateway` | 2 | 0.25 vCPU, 0.5 GB |
| Load balancers | `aws_lb.api` (public), `aws_lb.internal` | 2 | |
| Public IPv4 addresses | one per task (no NAT gateway), and the public load balancer's nodes in 2 zones | 6 idle, up to 10 busy | |
| Aurora PostgreSQL 17 | `aws_rds_cluster_instance.writer`, `.reader` | 2 | Serverless v2, 0 to 2 ACUs each, pauses after 5 idle minutes |
| Valkey | `aws_elasticache_replication_group.valkey` | 1 node | `cache.t4g.micro` |
| Secrets | the owner, four modules, Pledges' jobs role, two read connections (Campaigns, Pledges), the webhook secret | 9 | |
| CloudFront | `aws_cloudfront_distribution.public` | 1 | per request |

There is no NAT gateway: the tasks sit in public subnets with public addresses, which is cheaper
than one NAT gateway for a sandbox (see lesson s2-l03).

## Per hour

**Idle** (deployed, no traffic, the database paused):

| Item | Math | Per hour |
|---|---|---|
| Monolith tasks | 2 × (0.5 × $0.0404784 + 1 × $0.004446) = 2 × $0.02469 | $0.0494 |
| Gateway tasks | 2 × (0.25 × $0.0404784 + 0.5 × $0.004446) = 2 × $0.01234 | $0.0247 |
| Public IPv4 | 6 × $0.005 | $0.0300 |
| Load balancers | 2 × $0.0225 (LCUs near zero) | $0.0450 |
| Aurora | paused: no instance charge | $0.0000 |
| Valkey | 1 × $0.0128 | $0.0128 |
| Secrets | 9 × $0.40 / 720 h | $0.0050 |
| **Total** | | **≈ $0.17** |

**Under the load profile** (6 monolith tasks, both database instances at their 2-ACU ceiling,
about 2 LCUs per load balancer):

| Item | Math | Per hour |
|---|---|---|
| Monolith tasks | 6 × $0.02469 | $0.1481 |
| Gateway tasks | 2 × $0.01234 | $0.0247 |
| Public IPv4 | 10 × $0.005 | $0.0500 |
| Load balancers | 2 × $0.0225 + 4 LCU × $0.008 | $0.0770 |
| Aurora | 2 instances × 2 ACU × $0.12 | $0.4800 |
| Valkey | | $0.0128 |
| Secrets | | $0.0050 |
| **Total** | | **≈ $0.80** |

Everything else stays inside the free tiers at the sandbox's scale: CloudFront (a run at scale 0.02
sends about 1,800 totals requests), CloudWatch (6 alarms, 1 dashboard, logs kept for a day), and
Cognito (one creator and a few thousand backers per run; each run's user pool is new, so several
runs in a month add up toward the 10,000 free monthly active users). Aurora storage is a few GB at
$0.10 per GB-month, under a cent an hour.

A one-hour lesson with a load test therefore costs well under a dollar, and an environment
forgotten for a month while idle about $0.17 × 730 ≈ $122, which is why the nightly sweep destroys
anything left up.

## Compared with the loop

The loop's Round 2 production estimate is about $13.4K a month (R2.6), for 30,000 live campaigns,
two `db.r6g.4xlarge` instances, 16 tasks, MSK, WAF and 50 million emails. The sandbox is the same
architecture at the smallest size that still runs every part; its numbers say what each part
costs, not what production would.
