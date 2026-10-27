#!/usr/bin/env bash
# The end-to-end smoke test of the local stack (scripts/local-up.sh): one campaign from draft to
# settled, through the gateway, as a creator and a backer would take it, with the fake PSP played by
# this script. It asserts the final state through the API, then the emails, the broker and the
# dead-letter queues. It never changes the database or the clock behind the API's back: the
# campaign's deadline is real, 40 seconds away, and the close job runs every 30 s, so a run takes
# about a minute and a half.
#
# Usage: scripts/local-smoke.sh      (GATEWAY_URL defaults to http://127.0.0.1:8080)
set -euo pipefail
cd "$(dirname "$0")/.."
compose=(docker compose --profile stack)
gateway="${GATEWAY_URL:-http://127.0.0.1:${LOCAL_GATEWAY_PORT:-8080}}"
deadline_in="${DEADLINE_SECONDS:-40}"
webhook_secret="whsec_development_only_0123456789" # compose.yaml: the payments service's Payments__WebhookSecret
started=$(date +%s)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

step() { printf '%4ss  %s\n' "$(($(date +%s) - started))" "$*"; }
fail() {
  echo "FAILED after $(($(date +%s) - started)) s: $*" >&2
  exit 1
}
key() { od -An -tx1 -N16 /dev/urandom | tr -d ' \n'; }
# A string field of a JSON reply (the replies are flat enough for this; no jq needed).
field() { sed -nE "s/.*\"$1\":\"([^\"]*)\".*/\1/p" <<<"$2" | head -n 1; }
sql() { "${compose[@]}" exec -T postgres psql -U postgres -d crowdfunding -tAXc "$1"; }
uuid_of() { echo "${1#*_}"; } # cmp_<32 hex> -> the uuid PostgreSQL reads
utc() { date -u -r "$1" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "@$1" +%Y-%m-%dT%H:%M:%SZ; }

