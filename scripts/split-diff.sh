#!/usr/bin/env bash
# The course's claim, checked on the code: when a module leaves the monolith, its Domain and
# Application code does not change; only Infrastructure, hosts, contracts wiring and deployment do.
#
#   scripts/split-diff.sh [--allow FILE] [--layers] <fromRef> <toRef>
#
# Diffs the Domain and Application layers of every module between two refs (tags, branches or
# commits) and puts each changed file in a class:
#   none                 nothing in those layers changed;
#   permitted            the split needed it and no rule changed (a project reference, a port a
#                        remote adapter implements);
#   boundary-correction  a module boundary drawn wrong in the monolith and corrected inside it
#                        before the extraction: the one kind of change that says the split forced
#                        a domain change, counted and named on its own;
#   release-1-fix        a Release 1 bug fix on the code track, a bug in one process already, not
#                        driven by the split;
#   forbidden            anything the allow-list (split-allowed.yaml) does not cover.
# A file is covered when an entry lists it, its change matches, and every commit between the two
# refs that touched it names, in its subject, a ticket or lesson of an entry that lists it. So an
# entry covers the work it was written for, not a later commit to the same file.
#
# Watched: */*.Domain/** and */*.Application/** under src/Modules and src/Services (a service's own
# folder since s5-l05). Renames are detected: a pure move counts as no change. Notifications is one
# project with no layers (poly-pattern-tiers), so all of it is watched except its adapters and
# wiring: Migrations/, Properties/, *DbContext.cs, *Module.cs, Ses*.cs and *.json.
#
# --layers also prints files and lines changed per layer for the whole of src/, infra/ and tests/.
#
# Exit status: 0 when nothing is forbidden, 1 when something is, 2 on bad usage or a bad allow-list.
#
# Allow-list format, a restricted YAML: one list under "allowed:", "key: value" pairs, and a path
# list under "paths:".
#   allowed:
#     - class: release-1-fix        # permitted | boundary-correction | release-1-fix
#       id: CQA-210                 # release-1-fix: exactly one ticket. boundary-correction: a lesson
#                                   # (s5-l05), tickets too if any. permitted: tickets or lessons.
#       reason: Notifications keeps who backs a campaign per pledge.
#       change: any                 # added | modified | deleted | any (the default)
#       paths:                      # or a single "path: ..."
#         - src/Services/Notifications/Notifications/Notifier.cs
#         - src/Services/Notifications/Notifications/Handlers.cs
# A path is exact or a shell pattern, in which * also matches "/". Keep entries narrow.
set -euo pipefail

usage() {
  sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//' >&2
  exit 2
}

root=$(git rev-parse --show-toplevel)
allow="$root/split-allowed.yaml"
layers=0
while [ $# -gt 0 ]; do
  case $1 in
    --allow) allow=$2; shift 2 ;;
    --layers) layers=1; shift ;;
    -h|--help) usage ;;
    --) shift; break ;;
    -*) echo "split-diff: unknown option $1" >&2; usage ;;
    *) break ;;
  esac
