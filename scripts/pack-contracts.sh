#!/usr/bin/env bash
# Packs each module's Contracts into the local feed for this machine, each under a fresh prerelease of
# its own version (<version>-local.<UTC time>), and records those versions in
# artifacts/packages/local-contracts-version.props, which Directory.Packages.props reads. The Contracts
# are built on the BuildingBlocks package, so the BuildingBlocks are packed fresh first.
# CI and Docker images pack the release versions into a clean feed and do not use this script.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/pack-buildingblocks.sh

stamp=$(date -u +%Y%m%d%H%M%S)
rm -f artifacts/packages/CrowdFunding.*.Contracts.*-local.*
{
  echo "<Project>"
  echo "  <PropertyGroup>"
  for project in src/Modules/*/*.Contracts/*.Contracts.csproj src/Services/*/*.Contracts/*.Contracts.csproj; do
    dir=$(dirname "$project")
    module=$(basename "$dir" .Contracts)
    base=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$dir/Directory.Build.props")
    version="$base-local.$stamp"
    dotnet pack "$project" -o artifacts/packages -p:Version="$version" >&2
    echo "    <${module}ContractsVersion>$version</${module}ContractsVersion>"
  done
  echo "  </PropertyGroup>"
  echo "</Project>"
} > artifacts/packages/local-contracts-version.props.new
mv artifacts/packages/local-contracts-version.props.new artifacts/packages/local-contracts-version.props
echo "Contracts packed (local.$stamp); the modules restore them."
