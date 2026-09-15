#!/usr/bin/env bash
# The notes of a Release, from the changelog. For a round's tag (round-61) the round's entry;
# for any other tag (show-2026-09-19) the tag's own line — commit, date, the round it sits on —
# and that round's entry. Run from the repository root: release-notes.sh <tag> [CHANGELOG.md].
set -euo pipefail

tag="${1:?the tag}"
log="${2:-CHANGELOG.md}"

entry() {
  # The changelog entry of round $1: from its "## Round N" heading to the next "## " heading.
  awk -v n="$1" '
    /^## /  { if (p) exit; p = ($2 == "Round" && $3 == n) }
    p       { print }
  ' "$log"
}

case "$tag" in
  round-*)
    n="${tag#round-}"
    body="$(entry "$n")"
    [ -n "$body" ] || { echo "CHANGELOG.md has no entry for round $n" >&2; exit 1; }
    printf '%s\n' "$body"
    ;;
  *)
    commit="$(git rev-parse --verify --quiet "$tag^{commit}")" || { echo "'$tag' is not a tag or a commit" >&2; exit 1; }
    round="$(git describe --tags --match 'round-*' --abbrev=0 "$commit" 2>/dev/null || true)"
    printf 'Tagged `%s` at commit %s (%s)' "$tag" "$(git rev-parse --short "$commit")" "$(git log -1 --format=%cs "$commit")"
    if [ -n "$round" ]; then
      printf ', on `%s`.\n\n' "$round"
      entry "${round#round-}"
    else
      printf '.\n'
    fi
    ;;
esac