done
[ $# -eq 2 ] || usage
from=$1
to=$2
git rev-parse --verify --quiet "$from^{commit}" >/dev/null || { echo "split-diff: unknown ref $from" >&2; exit 2; }
git rev-parse --verify --quiet "$to^{commit}" >/dev/null || { echo "split-diff: unknown ref $to" >&2; exit 2; }
[ -f "$allow" ] || { echo "split-diff: allow-list $allow not found" >&2; exit 2; }

watched=(
  ':(glob)src/Modules/*/*.Domain/**'
  ':(glob)src/Modules/*/*.Application/**'
  ':(glob)src/Services/*/*.Domain/**'
  ':(glob)src/Services/*/*.Application/**'
  ':(glob)src/Modules/Notifications/**'
  ':(exclude,glob)src/Modules/Notifications/Migrations/**'
  ':(exclude,glob)src/Modules/Notifications/Properties/**'
  ':(exclude,glob)src/Modules/Notifications/**/*DbContext.cs'
  ':(exclude,glob)src/Modules/Notifications/**/*Module.cs'
  ':(exclude,glob)src/Modules/Notifications/**/Ses*.cs'
  ':(exclude,glob)src/Modules/Notifications/**/*.json'
  ':(glob)src/Services/Notifications/Notifications/**'
  ':(exclude,glob)src/Services/Notifications/Notifications/Migrations/**'
  ':(exclude,glob)src/Services/Notifications/Notifications/Properties/**'
  ':(exclude,glob)src/Services/Notifications/Notifications/**/*DbContext.cs'
  ':(exclude,glob)src/Services/Notifications/Notifications/**/*Module.cs'
  ':(exclude,glob)src/Services/Notifications/Notifications/**/Ses*.cs'
  ':(exclude,glob)src/Services/Notifications/Notifications/**/*.json'
  ':(exclude,glob)**/bin/**'
  ':(exclude,glob)**/obj/**'
)

# The allow-list as one tab-separated line per entry and path: entry number, class, change, ids, reason, path.
entries=$(awk -v file="$allow" -v q="'" '
  function bad(msg) { printf "split-diff: %s: line %d: %s\n", file, NR, msg > "/dev/stderr"; failed = 1; exit 2 }
  function value(line,   first) {
    sub(/^[^:]*:[ \t]*/, "", line); sub(/[ \t]+$/, "", line)
    first = substr(line, 1, 1)
    if (length(line) > 1 && (first == "\"" || first == q) && substr(line, length(line)) == first) line = substr(line, 2, length(line) - 2)
    gsub(/\t/, " ", line)
    return line
  }
  function item(line,   first) {
    sub(/^[ \t]*-[ \t]*/, "", line); sub(/[ \t]+$/, "", line)
    first = substr(line, 1, 1)
    if (length(line) > 1 && (first == "\"" || first == q) && substr(line, length(line)) == first) line = substr(line, 2, length(line) - 2)
    return line
  }
  function flush(   i, n, ids, lesson) {
    if (!open) return
    open = 0
    if (np == 0) bad("entry " entry " has no path")
    if (class != "permitted" && class != "boundary-correction" && class != "release-1-fix")
      bad(paths[1] ": class must be permitted, boundary-correction or release-1-fix")
    if (change == "") change = "any"
    if (change !~ /^(added|modified|deleted|any)$/) bad(paths[1] ": change must be added, modified, deleted or any")
    if (id !~ /^(CQA-[0-9]+|s[0-9]+-[al][0-9]+)(, *(CQA-[0-9]+|s[0-9]+-[al][0-9]+))*$/)
      bad(paths[1] ": id must name tickets (CQA-123) or lessons (s5-l05), separated by commas")
    n = split(id, ids, /, */)
    lesson = 0
    for (i = 1; i <= n; i++) if (ids[i] ~ /^s/) lesson = 1
    if (class == "boundary-correction" && !lesson) bad(paths[1] ": a boundary-correction names the lesson that makes it")
    if (class == "release-1-fix" && (n != 1 || id !~ /^CQA-/)) bad(paths[1] ": a release-1-fix entry names exactly one ticket; one entry per ticket")
    if (reason == "") bad(paths[1] ": reason is empty")
    for (i = 1; i <= np; i++) printf "%d\t%s\t%s\t%s\t%s\t%s\n", entry, class, change, id, reason, paths[i]
  }
  /^[ \t]*(#|$)/ { next }
  /^allowed:[ \t]*(\[\])?[ \t]*$/ { seen = 1; next }
  {
    if (!seen) bad("expected \"allowed:\" first")
    match($0, /^[ \t]*/); indent = RLENGTH
  }
  /^[ \t]*-[ \t]/ && inpaths && indent > top { np++; paths[np] = item($0); next }
  /^[ \t]*-[ \t]/ {
    if (top == "") top = indent
    if (indent != top) bad("a list item where an entry was expected")
    flush(); open = 1; entry++; np = 0; class = change = id = reason = ""; inpaths = 0
    sub(/-[ \t]+/, " ")
  }
  {
    if (!open) bad("expected a list of entries")
    key = $0; sub(/^[ \t]*/, "", key); sub(/:.*/, "", key)
    inpaths = 0
    if (key == "path") { np++; paths[np] = value($0) }
    else if (key == "paths") { if (value($0) != "") bad("paths: takes a list, one \"- path\" per line"); inpaths = 1 }
    else if (key == "class") class = value($0)
    else if (key == "change") change = value($0)
    else if (key == "id") id = value($0)
    else if (key == "reason") reason = value($0)
    else bad("unknown key " key)
  }
  END { if (failed) exit 2; flush() }
' "$allow") || exit 2

# Renames are detected (-M): a file a service's move only renamed (R100) changed nothing and is not
# counted; a renamed file that also changed is judged at its new path, and an entry may name either path.
changes=$(git diff -M --name-status "$from" "$to" -- "${watched[@]}")
numstat=$(git diff -M --numstat "$from" "$to" -- "${watched[@]}")

# The commits between the refs that changed the file (at its new or old path), leaving out a commit
# that only moved it: a service's move is renames only and changes nothing in the file.
changing_commits() {
  local sha subject
  git log --no-merges --format='%h%x09%s' "$from..$to" -- "$1" "$2" |
    while IFS=$'\t' read -r sha subject; do
      if git diff-tree --no-commit-id -M -r --name-status "$sha" -- "$1" "$2" | grep -qv '^R100'; then
        printf '%s\t%s\n' "$sha" "$subject"
      fi
    done
}

annotate=0
[ "${GITHUB_ACTIONS:-}" = "true" ] && annotate=1

count_permitted=0
count_boundary=0
count_fix=0
forbidden=0
used=" "
stubs=""
report=""
while IFS=$'\t' read -r status path renamed; do
  [ -n "${path:-}" ] || continue
  old=$path
  case $status in
    R100) continue ;;
    R*) path=$renamed; change=modified ;;
    A) change=added ;;
    D) change=deleted ;;
    *) change=modified ;;
  esac
  if [ "$old" != "$path" ]; then
    lines=$(git diff --numstat "$from:$old" "$to:$path" | awk -F'\t' '{ print "+" $1 " -" $2; exit }')
  else
    lines=$(printf '%s\n' "$numstat" | awk -F'\t' -v p="$path" '$3 == p { print "+" $1 " -" $2; exit }')
  fi

  # Every entry that lists this file for this kind of change.
  matched=""
  while IFS=$'\t' read -r e_no e_class e_change e_ids e_reason e_path; do
    [ -n "${e_path:-}" ] || continue
    # shellcheck disable=SC2053 # the entry's path is a pattern on purpose
    [[ $path == $e_path || $old == $e_path ]] || continue
    [ "$e_change" = any ] || [ "$e_change" = "$change" ] || continue
    matched="$matched$e_no	$e_class	$e_ids	$e_reason"$'\n'
  done <<< "$entries"

  why=""
  if [ -z "$matched" ]; then
    why="no entry lists it"
  else
    # Every commit that touched the file names a ticket or lesson of an entry that lists it.
    ids=$(printf '%s' "$matched" | cut -f3 | tr ',' '\n' | tr -d ' ' | sort -u | paste -sd'|' -)
    while IFS=$'\t' read -r sha subject; do
      [ -n "${sha:-}" ] || continue
      if ! printf '%s\n' "$subject" | grep -Eq "(^|[^A-Za-z0-9-])($ids)([^0-9]|\$)"; then
        why="commit $sha \"$subject\" names none of ${ids//|/, }"
        break
      fi
    done <<< "$(changing_commits "$path" "$old")"
  fi

  if [ -z "$why" ]; then
    classes=$(printf '%s' "$matched" | cut -f2 | sort -u | paste -sd'+' -)
    case $classes in
      *boundary-correction*) count_boundary=$((count_boundary + 1)); label=boundary-correction ;;
      *permitted*) count_permitted=$((count_permitted + 1)); label=permitted ;;
      *) count_fix=$((count_fix + 1)); label=release-1-fix ;;
    esac
    for n in $(printf '%s' "$matched" | cut -f1); do used="$used$n "; done
    detail=$(printf '%s' "$matched" | awk -F'\t' '{ printf "%s%s %s: %s", (NR > 1 ? "; " : ""), $2, $3, $4 }')
    report="$report$(printf '  %-19s %-8s %-10s %s  [%s]' "$label" "$change" "$lines" "$path" "$detail")"$'\n'
  else
    forbidden=$((forbidden + 1))
    report="$report$(printf '  %-19s %-8s %-10s %s  (%s)' FORBIDDEN "$change" "$lines" "$path" "$why")"$'\n'
    stubs="$stubs  - class: permitted | boundary-correction | release-1-fix"$'\n'"    id: CQA-? or s?-l??"$'\n'"    reason: ?"$'\n'"    path: $path"$'\n'
    if [ "$annotate" = 1 ]; then
      echo "::error file=$path::$path ($change between $from and $to) is a Domain or Application change split-allowed.yaml does not cover: $why"
    fi
  fi
