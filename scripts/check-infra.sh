#!/usr/bin/env bash
# The offline checks of every root, with OpenTofu (the default) or Terraform: fmt, init without the
# backend and with the lock file read-only, validate, and the tests against mock providers. No
# credentials and no AWS calls. CI runs it once per tool (ci.yml).
# Usage: scripts/check-infra.sh            # tofu
#        IAC=terraform scripts/check-infra.sh
set -euo pipefail
iac="${IAC:-tofu}"
"$iac" fmt -check -recursive infra
# The roots, and each module that has tests of its own (infra/modules/service-signals).
for dir in infra/bootstrap infra/env infra/services/*/ infra/modules/*/; do
  dir="${dir%/}"
  ls "$dir"/*.tf >/dev/null 2>&1 || continue
  case "$dir" in infra/modules/*) [ -d "$dir/tests" ] || continue ;; esac
  echo "== $iac: $dir"
  "$iac" -chdir="$dir" init -backend=false -input=false -lockfile=readonly
  "$iac" -chdir="$dir" validate
  "$iac" -chdir="$dir" test
done
