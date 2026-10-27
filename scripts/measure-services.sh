#!/usr/bin/env bash
# A learner's exercise in their own AWS account: run the four-services profile against the four services
# deployed from infra/env, then read what each service saw from CloudWatch, and write the same report
# as a local run (artifacts/load/four-services.json and .md), labelled as measured in that account.
#
#   scripts/measure-services.sh <scale>         (0.01 is a few minutes; see tests/load/README.md for cost)
#
# Needs: the account's credentials, the outputs of infra/env (IAC names the tool: tofu by default), k6, jq. The course's own runs make
# no AWS call; this script is for your account only.
set -euo pipefail
cd "$(dirname "$0")/.."
scale=${1:?usage: measure-services.sh <scale>}
dir=artifacts/load
cluster=$("${IAC:-tofu}" -chdir=infra/env output -raw cluster)
project=$("${IAC:-tofu}" -chdir=infra/env output -raw project 2>/dev/null || echo hicleanarch)
start=$(date -u +%Y-%m-%dT%H:%M:%SZ)

WHERE=account TOPOLOGY="four services on ECS Fargate" SETUP="your AWS account, us-east-1, infra/env at $(git rev-parse --short HEAD)" \
  LOAD_SCRIPT=tests/load/four-services.js scripts/load.sh "$scale" || true
end=$(date -u -v+5M +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d '+5 minutes' +%Y-%m-%dT%H:%M:%SZ)

# One query per number: Service Connect's traffic metrics (AWS/ECS, per discovery name) for each service,
# and its queue's oldest message (AWS/SQS). Service Connect gives averages and maxima, not percentiles;
# the P99s in the report are k6's, at the gateway.
queries=$(jq --arg cluster "$cluster" --arg prefix "$project-sandbox-" '
  [.services | to_entries[] | .key as $s | .value as $v | ($s | gsub("[^a-z]"; "")) as $id |
    # Service Connect's metrics for the service's public port, as infra/env/service-monitoring.tf reads them.
    [{ Name: "ClusterName", Value: $cluster }, { Name: "ServiceName", Value: $v.ecsService }, { Name: "DiscoveryName", Value: $v.discoveryName }] as $dims |
    # The 5xx count and the response time are published under TargetDiscoveryName only (CQA-2123).
    [{ Name: "TargetDiscoveryName", Value: $v.discoveryName }] as $target |
    { Id: "\($id)req", MetricStat: { Metric: { Namespace: "AWS/ECS", MetricName: "RequestCount", Dimensions: $dims }, Period: 3600, Stat: "Sum" } },
    { Id: "\($id)err", MetricStat: { Metric: { Namespace: "AWS/ECS", MetricName: "HTTPCode_Target_5XX_Count", Dimensions: $target }, Period: 3600, Stat: "Sum" } },
    { Id: "\($id)avg", MetricStat: { Metric: { Namespace: "AWS/ECS", MetricName: "TargetResponseTime", Dimensions: $target }, Period: 3600, Stat: "Average" } },
    { Id: "\($id)max", MetricStat: { Metric: { Namespace: "AWS/ECS", MetricName: "TargetResponseTime", Dimensions: $target }, Period: 3600, Stat: "Maximum" } },
    { Id: "\($id)lag", MetricStat: { Metric: { Namespace: "AWS/SQS", MetricName: "ApproximateAgeOfOldestMessage", Dimensions: [{ Name: "QueueName", Value: "\($prefix)\($v.queue)" }] }, Period: 3600, Stat: "Maximum" } }
  ]' tests/load/slos.json)
aws cloudwatch get-metric-data --start-time "$start" --end-time "$end" --metric-data-queries "$queries" > "$dir/cloudwatch.raw.json"

jq --slurpfile slos tests/load/slos.json '
  (.MetricDataResults | map({ key: .Id, value: (.Values | if length == 0 then null else max end) }) | from_entries) as $v
  | { services: ($slos[0].services | to_entries | map(.key as $s | { key: $s, value: {
      requests: $v["\($s)req"], errors: $v["\($s)err"], responseAvgMs: $v["\($s)avg"], responseMaxMs: $v["\($s)max"], oldestMessageS: $v["\($s)lag"] } }) | from_entries) }
' "$dir/cloudwatch.raw.json" > "$dir/cloudwatch.json"

scripts/four-services-report.sh "$dir/four-services.k6.json" "$dir/cloudwatch.json"
