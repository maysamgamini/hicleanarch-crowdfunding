#!/usr/bin/env bash
# Runs the four-services profile (tests/load/four-services.js) on this machine and writes
# artifacts/load/four-services.json and .md: a local measurement, labelled as one.
#
#   scripts/load-local.sh [scale] [--at <tag>]
#
#   scale     default 0.01: 30 pledges and 10 page loads a second for a minute, then a hot tier
#   --at tag  run the code of an earlier tag (s3-l13 to s4-l09, while one host still ran every module)
#             with this profile, to check that lesson's promises locally: its host is built from a
#             temporary worktree of that tag
#
# Without --at it runs the four services as compose.yaml's stack does (scripts/local-up.sh): Campaigns,
# Pledges, Payments and Notifications, each in its own container, events through Moto's SNS and SQS, the
# gateway in front. An nginx cache stands in for CloudFront in front of the gateway's totals
# (tests/load/cdn), and k6 runs in the grafana/k6 image; both join the stack's network. The stack and its
# data are removed at the end. With --at it starts PostgreSQL 17, Valkey 8 and nginx on free ports and the
# tag's one host in Development. Either way: fails if a threshold or a shape check fails. Needs Docker,
# the .NET SDK (for --at) and jq. Run it on a quiet machine (load average below the CPU count): the setup
# line of the report records the load average at the start.
set -euo pipefail
cd "$(dirname "$0")/.."
scale=0.01
tag=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --at) tag=${2:?--at needs a tag}; shift 2 ;;
    *) scale=$1; shift ;;
  esac
done

profile=$PWD
dir=$profile/artifacts/load
mkdir -p "$dir"
run="load-$$"
code=$profile

load_average=$(uptime | sed 's/.*load averages*: *//; s/,* .*//')
cpus=$(sysctl -n hw.ncpu 2>/dev/null || nproc)

# The origin requests are counted from the cache's log, which knows which reads it sent upstream; that
# replaces the client's count from the X-Cache header, which cannot tell a read that waited for another's.
report() {
  local origin
  origin=$(docker exec "$run-cdn" sh -c "grep -c ' [0-9]' /var/log/nginx/totals.log || true")
  jq --argjson origin "$origin" '
    (.launch.seconds / 2 | ceil + 2) as $limit
    | .totals.originRequests = $origin | .totals.originPerSecond = ($origin / .launch.seconds) | .totals.countedAt = "the nginx cache"
    | .shape = [.shape[] | if (.what | startswith("CDN path")) then .observed = $origin | .ok = ($origin <= $limit) else . end]
    | .shapeHolds = all(.shape[]; .ok)' "$dir/four-services.k6.json" > "$dir/four-services.k6.json.new"
  mv "$dir/four-services.k6.json.new" "$dir/four-services.k6.json"
  scripts/four-services-report.sh "$dir/four-services.k6.json" > /dev/null
  jq -e .shapeHolds "$dir/four-services.json" >/dev/null || { echo "A shape check failed: see $dir/four-services.md" >&2; status=1; }
  echo "Report: $dir/four-services.md"
}

