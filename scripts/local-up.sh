#!/usr/bin/env bash
# Starts the local stack (compose.yaml, profile `stack`) the way a deploy does on AWS: the data stores,
# then the broker's topics and queues (what Terraform builds, from events/broker-subscriptions.json),
# then the migration task, then the hosts and the gateway. Then it checks that no endpoint is on AWS.
#
# Usage: scripts/local-up.sh            then scripts/local-smoke.sh
#        docker compose --profile stack down -v     to remove everything, data included
set -euo pipefail
cd "$(dirname "$0")/.."
compose=(docker compose --profile stack)

# Every host, URL and connection string the stack is given names a service of compose.yaml or this
# machine, and nothing names AWS: the local stack is the course's only end-to-end run, and it is free.
assert_local() {
  local config services hosts
  config=$("${compose[@]}" config)
  services=" $("${compose[@]}" config --services | tr '\n' ' ') localhost 127.0.0.1 "
  if grep -qiE 'amazonaws\.com|amazonaws\.com\.cn' <<<"$config"; then
    echo "local-up: an AWS endpoint is configured:" >&2
    grep -iE 'amazonaws' <<<"$config" >&2
    return 1
  fi
  hosts=$({
    grep -oE '[a-z]+://[^/:" ]+' <<<"$config" | sed -E 's#^[a-z]+://##'
    grep -oE 'Host=[^;" ]+' <<<"$config" | cut -d= -f2
    grep -oE '[A-Za-z0-9.-]+:6379' <<<"$config" | cut -d: -f1
  } | sort -u)
  for host in $hosts; do
    if [[ "$services" != *" $host "* ]]; then
      echo "local-up: $host is not a service of compose.yaml or this machine" >&2
      return 1
    fi
  done
  echo "every endpoint is local: $(tr '\n' ' ' <<<"$hosts")"
}

assert_local
mkdir -p .local
"${compose[@]}" build --quiet
"${compose[@]}" up -d --wait postgres valkey moto
"${compose[@]}" run --rm -T broker-setup >.local/broker.env.new
mv .local/broker.env.new .local/broker.env
assert_local # again, with the topics and queues just written
"${compose[@]}" up -d --wait payments notifications campaigns api gateway
echo "the local stack is up: the gateway is on http://localhost:${LOCAL_GATEWAY_PORT:-8080}"
