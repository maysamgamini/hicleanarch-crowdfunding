#!/usr/bin/env bash
# Packs the BuildingBlocks into the local feed for this machine, under a fresh prerelease version
# (<version>-local.<UTC time>), and records that version in artifacts/packages/local-version.props, which
# Directory.Packages.props reads. A new version each time means NuGet never serves a stale copy from
# its package cache after the BuildingBlocks change. CI and Docker images pack the release version
# into a clean feed and do not use this script.
set -euo pipefail
cd "$(dirname "$0")/.."

base=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/BuildingBlocks/Directory.Build.props)
version="$base-local.$(date -u +%Y%m%d%H%M%S)"

mkdir -p artifacts/packages
rm -f artifacts/packages/CrowdFunding.BuildingBlocks.*-local.*
dotnet pack BuildingBlocks.slnx -o artifacts/packages -p:Version="$version"
cat > artifacts/packages/local-version.props <<PROPS
<Project>
  <PropertyGroup>
    <BuildingBlocksVersion>$version</BuildingBlocksVersion>
  </PropertyGroup>
</Project>
PROPS
echo "BuildingBlocks $version packed; the modules restore it."
