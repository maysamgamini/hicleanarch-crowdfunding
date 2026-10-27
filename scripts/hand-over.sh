#!/usr/bin/env bash
# Hands one module's schema to its service's migration role, once, in the release that gives the
# module its own host (s5-l03, s5-l08): the shared layer's hand-over task (infra/env/<name>-network.tf)
# runs the service's image with `hand-over`, as the database owner. From then on only the service's
# migrate step changes that schema; the monolith's skips it. Run it as the shared deploy role, after
# the shared layer is applied with <name>_image set and before the service's first deploy. Running it
# again changes nothing.
#
# A command after the name runs instead of `hand-over`, in the same task: the service's image as the
# database owner. The one such command is Pledges' drop-moved-payments (CQA-2102, CQA-2117): what s4-l07's
# move left of Payments on the shared cluster leaves a week after the fence. No deploy runs it: a sandbox
# rebuilt since s5-l10 has nothing of the move. An environment kept up since s4-l07 runs it once, by hand,
# a week or more after the fence: hand-over.sh pledges drop-moved-payments
# Usage: hand-over.sh <name> [command...]
set -euo pipefail
name="${1:?usage: hand-over.sh <name> [command...]}"
shift
task=$("${IAC:-tofu}" -chdir=infra/env output -json handover_task_definitions | jq -r --arg name "$name" '.[$name] // empty')
test -n "$task" || { echo "no hand-over task for $name: apply infra/env with ${name}_image set" >&2; exit 2; }
CONTAINER=hand-over "$(dirname "$0")/run-migration.sh" infra/env "$task" "$@"