# A request through the gateway; prints "<status> <body>". A change carries an Idempotency-Key and is
# sent again with the same key while the API answers 409 with Retry-After (busy, nothing kept).
call() {
  local method="$1" path="$2" token="${3:-}" body="${4:-}" id reply status
  id=$(key)
  for _ in $(seq 1 30); do
    local args=(-s -o "$scratch/body" -w '%{http_code}' -X "$method" "$gateway$path" -D "$scratch/headers")
    [[ -n "$token" ]] && args+=(-H "Authorization: Bearer $token")
    [[ "$method" != GET ]] && args+=(-H "Idempotency-Key: $id")
    [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
    status=$(curl "${args[@]}")
    reply=$(cat "$scratch/body")
    if [[ "$status" == 409 ]] && grep -qi '^retry-after:' "$scratch/headers"; then
      sleep "$(grep -i '^retry-after:' "$scratch/headers" | tr -dc '0-9')"
      continue
    fi
    break
  done
  rm -f "$scratch/body" "$scratch/headers"
  echo "$status $reply"
}

expect() { # expect <status> <reply of call> <what>
  [[ "${2%% *}" == "$1" ]] || fail "$3: expected $1, got ${2:0:400}"
  echo "${2#* }"
}

# Waits up to $1 seconds for "$2" (a command) to succeed.
eventually() {
  local timeout=$1 until
  shift
  until=$(($(date +%s) + timeout))
  until "$@"; do
    (($(date +%s) < until)) || return 1
    sleep 1
  done
}

# Whether GET $1 (with curl options ${@:3}) shows status $2; the last reply is kept for the failure message.
status_is() {
  curl -s -w ' (HTTP %{http_code})' "${@:3}" "$gateway$1" >"$scratch/last" || true
  [[ "$(field status "$(cat "$scratch/last")")" == "$2" ]]
}
last() { head -c 400 "$scratch/last"; }

step "the gateway answers"
eventually 60 curl -sf "$gateway/health" -o /dev/null || fail "the gateway is not up; run scripts/local-up.sh"
[[ "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$gateway/v1/campaigns")" == 401 ]] || fail "a change without a token was not refused"

step "tokens from the local issuer: a creator and a backer"
token() { "${compose[@]}" run --rm --no-deps -T -e ASPNETCORE_ENVIRONMENT=Development api token "$1"; }
creator=$(token creator)
backer=$(token backer)
[[ "$creator" == *.*.* && "$backer" == *.*.* ]] || fail "the token command printed no token"

step "a notification preference: the gateway sends it to the Notifications service (s5-l03)"
expect 204 "$(call PUT /v1/me/notifications/campaign_updates "$backer" '{"enabled":true}')" "set a preference"
"${compose[@]}" logs notifications 2>/dev/null | grep -q '/v1/me/notifications/campaign_updates' ||
  [[ "$(sql "SELECT count(*) FROM notifications.notification_preferences WHERE kind = 'campaign_updates'")" -ge 1 ]] ||
  fail "the preference was not stored"

deadline=$(utc $(($(date +%s) + deadline_in)))
step "draft: a campaign with its deadline at $deadline"
draft='{"title":"Local smoke","goal_minor":5000,"currency":"USD","deadline":"'"$deadline"'","tiers":[{"title":"Thanks","min_pledge_minor":500,"limit":null}]}'
reply=$(expect 201 "$(call POST /v1/campaigns "$creator" "$draft")" "create the draft")
campaign=$(field campaign_id "$reply")
[[ -n "$campaign" ]] || fail "no campaign_id in $reply"
# A draft is shown only to its creator (CQA-505).
status_is "/v1/campaigns/$campaign" DRAFT -H "Authorization: Bearer $creator" || fail "the new campaign is not a DRAFT: $(last)"

step "publish $campaign"
expect 200 "$(call POST "/v1/campaigns/$campaign/publish" "$creator")" "publish" >/dev/null
status_is "/v1/campaigns/$campaign" LIVE || fail "the published campaign is not LIVE: $(last)"
# Pledges opens the funding window when it gets CampaignPublished; its totals answer from then on.
eventually 30 curl -sf "$gateway/v1/campaigns/$campaign/totals" -o /dev/null ||
  fail "Pledges never opened the funding window: $(curl -s -w ' (HTTP %{http_code})' "$gateway/v1/campaigns/$campaign/totals" | head -c 400)"

step "pledge 60.00 USD with a new card"
pledge_request='{"amount_minor":6000,"tier_id":null,"payment_method_id":null}'
reply=$(expect 201 "$(call POST "/v1/campaigns/$campaign/pledges" "$backer" "$pledge_request")" "pledge")
pledge=$(field pledge_id "$reply")
secret=$(field client_secret "$reply")
[[ "$(field status "$reply")" == PENDING_SETUP && -n "$secret" ]] || fail "the pledge is not waiting for its card: $reply"

step "the PSP: the backer finished the card form; its signed webhook comes in through the gateway"
body='{"id":"evt_'"$(key)"'","type":"setup_succeeded","data":{"setup_id":"'"${secret%%_secret_*}"'","payment_method":"pm_card_visa"}}'
t=$(date +%s)
signature="t=$t,v1=$(printf '%s' "$t.$body" | openssl dgst -sha256 -hmac "$webhook_secret" | sed 's/^.* //')"
received=$(curl -s -w ' %{http_code}' -X POST "$gateway/v1/webhooks/psp" -H 'Content-Type: application/json' -H "Psp-Signature: $signature" -d "$body")
[[ "$received" == *" 200" ]] || fail "the webhook was refused: $received"
# Payments' PaymentMethodSaved reaches Pledges only through SNS and SQS (Moto).
eventually 60 status_is "/v1/pledges/$pledge" ACTIVE -H "Authorization: Bearer $backer" || fail "the pledge never became ACTIVE: $(last)"

step "wait for the deadline; the close job decides the campaign (every 30 s)"
eventually $((deadline_in + 90)) status_is "/v1/campaigns/$campaign" FUNDED ||
  status_is "/v1/campaigns/$campaign" SETTLED || fail "the campaign was never decided: $(last)"

step "charge and settle: Pledges asks Payments to charge, through the broker, and Payments settles"
eventually 120 status_is "/v1/pledges/$pledge" CHARGED -H "Authorization: Bearer $backer" || fail "the pledge was never CHARGED: $(last)"
eventually 60 status_is "/v1/campaigns/$campaign" SETTLED || fail "the campaign was never SETTLED: $(last)"
settlement=$(expect 200 "$(call GET "/v1/campaigns/$campaign/settlement" "$creator")" "settlement")
[[ "$(field status "$settlement")" == DONE && "$settlement" == *'"charged":1'* && "$settlement" == *'"failed":0'* ]] ||
  fail "the settlement is not one charge done: $settlement"

step "the emails: the backer's three and the creator's summary"
c=$(uuid_of "$campaign")
p=$(uuid_of "$pledge")
# The emails the paced worker has sent (CQA-907): the creator hears the decision at once and the
# settlement after (CQA-400); the backer is told by their own pledge's PledgeDecided (CQA-1625).
kinds() { sql "SELECT string_agg(kind, ',' ORDER BY kind) FROM notifications.outgoing_emails WHERE status = 'SENT' AND recipient_id = ($1)"; }
eventually 30 test "$(kinds "SELECT creator_id FROM notifications.campaign_creators WHERE campaign_id = '$c'")" == campaign_decided,campaign_settled ||
  fail "the creator was not told the campaign was decided and settled: $(kinds "SELECT creator_id FROM notifications.campaign_creators WHERE campaign_id = '$c'")"
eventually 30 test "$(kinds "SELECT backer_id FROM pledges.pledges WHERE id = '$p'")" == campaign_funded,charge_succeeded,pledge_confirmed || true
emails=$(kinds "SELECT backer_id FROM pledges.pledges WHERE id = '$p'")
[[ "$emails" == campaign_funded,charge_succeeded,pledge_confirmed ]] || fail "the backer's emails are $emails"

step "the broker: every event owed to it was published, and nothing is dead-lettered"
unpublished=$(sql "SELECT (SELECT count(*) FROM campaigns.outbox WHERE published_at IS NULL AND 'broker' = ANY (sinks))
  + (SELECT count(*) FROM pledges.outbox WHERE published_at IS NULL AND 'broker' = ANY (sinks))
  + (SELECT count(*) FROM payments.outbox WHERE published_at IS NULL AND 'broker' = ANY (sinks))")
[[ "$unpublished" == 0 ]] || fail "$unpublished events still owed to the broker"
crossed=$(sql "SELECT string_agg(DISTINCT type, ',' ORDER BY type) FROM (
  SELECT type FROM pledges.outbox WHERE published_at IS NOT NULL AND payload->>'campaignId' = '$c'::uuid::text
  UNION ALL SELECT type FROM payments.outbox WHERE published_at IS NOT NULL AND payload->>'campaignId' = '$c'::uuid::text) t")
for type in CampaignFundingDecided PledgeChargeRequested PledgeChargeSucceeded CampaignSettled; do
  [[ ",$crossed," == *",$type,"* ]] || fail "$type for this campaign never went through the broker (published: $crossed)"
done
dead=$("${compose[@]}" exec -T moto python3 -c '
import boto3
sqs = boto3.client("sqs", endpoint_url="http://localhost:5000", region_name="us-east-1", aws_access_key_id="test", aws_secret_access_key="test")
urls = sqs.list_queues(QueueNamePrefix="hicleanarch-local").get("QueueUrls", [])
print(sum(int(sqs.get_queue_attributes(QueueUrl=u, AttributeNames=["ApproximateNumberOfMessages"])["Attributes"]["ApproximateNumberOfMessages"]) for u in urls if u.endswith("dlq")))')
[[ "$dead" == 0 ]] || fail "$dead messages in the dead-letter queues"

step "smoke test passed: $campaign settled, $pledge charged, in $(($(date +%s) - started)) s"
