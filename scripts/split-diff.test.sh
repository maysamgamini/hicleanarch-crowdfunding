#!/usr/bin/env bash
# Tests of scripts/split-diff.sh on a throwaway repository: each class (none, permitted,
# boundary-correction, release-1-fix, forbidden), the commit-subject rule, the change kind, the
# Notifications exclusions, a service's move (renames) and malformed allow-lists.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/split-diff.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"

git init -q -b main .
git config user.email test@example.com
git config user.name test
write() { mkdir -p "$(dirname "$1")"; printf '%s\n' "$2" > "$1"; }
commit() { git add -A && git commit -qm "$1" && git tag "$2"; }

write src/Modules/Pledges/Pledges.Domain/Pledge.cs 'class Pledge {}'
write src/Modules/Pledges/Pledges.Application/PlacePledge.cs 'class PlacePledge {}'
write src/Modules/Pledges/Pledges.Infrastructure/Adapter.cs 'class Adapter {}'
write src/Modules/Notifications/Handlers.cs 'class Handlers {}'
write src/Modules/Notifications/SesEmailSender.cs 'class Ses {}'
write split-allowed.yaml 'allowed: []'
commit base base

failures=0
expect() { # expect <exit> <grep pattern> -- <args>
  local want=$1 pattern=$2 got=0 out
  shift 3
  out=$("$script" "$@" 2>&1) || got=$?
  if [ "$got" != "$want" ] || ! grep -q -- "$pattern" <<< "$out"; then
    echo "FAIL: split-diff $* exited $got (want $want), output lacks '$pattern':"
    echo "$out" | sed 's/^/    /'
    failures=$((failures + 1))
  else
    echo "ok: $* -> $want, '$pattern'"
  fi
}

# Infrastructure and Notifications' adapters may change freely.
write src/Modules/Pledges/Pledges.Infrastructure/Adapter.cs 'class Adapter { int x; }'
write src/Modules/Notifications/SesEmailSender.cs 'class Ses { int x; }'
write src/Modules/Notifications/expectations.json '{}'
commit 'feat(s4-l02): adapters' infra
expect 0 'none: no Domain or Application file changed' -- base infra

# A Release 1 fix: forbidden with no entry, release-1-fix with its ticket's entry.
write src/Modules/Pledges/Pledges.Domain/Pledge.cs 'class Pledge { int y; }'
write src/Modules/Notifications/Handlers.cs 'class Handlers { int y; }'
commit 'fix: CQA-210: the backer list per pledge' fix
expect 1 'FORBIDDEN .*Pledge.cs  (no entry lists it)' -- base fix
expect 1 'path: src/Modules/Notifications/Handlers.cs' -- base fix
cat > allowed.yaml <<'YAML'
# a comment
allowed:
  - class: release-1-fix
    id: CQA-210
    reason: "a Release 1 bug: one: two"
    paths:
      - src/Modules/Pledges/Pledges.Domain/Pledge.cs
      - src/Modules/Notifications/Handlers.cs
YAML
expect 0 '2 changed: 0 permitted, 0 boundary-correction, 2 release-1-fix, 0 forbidden' -- --allow allowed.yaml base fix
expect 0 'a Release 1 bug: one: two' -- --allow allowed.yaml base fix

# The wrong change kind does not match.
printf 'allowed:\n  - class: release-1-fix\n    id: CQA-210\n    change: added\n    reason: r\n    path: src/Modules/Pledges/Pledges.Domain/Pledge.cs\n' > added.yaml
expect 1 'Pledge.cs  (no entry lists it)' -- --allow added.yaml base fix

# A later commit to a listed file that names no listed ticket is forbidden, though an entry lists the file.
write src/Modules/Pledges/Pledges.Domain/Pledge.cs 'class Pledge { int y; int leak; }'
commit 'feat(s4-l05): a host of its own' leak
expect 1 'names none of CQA-210' -- --allow allowed.yaml base leak

