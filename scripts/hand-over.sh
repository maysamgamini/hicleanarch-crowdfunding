#!/usr/bin/env bash
# Hands one module's schema to its service's migration role, once, in the release that gives the
# module its own host (s5-l03, s5-l08): the shared layer's hand-over task (infra/env/<name>-network.tf)
# runs the service's image with `hand-over`, as the database owner. From then on only the service's
# migrate step changes that schema; the monolith's skips it. Run it as the shared deploy role, after
# the shared layer is applied with <name>_image set and before the service's first deploy. Running it
# again changes nothing.
# Usage: hand-over.sh <name>
set -euo pipefail
name="${1:?usage: hand-over.sh <name>}"
task=$("${IAC:-tofu}" -chdir=infra/env output -json handover_task_definitions | jq -r --arg name "$name" '.[$name] // empty')
test -n "$task" || { echo "no hand-over task for $name: apply infra/env with ${name}_image set" >&2; exit 2; }
"$(dirname "$0")/run-migration.sh" infra/env "$task"
