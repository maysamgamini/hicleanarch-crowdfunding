#!/usr/bin/env bash
# Makes a service host from templates/service in a throwaway worktree of HEAD, builds it and runs the
# tests the template generated for it (lesson s5-l01): a new service is one command, and it starts,
# answers its health checks and serves its module alone. Needs Docker (the tests' PostgreSQL).
#
#   scripts/try-service-template.sh <Module> <layered|single> <port> [service|modules]
#   scripts/try-service-template.sh Notifications single 5181
set -euo pipefail
cd "$(dirname "$0")/.."
module=${1:?usage: try-service-template.sh <Module> <layered|single> <port>}
layout=${2:?layout}
port=${3:?port}
module_in=${4:-service}

scratch=$(mktemp -d)
trap 'git worktree remove --force "$scratch/repo" >/dev/null 2>&1 || true; rm -rf "$scratch"' EXIT
git worktree add --detach "$scratch/repo" HEAD >/dev/null
cp -R artifacts "$scratch/repo/" 2>/dev/null || true
dotnet new install ./templates/service --debug:custom-hive "$scratch/hive" >/dev/null
dotnet new crowdfunding-service -n "$module" --layout "$layout" --port "$port" --moduleIn "$module_in" -o "$scratch/repo" \
  --debug:custom-hive "$scratch/hive" >/dev/null

cd "$scratch/repo"
[ -f artifacts/packages/local-version.props ] || ./scripts/pack-buildingblocks.sh >/dev/null
[ -x scripts/pack-contracts.sh ] && ./scripts/pack-contracts.sh >/dev/null
dotnet build "src/Services/$module/$module.Host" -warnaserror
dotnet test "src/Services/$module/tests/$module.Host.Tests"
echo "$module.Host, made from templates/service, builds and passes its tests."
