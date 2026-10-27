#!/usr/bin/env bash
# Checks that every contract change since a base commit carries the version SemVer asks for: the
# base commit's contract snapshots against this commit's, by the producer tests' own rules (a breaking
# change needs a new major version and a higher event version, an addition a new minor version).
#   scripts/check-contracts.sh origin/main
set -euo pipefail
cd "$(dirname "$0")/.."

base=${1:?usage: scripts/check-contracts.sh <base commit>}
if ! git cat-file -e "$base^{commit}" 2>/dev/null; then
  git fetch -q --depth=1 origin "$base"
fi

baseline=$(mktemp -d)
trap 'rm -rf "$baseline"' EXIT
# A base from before the snapshots existed has none; then there is nothing to compare.
git ls-tree -r --name-only "$base" -- src/Modules src/Services | { grep '\.Contracts/contract/contract\.json$' || true; } | while read -r snapshot; do
  mkdir -p "$baseline/$(dirname "$snapshot")"
  git show "$base:$snapshot" > "$baseline/$snapshot"
done

CONTRACT_BASELINE="$baseline" dotnet test tests/Contracts.ProducerTests \
  --filter "FullyQualifiedName~BaselineTests"
