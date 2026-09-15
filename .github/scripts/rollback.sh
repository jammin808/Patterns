#!/usr/bin/env bash
# The rollback workflow's hands: puts the tree of one version back on a branch as a new commit,
# pushes it and starts the build on it. Nothing is rewritten. Inputs by environment:
#   TO        the version — a tag (round-57, show-2026-09-19), a commit or a branch   (required)
#   BRANCH    the branch it lands on; blank: FROM_REF; a name that does not exist is made from FROM_REF
#   WHAT      only these paths, space-separated; blank: everything
#   FROM_REF  the branch the run was started from
#   ACTOR     who started it (for the commit message)
#   DRY_RUN=1 commit, but neither push nor start a build (the local test runs it so)
# The workflows (.github/) stay as they are on the branch unless WHAT names them, so the branch
# always knows how to build itself. Run from the repository root with origin fetched in full.
set -euo pipefail

to_ref="${TO:?TO — the version to put back}"
from_ref="${FROM_REF:?FROM_REF — the branch the run was started from}"
branch="${BRANCH:-$from_ref}"
actor="${ACTOR:-someone}"
summary="${GITHUB_STEP_SUMMARY:-/dev/null}"

resolve() {
  git rev-parse --verify --quiet "refs/tags/$1^{commit}" \
  || git rev-parse --verify --quiet "refs/remotes/origin/$1^{commit}" \
  || git rev-parse --verify --quiet "$1^{commit}" 2>/dev/null
}
to="$(resolve "$to_ref")" || { echo "::error::'$to_ref' is not a tag, a commit or a branch of this repository."; exit 1; }

# Every path asked for has to exist in that version; a path that does not is a deletion, which is
# a decision for a person and a plain commit, not a roll-back.
for p in ${WHAT:-}; do
  git cat-file -e "$to:$p" 2>/dev/null || { echo "::error::'$p' does not exist at $to_ref."; exit 1; }
done

# The branch it lands on.
if git rev-parse --verify --quiet "refs/remotes/origin/$branch" >/dev/null; then
  git checkout -q -B "$branch" "origin/$branch"
  made=""
else
  git checkout -q -B "$branch" "origin/$from_ref"
  made=" — a new branch from $from_ref"
fi
was="$(git rev-parse HEAD)"

# The version's tree, into the index and the working tree.
if [ -z "${WHAT:-}" ]; then
  git read-tree -u --reset "$to"
  if git cat-file -e "$was:.github" 2>/dev/null; then
    git rm -r -q --cached --ignore-unmatch -- .github
    rm -rf .github
    git checkout -q "$was" -- .github
  fi
else
  # shellcheck disable=SC2086
  git rm -r -q --cached --ignore-unmatch -- $WHAT
  for p in $WHAT; do rm -rf -- "$p"; done
  # shellcheck disable=SC2086
  git checkout -q "$to" -- $WHAT
fi
git add -A

if git diff --cached --quiet; then
  echo "\`$branch\` already has ${WHAT:-everything} as it is at \`$to_ref\`; nothing to do." | tee -a "$summary"
  exit 0
fi

short="$(git rev-parse --short "$to")"
when="$(git log -1 --format=%cs "$to")"
subject="$(git log -1 --format=%s "$to" | cut -c1-80)"
scope="${WHAT:+$WHAT }"
git -c user.name="github-actions[bot]" -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
  commit -q -F - <<MSG
Roll back ${scope}to $to_ref ($short, $when)

${WHAT:+The paths $WHAT}${WHAT:-The tree}, put back as at $to_ref — "$subject" — on $branch by the
rollback workflow, started by $actor. The versions between are still in the history: rolling
back to $(git rev-parse --short "$was") puts them back.
MSG
new="$(git rev-parse --short HEAD)"

{
  echo "Rolled ${scope}back to \`$to_ref\` ($short, $when) on \`$branch\`$made: commit \`$new\`."
  echo
  echo "- Undo: run this workflow again with \`to\` = \`$(git rev-parse --short "$was")\` and \`branch\` = \`$branch\`."
  echo "- The build of the rolled-back branch runs next on the Actions page."
} | tee -a "$summary"

if [ "${DRY_RUN:-0}" = "1" ]; then
  echo "DRY_RUN: not pushed, no build started."
  exit 0
fi
git push -q origin "HEAD:refs/heads/$branch"
gh workflow run build.yml --ref "$branch" \
  || echo "::warning::The build could not be started on $branch (its build.yml may predate the workflow_dispatch trigger); push a commit to build it."