done <<< "$changes"

total=$((forbidden + count_permitted + count_boundary + count_fix))
echo "split-diff $from..$to ($(git rev-parse --short "$from^{commit}")..$(git rev-parse --short "$to^{commit}")): Domain and Application of every module"
if [ "$total" -eq 0 ]; then
  echo "  none: no Domain or Application file changed"
else
  printf '%s' "$report"
fi
echo "result: $total changed: $count_permitted permitted, $count_boundary boundary-correction, $count_fix release-1-fix, $forbidden forbidden"

boundary=$(printf '%s\n' "$entries" | awk -F'\t' -v used="$used" '
  $2 == "boundary-correction" && index(used, " " $1 " ") && !seen[$1]++ { print "  " $4 ": " $5 }')
if [ -n "$boundary" ]; then
  echo "boundary corrections, the places where the split forced a domain change: $(printf '%s\n' "$boundary" | wc -l | tr -d ' ')"
  printf '%s\n' "$boundary"
fi

unused=$(printf '%s\n' "$entries" | awk -F'\t' -v used="$used" '
  NF && !index(used, " " $1 " ") && !seen[$1]++ { print "  " $2 " " $4 " (" $6 ")" }')
if [ -n "$unused" ]; then
  echo "allow-list entries this range does not use (not an error: they may belong to a later step):"
  printf '%s\n' "$unused"
fi

if [ "$layers" = 1 ]; then
  echo "files and lines changed per layer, $from..$to:"
  git diff --no-renames --numstat "$from" "$to" -- src infra tests events deploy | awk -F'\t' '
    {
      p = $3
      if (p ~ /\/bin\// || p ~ /\/obj\//) next
      if (p ~ /^src\/Modules\/[^\/]+\/[^\/]+\.Domain\//) l = "Domain"
      else if (p ~ /^src\/Modules\/[^\/]+\/[^\/]+\.Application\//) l = "Application"
      else if (p ~ /^src\/Modules\/[^\/]+\/[^\/]+\.Contracts\//) l = "Contracts"
      else if (p ~ /^src\/Modules\/.*\/Migrations\//) l = "Migrations"
      else if (p ~ /^src\/Modules\/[^\/]+\/[^\/]+\.Infrastructure\//) l = "Infrastructure"
      else if (p ~ /^src\/Modules\/Notifications\/(.*DbContext\.cs|.*Module\.cs|Ses.*\.cs|.*\.json|Properties\/.*)$/) l = "Notifications (adapters, wiring)"
      else if (p ~ /^src\/Modules\/Notifications\//) l = "Notifications (watched)"
      else if (p ~ /^src\/Modules\//) l = "Modules (other)"
      else if (p ~ /^src\/BuildingBlocks\//) l = "BuildingBlocks"
      else if (p ~ /^src\/(Host|Services|Gateway)\//) l = "Hosts and gateway"
      else if (p ~ /^src\/Messaging\//) l = "Messaging"
      else if (p ~ /^src\//) l = "src (other)"
      else if (p ~ /^infra\//) l = "Terraform"
      else if (p ~ /^tests\//) l = "Tests"
      else l = "Events and deploy files"
      files[l]++; add[l] += ($1 == "-" ? 0 : $1); del[l] += ($2 == "-" ? 0 : $2)
    }
    END {
      for (l in files) { any = 1; break }
      if (!any) { print "  nothing"; exit }
      for (l in files) printf "  %-30s %5d files  +%-7d -%d\n", l, files[l], add[l], del[l]
    }' | sort
fi

if [ "$forbidden" -gt 0 ]; then
  echo
  echo "Each forbidden change needs a design fix or an entry in $(basename "$allow"), and its commits must name the entry's ticket or lesson. A stub:"
  printf '%s' "$stubs"
  exit 1
fi
