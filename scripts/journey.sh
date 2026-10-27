#!/usr/bin/env bash
# Where is this campaign's journey? Every step each service logged for it, in time order, from the
# services' log groups, and a GAP line for each service that logged none (lesson s5-l13).
#
#   scripts/journey.sh <campaign-id> [--services campaigns,pledges] [--since 1d] [--results file.json]
#
# <campaign-id>  the campaign's id as the events carry it (a GUID, not the public cmp_ id)
# --services     the log groups to read, by service name: /hicleanarch/sandbox/<service>; all four by default
# --since        how far back to look (the sandbox keeps 1 day of logs; production 120 days)
# --results      read a saved `aws logs get-query-results` reply instead of running the query
#
# The query is infra/env/journey.logsinsights, the same text OpenTofu saves as the console's query.
# Run it with the operators' read-only role (infra/bootstrap/operators.tf); it only reads.
set -euo pipefail

usage() { echo "usage: $0 <campaign-id> [--services a,b] [--since 1d] [--results file]" >&2; exit 2; }
[ $# -ge 1 ] || usage
campaign=$1
shift
services=${JOURNEY_SERVICES:-campaigns,pledges,payments,notifications}
since=1d
results=""
while [ $# -gt 0 ]; do
  case $1 in
    --services) services=$2; shift 2 ;;
    --since) since=$2; shift 2 ;;
    --results) results=$2; shift 2 ;;
    *) usage ;;
  esac
done
[[ $campaign =~ ^[0-9a-fA-F-]{36}$ ]] || { echo "the campaign id is a GUID: $campaign" >&2; exit 2; }

root="$(cd "$(dirname "$0")/.." && pwd)"
project=${PROJECT:-hicleanarch}

if [ -z "$results" ]; then
  query=$(sed "s/CAMPAIGN_ID/$campaign/" "$root/infra/env/journey.logsinsights")
  groups=()
  for s in ${services//,/ }; do groups+=("/$project/sandbox/$s"); done
  seconds=$(( ${since%d} * 86400 ))
  id=$(aws logs start-query --log-group-names "${groups[@]}" --query-string "$query" \
    --start-time $(( $(date +%s) - seconds )) --end-time "$(date +%s)" --query queryId --output text)
  results=$(mktemp)
  trap 'rm -f "$results"' EXIT
  until aws logs get-query-results --query-id "$id" > "$results" && jq -e '.status == "Complete"' "$results" > /dev/null; do
    jq -e '.status == "Failed" or .status == "Cancelled" or .status == "Timeout"' "$results" > /dev/null && { echo "the query ended: $(jq -r .status "$results")" >&2; exit 1; }
    sleep 1
  done
fi

# One row per step: time, service (the log group's last segment), step, pledge, outcome.
jq -r '.results[] | map({(.field): .value}) | add
  | [.["@timestamp"], (.["@log"] | split("/") | last), .step, (.pledge // "-"), .outcome] | @tsv' "$results"

# A service with no line for the campaign is a gap in the view, never a silent omission.
for s in ${services//,/ }; do
  jq -e --arg s "$s" '[.results[] | map({(.field): .value}) | add | select(.["@log"] | split("/") | last == $s)] | length > 0' "$results" > /dev/null \
    || printf 'GAP\t%s\tno step logged for this campaign\n' "$s"
done
