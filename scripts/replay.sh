#!/usr/bin/env bash
# Replays the later lessons after an earlier lesson has been fixed.
#
#   scripts/replay.sh <lesson-tag> <fixed-commit> [--apply]
#
# <lesson-tag>    the tag of the lesson that was fixed, for example s2-l04
# <fixed-commit>  the commit that is the corrected end of that lesson (usually a branch
#                 made from <lesson-tag> with the fix committed on it)
#
# Every commit after <lesson-tag> on main is cherry-picked onto <fixed-commit>, in order.
# Each lesson tag on those commits moves to the commit that replaces it, <lesson-tag> moves
# to <fixed-commit>, and main moves to the last replayed commit.
#
# Without --apply it only prints what it would do. With --apply it does the work on a
# temporary branch first and moves no tag and not main unless every commit applies. On a
# conflict it stops and leaves the temporary branch for you to inspect.
# Pushing is left to you: git push --force origin main 's*-l*'
set -euo pipefail

usage() { echo "usage: $0 <lesson-tag> <fixed-commit> [--apply]" >&2; exit 2; }
[[ $# -ge 2 && $# -le 3 ]] || usage
lesson_tag=$1
fixed=$(git rev-parse --verify "$2^{commit}")
apply=false
if [[ $# -eq 3 ]]; then
  [[ $3 == --apply ]] || usage
  apply=true
fi

old=$(git rev-parse --verify "refs/tags/$lesson_tag^{commit}")
git merge-base --is-ancestor "$old" main || { echo "$lesson_tag is not on main" >&2; exit 1; }
git merge-base --is-ancestor "$old" "$fixed" \
  || { echo "the fixed commit must be built on $lesson_tag" >&2; exit 1; }
[[ -z $(git status --porcelain) ]] || { echo "the working tree is not clean" >&2; exit 1; }

commits=$(git rev-list --reverse "$old..main")
lesson_tags_on() { git tag --points-at "$1" --list 's*-l*'; }

echo "$lesson_tag: $(git rev-parse --short "$old") -> $(git rev-parse --short "$fixed")"
for c in $commits; do
  echo "replay $(git log -1 --format='%h %s' "$c")$(for t in $(lesson_tags_on "$c"); do printf ' [%s]' "$t"; done)"
done
if [[ -z $commits ]]; then echo "no later commits; only $lesson_tag moves"; fi
$apply || { echo "dry run: nothing changed. Add --apply to replay."; exit 0; }

start_ref=$(git symbolic-ref --quiet --short HEAD || git rev-parse HEAD)
work="replay/$lesson_tag-$(date +%Y%m%d%H%M%S)"
git switch --quiet -c "$work" "$fixed"

moves=$(mktemp)
trap 'rm -f "$moves"' EXIT
echo "$lesson_tag $fixed" >>"$moves"
for c in $commits; do
  if ! git cherry-pick --allow-empty --keep-redundant-commits "$c" >/dev/null; then
    echo "conflict while replaying $(git log -1 --format='%h %s' "$c")." >&2
    echo "Resolve it on branch $work, or run: git cherry-pick --abort; git switch $start_ref" >&2
    exit 1
  fi
  for t in $(lesson_tags_on "$c"); do echo "$t $(git rev-parse HEAD)" >>"$moves"; done
done

while read -r tag commit; do
  git tag -f "$tag" "$commit" >/dev/null
  echo "moved $tag -> $(git rev-parse --short "$commit")"
done <"$moves"
new_main=$(git rev-parse HEAD)
if [[ $start_ref == main ]]; then
  git switch --quiet main
  git reset --quiet --hard "$new_main"
else
  git branch -f main "$new_main"
  git switch --quiet "$start_ref" 2>/dev/null || git switch --quiet --detach "$start_ref"
fi
git branch -D "$work" >/dev/null
echo "main -> $(git rev-parse --short main). Rebuild and test every tag, then push."
