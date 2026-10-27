#!/usr/bin/env bash
# Puts one four-services run together: k6's report, the cost estimate (tests/load/cost-design.json),
# the merge-back evidence (scripts/merge-back.sh) and, for a run in an AWS account, what CloudWatch
# saw per service. Writes artifacts/load/four-services.json and .md, and prints the JSON's path.
#
#   scripts/four-services-report.sh <k6 report json> [cloudwatch json] [merge-back range]
set -euo pipefail
cd "$(dirname "$0")/.."
k6=${1:?usage: four-services-report.sh <k6 report json> [cloudwatch json] [range]}
cloudwatch=${2:-}
dir=$(dirname "$k6")

cost=$(jq '
  def hourly($p): .tasks * (.vcpu * $p.fargateVcpuHour + .memoryGb * $p.fargateGbHour + $p.publicIpv4Hour);
  .prices as $p
  | { kind: "design estimate", basis: .comment,
      monolithPerHour: ([.monolith[] | hourly($p)] | add),
      fourServicesPerHour: ([.fourServices[] | hourly($p)] | add),
      perService: (.fourServices | map_values(hourly($p))),
      math: ((.monolith + .fourServices) | map_values("\(.tasks) × (\(.vcpu) vCPU × $\($p.fargateVcpuHour) + \(.memoryGb) GB × $\($p.fargateGbHour) + one IPv4 address at $\($p.publicIpv4Hour))")) }
  | .extraPerHour = (.fourServicesPerHour - .monolithPerHour)
  | .extraPer30Days = (.extraPerHour * 720)' tests/load/cost-design.json)
merge=$(scripts/merge-back.sh "${3:-}")
server=$( [[ -n "$cloudwatch" ]] && cat "$cloudwatch" || echo null)

jq -n --slurpfile run "$k6" --argjson cost "$cost" --argjson merge "$merge" --argjson server "$server" \
  '$run[0] + {cost: $cost, mergeBack: $merge, serverSide: $server}' > "$dir/four-services.json"

{
  jq -r '.cost.math as $math |
    def n: if . == null then "not observed" else (. * 10 | round / 10 | tostring) end;
    def s: if . == null then "not observed" else "\(n) s" end;
    "## Four services at \(.scale) × the loop'"'"'s rates (\(.where))", "",
    "Served by: \(.topology). Setup: \(.setup).", "",
    "These are \(.numbers).", "",
    "| Service | Requests | Availability (objective) | P99 of its key endpoint (objective) | Met |", "|---|---|---|---|---|",
    (.services | to_entries[] | .value as $s | "| \(.key) | \($s.requests // 0) | \(if $s.availability == null then "no requests" else "\($s.availability * 100000 | round / 1000)%" end) (\($s.objective.availability * 100)%) | \($s.latencyMs.p99 | n) ms (< \($s.objective.p99Ms) ms, \($s.objective.endpoint)) | \(if $s.met == null then "—" elif $s.met then "yes" else "NO" end) |"), "",
    "| Shape check | Expected | Observed | Holds |", "|---|---|---|---|",
    (.shape[] | "| \(.what) | \(.expected) | \(.observed // "not observed") | \(if .ok then "yes" else "NO" end) |"), "",
    "Totals: \(.totals.reads) reads, \(.totals.originRequests) reached the origin (counted at \(.totals.countedAt // "the client")); the displayed total settled \(.displayedTotalsSettledAfterS | s) after the hot tier, and its P99 age was \(.totals.ageP99Ms | n) ms.", "",
    "Close pass: \(.closePass.pledges) pledges; decided \(.closePass.decidedAfterS | s) and settled \(.closePass.settledAfterS | s) after the deadline (target < \(.closePass.targetS) s).", "",
    "Thresholds \(if .thresholdsPassed then "passed" else "FAILED" end); shape \(if .shapeHolds then "holds" else "FAILED" end).", "",
    "### What the tasks cost (\(.cost.kind))", "",
    "| Deployable | Math | Per hour |", "|---|---|---|",
    (.cost.perService | to_entries[] | "| \(.key) | \($math[.key]) | $\(.value * 10000 | round / 10000) |"),
    "| **Four services** | | **$\(.cost.fourServicesPerHour * 10000 | round / 10000)** |",
    "| The monolith | \($math.monolith) | $\(.cost.monolithPerHour * 10000 | round / 10000) |", "",
    "The split adds about $\(.cost.extraPerHour * 10000 | round / 10000) an hour, $\(.cost.extraPer30Days | round) for 30 days: list prices, not a bill.", "",
    "### Merge-back evidence (from the repository)", "",
    "Commits in \(.mergeBack.range) that changed src: \(.mergeBack.commits).", "",
    "| Pair | Changed both / changed either | Most change both? |", "|---|---|---|",
    (.mergeBack.pairs[] | "| \(.a) and \(.b) | \(.both) / \(.either) | \(if .mostChangeBoth then "YES" else "no" end) |"), "",
    (if (.mergeBack.synchronousCalls | length) == 0 then "Synchronous calls between services: none found."
     else "Synchronous calls between services:", "", (.mergeBack.synchronousCalls[] | "- \(.callers | join(", ")) → \(.server): \(.command) (\(.endpoint))") end), "",
    (if .serverSide == null then empty else
      "### Server side, per service (your AWS account, CloudWatch)", "",
      "| Service | Requests | 5xx | Average / maximum response time | Oldest queued message |", "|---|---|---|---|---|",
      (.serverSide.services | to_entries[] | "| \(.key) | \(.value.requests // "—") | \(.value.errors // "—") | \(.value.responseAvgMs // "—") / \(.value.responseMaxMs // "—") ms | \(.value.oldestMessageS // "—") s |"), "" end)
  ' "$dir/four-services.json"
} > "$dir/four-services.md"
echo "$dir/four-services.json"
