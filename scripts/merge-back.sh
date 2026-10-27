#!/usr/bin/env bash
# The evidence for decision merge-back, from the repository alone, as JSON on standard output.
#
#   scripts/merge-back.sh [range]      (default: since the split began, s3-l16..HEAD; without that tag, the last 200 commits)
#
# 1. Changes together: for each pair of services, of the commits that changed either, how many changed
#    both. "Most commits change both" is a share above 0.5. A service is its module and its host; shared
#    code (the platform, the gateway, Terraform) is left out, and a Contracts change counts for its producer.
# 2. Calls between services: each command a Contracts package marks with [RemoteEndpoint], who serves
#    it, and which other modules send it. These are the synchronous calls; each needs a fallback.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -n "${1:-}" ]]; then
  range=$1
elif git rev-parse -q --verify "s3-l16^{commit}" >/dev/null; then
  range="s3-l16..HEAD" # the last monolith tag: every split since is in the range
else
  range="HEAD~200..HEAD"
fi
git rev-parse -q --verify "${range%%..*}^{commit}" >/dev/null || range="HEAD"

services=(campaigns notifications payments pledges)
# Each service's paths: its folder (s5-l12: src/Services/<Name>/, with any client it uses to call another),
# and where its module and host were before, for the history: src/Modules/<Name>/, the s4 hosts beside
# them, and src/Host, the monolith's host that became Pledges'.
prefixes="campaigns=src/Services/Campaigns/;campaigns=src/Modules/Campaigns/;campaigns=src/Services/Campaigns.Host/;notifications=src/Services/Notifications/;notifications=src/Modules/Notifications/;notifications=src/Services/Notifications.Host/;payments=src/Services/Payments/;payments=src/Modules/Payments/;payments=src/Services/Payments.Host/;pledges=src/Services/Pledges/;pledges=src/Modules/Pledges/;pledges=src/Modules/Payments.Client/;pledges=src/Services/Pledges.Host/;pledges=src/Host/"

# One line per commit: the services it changed, each between spaces.
# A move is not a change: renamed files are left out (-M, and R is not in the filter).
changed=$(git log --format='@%H' --name-only -M --diff-filter=ACDMT "$range" -- src | awk -v specs="$prefixes" '
  BEGIN { n = split(specs, entries, ";"); for (i = 1; i <= n; i++) { split(entries[i], kv, "="); prefix[kv[2]] = kv[1] } }
  /^@/ { if (seen) print touched; touched = " "; seen = 1; next }
  NF { for (p in prefix) if (index($0, p) == 1 && index(touched, " " prefix[p] " ") == 0) touched = touched prefix[p] " " }
  END { if (seen) print touched }')
commits=$(git rev-list --count "$range" -- src)

pairs=$(for ((i = 0; i < ${#services[@]}; i++)); do for ((j = i + 1; j < ${#services[@]}; j++)); do
  a=${services[i]}; b=${services[j]}
  either=$(grep -cE " ($a|$b) " <<< "$changed" || true)
  both=$(grep -E " $a " <<< "$changed" | grep -cE " $b " || true)
  jq -n --arg a "$a" --arg b "$b" --argjson either "$either" --argjson both "$both" \
    '{a: $a, b: $b, either: $either, both: $both, share: (if $either == 0 then 0 else ($both / $either) end), mostChangeBoth: (if $either == 0 then false else ($both / $either > 0.5) end)}'
done; done | jq -s .)

calls=$(git grep -h -B1 -E '^public sealed record [A-Za-z]+\(' -- 'src/Services/*/*.Contracts/*.cs' | awk '
  /\[RemoteEndpoint\("/ { split($0, q, "\""); method = q[2]; path = q[4]; next }
  method != "" { match($0, /record [A-Za-z]+/); print substr($0, RSTART + 7, RLENGTH - 7) "\t" method " " path; method = "" }' |
  while IFS=$'\t' read -r command endpoint; do
    server=$(git grep -l -E "record $command\(" -- 'src/Services/*/*.Contracts/*.cs' | head -1 | cut -d/ -f3)
    callers=$(for m in Campaigns Notifications Payments Pledges; do
      [[ "$m" == "$server" ]] && continue
      git grep -q -w "$command" -- "src/Services/$m" ":(exclude)src/Services/$m/$m.Contracts" && echo "$m"
    done | jq -R . | jq -s .)
    jq -n --arg command "$command" --arg endpoint "$endpoint" --arg server "$server" --argjson callers "$callers" \
      '{command: $command, endpoint: $endpoint, server: ($server | ascii_downcase), callers: ($callers | map(ascii_downcase))}'
  done | jq -s .)

jq -n --arg range "$range" --argjson commits "$commits" --argjson pairs "$pairs" --argjson calls "$calls" \
  '{range: $range, commits: $commits, pairs: $pairs, synchronousCalls: $calls}'
