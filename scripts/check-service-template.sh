#!/usr/bin/env bash
# Every service host is made from templates/service and still matches it: each one is generated
# again into a scratch folder and compared, file by file, with the repository's copy. A change to
# the plumbing goes into the template (or the platform), never into one host alone.
set -euo pipefail
cd "$(dirname "$0")/.."

# <module> <layout> <port>, as each host was created.
services=(
)

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
dotnet new install ./templates/service --debug:custom-hive "$scratch/hive" >/dev/null

status=0
for service in "${services[@]+"${services[@]}"}"; do
  read -r module layout port <<<"$service"
  out="$scratch/$module"
  dotnet new crowdfunding-service -n "$module" --layout "$layout" --port "$port" -o "$out" \
    --debug:custom-hive "$scratch/hive" >/dev/null
  while IFS= read -r file; do
    relative=${file#"$out"/}
    if ! diff -u "$file" "$relative"; then
      echo "$relative differs from templates/service ($module)." >&2
      status=1
    fi
  done < <(find "$out" -type f)
done

[ "$status" -eq 0 ] && echo "Every service host matches templates/service."
exit "$status"
