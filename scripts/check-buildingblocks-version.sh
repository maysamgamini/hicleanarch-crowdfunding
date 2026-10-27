#!/usr/bin/env bash
# Each commit that changes the BuildingBlocks' code changes their version too (CQA-2112). The packages are
# published by version (package.yml, tag bb-v<version>), and a published version cannot be replaced, so
# two different builds must never carry one number. The README and this file's own props are not code.
# Usage: scripts/check-buildingblocks-version.sh <base>     checks the commits base..HEAD
set -euo pipefail
base="${1:?usage: check-buildingblocks-version.sh <base commit>}"
props=src/BuildingBlocks/Directory.Build.props
version() { git show "$1:$props" 2>/dev/null | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p'; }

failed=0
for commit in $(git rev-list --reverse "$base..HEAD"); do
  changed=$(git diff-tree --no-commit-id --name-only -r "$commit" -- src/BuildingBlocks \
    | grep -v -e '/README\.md$' -e "^$props\$" || true)
  [ -z "$changed" ] && continue
  before=$(version "$commit^")
  after=$(version "$commit")
  if [ "$before" = "$after" ]; then
    echo "$(git log -1 --format='%h %s' "$commit"): changes the BuildingBlocks' code and keeps version $after." >&2
    echo "$changed" | sed 's/^/  /' >&2
    failed=1
  fi
done
if [ "$failed" = 0 ]; then
  echo "Every BuildingBlocks change in $base..HEAD comes with a new version."
fi
exit "$failed"