if [[ -z "$tag" ]]; then
  # The four services, as compose.yaml runs them, on ports and a subnet of their own.
  export COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-load} LOCAL_SUBNET=${LOCAL_SUBNET:-10.252.0.0/24}
  export LOCAL_POSTGRES_PORT=${LOCAL_POSTGRES_PORT:-55532} LOCAL_VALKEY_PORT=${LOCAL_VALKEY_PORT:-56479} LOCAL_MOTO_PORT=${LOCAL_MOTO_PORT:-55101}
  export LOCAL_API_PORT=${LOCAL_API_PORT:-58281} LOCAL_GATEWAY_PORT=${LOCAL_GATEWAY_PORT:-58280}
  compose=(docker compose --profile stack)
  network="${COMPOSE_PROJECT_NAME}_default"
  cleanup() {
    docker rm -f "$run-cdn" "$run-k6" >/dev/null 2>&1 || true
    "${compose[@]}" down -v >/dev/null 2>&1 || true
  }
  trap cleanup EXIT
  "${compose[@]}" down -v >/dev/null 2>&1 || true
  scripts/local-up.sh > "$dir/stack.log" 2>&1 || { tail -30 "$dir/stack.log" >&2; exit 1; }
  auth=$("${compose[@]}" config --format json | jq -c '.services.pledges.environment')
  key=$(jq -r .Auth__LocalSigningKey <<<"$auth")
  client=$(jq -r .Auth__ClientId <<<"$auth")

  docker create --name "$run-cdn" --network "$network" --network-alias cdn -e ORIGIN="gateway:8080" nginx:1.27-alpine >/dev/null
  docker cp "$profile/tests/load/cdn" "$run-cdn:/etc/nginx/templates"
  docker start "$run-cdn" >/dev/null
  for _ in $(seq 60); do docker exec "$run-cdn" wget -q -O /dev/null "http://127.0.0.1:8080/health/live" 2>/dev/null && break; sleep 1; done

  at=$(git -C "$profile" describe --tags --always)
  setup="$(uname -sm), $cpus CPUs, load average $load_average at the start; compose.yaml's stack in Docker (PostgreSQL 17, Valkey 8, Moto for SNS and SQS, the gateway and the four services of $at, Release builds) and an nginx cache"
  docker create --name "$run-k6" --network "$network" grafana/k6:1.3.0 run --quiet \
    -e API_URL="http://gateway:8080" -e PUBLIC_URL="http://cdn:8080" -e LOCAL_SIGNING_KEY="$key" -e LOCAL_CLIENT_ID="$client" \
    -e FORWARD_VIEWERS=1 -e SCALE="$scale" -e WHERE=local -e SETUP="$setup" -e TOPOLOGY="four services behind the gateway, events through SNS and SQS (Moto), as compose.yaml runs them" \
    -e REPORT_DIR=/home/k6 /home/k6/load/four-services.js >/dev/null
  docker cp "$profile/tests/load" "$run-k6:/home/k6/load"
  status=0
  docker start -a "$run-k6" || status=$?
  for f in four-services.k6.json four-services.k6.md; do docker cp "$run-k6:/home/k6/$f" "$dir/$f" 2>/dev/null || true; done
  report
  exit "$status"
fi

cleanup() {
  [[ -n "${host_pid:-}" ]] && kill "$host_pid" 2>/dev/null || true
  docker rm -f "$run-postgres" "$run-valkey" "$run-cdn" "$run-k6" >/dev/null 2>&1 || true
  [[ -n "$tag" ]] && git -C "$profile" worktree remove --force "$code" 2>/dev/null || true
}
trap cleanup EXIT

if [[ -n "$tag" ]]; then
  code=$dir/at-$tag
  git worktree add -q --detach "$code" "$tag"
fi

docker run -d --name "$run-postgres" -e POSTGRES_DB=crowdfunding -e POSTGRES_PASSWORD=postgres -p 127.0.0.1::5432 postgres:17 >/dev/null
docker run -d --name "$run-valkey" -p 127.0.0.1::6379 valkey/valkey:8 >/dev/null
pg=$(docker port "$run-postgres" 5432/tcp | head -1 | cut -d: -f2)
vk=$(docker port "$run-valkey" 6379/tcp | head -1 | cut -d: -f2)
until docker exec "$run-postgres" pg_isready -U postgres -d crowdfunding >/dev/null 2>&1; do sleep 1; done