# A boundary correction is counted and named on its own; a permitted change is counted apart.
write src/Modules/Pledges/Pledges.Application/CancelFunding.cs 'class CancelFunding {}'
write src/Modules/Pledges/Pledges.Application/PlacePledge.cs 'class PlacePledge { int z; }'
commit 'feat(s5-l05): the cancel goes to its owner' boundary
cat > boundary.yaml <<'YAML'
allowed:
  - class: boundary-correction
    id: s5-l05, CQA-1770
    change: added
    reason: the LIVE cancel moves to the module that owns the funding window
    path: src/Modules/Pledges/Pledges.Application/CancelFunding.cs
  - class: permitted
    id: s5-l05
    reason: a port
    path: src/Modules/Pledges/Pledges.Application/PlacePledge.cs
  - class: permitted
    id: s4-l06
    reason: not used here
    path: src/Modules/Campaigns/*
YAML
expect 0 '2 changed: 1 permitted, 1 boundary-correction, 0 release-1-fix, 0 forbidden' -- --allow boundary.yaml leak boundary
expect 0 'the split forced a domain change: 1' -- --allow boundary.yaml leak boundary
expect 0 's5-l05, CQA-1770: the LIVE cancel moves' -- --allow boundary.yaml leak boundary
expect 0 'does not use' -- --allow boundary.yaml leak boundary

# A service's move into src/Services/<Name>/ is renames only: nothing changed. A file the move renamed
# and a fix changed is judged at its new path, by an entry that names its old one, and the move's own
# commit need not name the entry's ticket. (Files of some lines, so that git sees the renames.)
write src/Modules/Pledges/Pledges.Domain/Pledge.cs "$(printf 'class Pledge\n{\n    int y;\n    int leak;\n    int a;\n    int b;\n    int c;\n    int d;\n}')"
write src/Modules/Pledges/Pledges.Domain/Backers.cs "$(printf 'class Backers\n{\n    int a;\n    int b;\n    int c;\n    int d;\n}')"
commit 'fix: CQA-210: the backer list per pledge, in full' prep
mkdir -p src/Services/Notifications
git mv src/Modules/Pledges src/Services/Pledges
git mv src/Modules/Notifications src/Services/Notifications/Notifications
commit 'refactor: Pledges and Notifications move into src/Services' move
expect 0 'none: no Domain or Application file changed' -- prep move
write src/Services/Pledges/Pledges.Domain/Pledge.cs "$(printf 'class Pledge\n{\n    int y;\n    int leak;\n    int a;\n    int b;\n    int c;\n    int d;\n    int e;\n}')"
commit 'fix: CQA-210: the backer list per pledge, again' movedfix
expect 0 'release-1-fix *modified .*src/Services/Pledges/Pledges.Domain/Pledge.cs' -- --allow allowed.yaml prep movedfix
expect 1 'FORBIDDEN .*modified .*src/Services/Pledges/Pledges.Domain/Pledge.cs' -- prep movedfix
# A file a fix added and a move then moved is judged by the entry that names where it was added.
printf 'allowed:\n  - class: release-1-fix\n    id: CQA-210\n    change: added\n    reason: r\n    path: src/Modules/Pledges/Pledges.Domain/Backers.cs\n' > added-then-moved.yaml
expect 1 'release-1-fix *added .*src/Services/Pledges/Pledges.Domain/Backers.cs' -- --allow added-then-moved.yaml boundary movedfix

# Malformed allow-lists are refused.
bad() { printf 'allowed:\n  - class: %s\n    id: %s\n    reason: r\n    path: x\n' "$1" "$2" > bad.yaml; }
bad permitted TICKET-1
expect 2 'id must name tickets' -- --allow bad.yaml base fix
bad boundary-correction CQA-1770
expect 2 'names the lesson' -- --allow bad.yaml base fix
bad release-1-fix 'CQA-1, CQA-2'
expect 2 'one entry per ticket' -- --allow bad.yaml base fix
bad split s4-l06
expect 2 'class must be' -- --allow bad.yaml base fix
printf 'allowed:\n  - class: permitted\n    id: s4-l06\n    path: x\n' > bad.yaml
expect 2 'reason is empty' -- --allow bad.yaml base fix
printf 'allowed:\n  - class: permitted\n    id: s4-l06\n    reason: r\n' > bad.yaml
expect 2 'has no path' -- --allow bad.yaml base fix
expect 2 'unknown ref' -- base no-such-ref

[ "$failures" -eq 0 ] || { echo "$failures failed"; exit 1; }
echo "all split-diff tests passed"
