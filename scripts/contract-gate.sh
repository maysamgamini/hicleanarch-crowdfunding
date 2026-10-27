#!/usr/bin/env bash
# The deploy gate for contracts (tools/ContractGate), from the repository root. Exit 0 allows, 1 refuses
# with the reasons, 2 is a usage error.
#   scripts/contract-gate.sh can-i-deploy --record <dir> --service <name> --version <sha> --ecs <service>=<sha> ...
#   scripts/contract-gate.sh record       --record <dir> --service <name> --version <sha>
#   scripts/contract-gate.sh can-retire   --record <dir> --type <Producer>.<Name> [--command] [--database <conn>]
# The record is one directory per environment on the repository's `deployments` branch, a file per service.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet run --project tools/ContractGate -c Release -v quiet -- "$@"