db() { echo "Host=127.0.0.1;Port=$pg;Database=crowdfunding;Username=$1;Password=$2"; }
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Owner="$(db postgres postgres)"
export ConnectionStrings__Campaigns="$(db campaigns_app campaigns_dev)"
export ConnectionStrings__Pledges="$(db pledges_app pledges_dev)"
export ConnectionStrings__PledgesJobs="$(db pledges_jobs pledges_jobs_dev)"
export ConnectionStrings__Payments="$(db payments_app payments_dev)"
export ConnectionStrings__Notifications="$(db notifications_app notifications_dev)"
export ConnectionStrings__Valkey="127.0.0.1:$vk"
export Pledges__CloseEvery=00:00:05
key=$(jq -r .Auth.LocalSigningKey "$code/src/Host/appsettings.Development.json")
client=$(jq -r .Auth.ClientId "$code/src/Host/appsettings.Development.json")

# From s3-l12 the modules build against packages packed into a local feed.
for pack in pack-buildingblocks.sh pack-contracts.sh; do
  if [[ -x "$code/scripts/$pack" ]]; then (cd "$code" && "scripts/$pack" > "$dir/build.log" 2>&1) || { tail -20 "$dir/build.log" >&2; exit 1; }; fi
done
dotnet build "$code/src/Host" -c Release > "$dir/build.log" 2>&1 || { grep -E "error|Error" "$dir/build.log" | tail -20 >&2; exit 1; }
dotnet run --project "$code/src/Host" -c Release --no-build -- migrate
port=$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
dotnet run --project "$code/src/Host" -c Release --no-build --urls "http://127.0.0.1:$port" > "$dir/host.log" 2>&1 &
host_pid=$!
until curl -fs "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; do
  kill -0 "$host_pid" 2>/dev/null || { cat "$dir/host.log" >&2; exit 1; }
  sleep 1
done

# Containers reach the host's loopback as host.docker.internal. Files go in with docker cp, not a bind
# mount: Docker in a VM (Colima, Docker Desktop) shares only some host folders.
docker create --name "$run-cdn" --add-host=host.docker.internal:host-gateway -e ORIGIN="host.docker.internal:$port" \
  -p 127.0.0.1::8080 nginx:1.27-alpine >/dev/null
docker cp "$profile/tests/load/cdn" "$run-cdn:/etc/nginx/templates"
docker start "$run-cdn" >/dev/null
cdn=$(docker port "$run-cdn" 8080/tcp | head -1 | cut -d: -f2)
for _ in $(seq 60); do curl -fs -o /dev/null "http://127.0.0.1:$cdn/health/live" && break; sleep 1; done
curl -fs -o /dev/null "http://127.0.0.1:$cdn/health/live" || { docker logs "$run-cdn" >&2; exit 1; }

at=${tag:-$(git -C "$profile" describe --tags --always)}
setup="$(uname -sm), $cpus CPUs, load average $load_average at the start; PostgreSQL 17, Valkey 8 and an nginx cache in Docker; the host of $at, Release build"
if command -v k6 >/dev/null; then
  api="http://127.0.0.1:$port"; public="http://127.0.0.1:$cdn"
  k6() { command k6 "$@"; }
  load=$profile/tests/load; out=$dir
else
  api="http://host.docker.internal:$port"; public="http://host.docker.internal:$cdn"
  # The profile goes in and the report comes out with docker cp, as above.
  k6() {
    docker create --name "$run-k6" --add-host=host.docker.internal:host-gateway grafana/k6:1.3.0 "$@" >/dev/null
    docker cp "$profile/tests/load" "$run-k6:/home/k6/load"
    local code=0
    docker start -a "$run-k6" || code=$?
    for f in four-services.k6.json four-services.k6.md; do docker cp "$run-k6:/home/k6/$f" "$dir/$f" 2>/dev/null || true; done
    return "$code"
  }
  load=/home/k6/load; out=/home/k6
fi

status=0
k6 run --quiet -e API_URL="$api" -e PUBLIC_URL="$public" -e LOCAL_SIGNING_KEY="$key" -e LOCAL_CLIENT_ID="$client" \
  -e SCALE="$scale" -e WHERE=local -e SETUP="$setup" -e TOPOLOGY="one host composing every module of $at (in-process events)" \
  -e REPORT_DIR="$out" "$load/four-services.js" || status=$?

report
exit "$status"
