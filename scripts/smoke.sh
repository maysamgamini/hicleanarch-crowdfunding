#!/usr/bin/env bash
# Smoke test against a running API: create a draft as a creator, read it back.
# Usage: TOKEN=<creator access token> smoke.sh <base url>
set -euo pipefail
base="${1:?usage: TOKEN=<token> smoke.sh <base url>}"
token="${TOKEN:?set TOKEN to the access token of a creator}"

for _ in $(seq 1 30); do
  curl -sf "$base/health" >/dev/null && break
  sleep 5
done

test "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$base/v1/campaigns" -H 'Content-Type: application/json' -d '{}')" = "401"

created=$(curl -sf -X POST "$base/v1/campaigns" -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
  -d '{"title":"Smoke test","goal_minor":100000,"currency":"USD","deadline":"2030-01-01T00:00:00Z","tiers":[]}')
id=$(echo "$created" | sed -E 's/.*"campaign_id":"([^"]+)".*/\1/')
curl -sf "$base/v1/campaigns/$id" | grep -q '"status":"DRAFT"'
echo "smoke test passed: $id"
