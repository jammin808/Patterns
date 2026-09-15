#!/usr/bin/env bash
# The rollback script under test (round 64): a scratch bare remote and a clone with three
# versions, then every case the workflow can be asked for, with DRY_RUN=1 so nothing is pushed —
# the whole tree by tag, one path, a bad version, a missing path, a name that is not a branch,
# nothing to do, a new branch, the workflows preserved, and the undo. Run from anywhere:
#   bash .github/scripts/test-rollback.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
script="$here/rollback.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cd "$work"
git init -q --bare remote.git
git clone -q remote.git clone
cd clone
git config user.name t; git config user.email t@t
git checkout -q -b main

mkdir -p .github/workflows docs src
echo "name: build v1" > .github/workflows/build.yml
echo "docs v1" > docs/PLAN.md
echo "src v1" > src/a.txt
git add -A && git commit -q -m "v1" && git tag round-1
echo "name: build v2" > .github/workflows/build.yml
echo "docs v2" > docs/PLAN.md
echo "src v2" > src/a.txt
git add -A && git commit -q -m "v2" && git tag round-2
echo "name: build v3" > .github/workflows/build.yml
echo "docs v3" > docs/PLAN.md
echo "src v3" > src/a.txt
git add -A && git commit -q -m "v3"
git push -q origin main --tags
v3="$(git rev-parse --short HEAD)"

pass=0
ok()   { pass=$((pass+1)); echo "ok   - $1"; }
fail() { echo "FAIL - $1"; exit 1; }
run()  { (export FROM_REF=main ACTOR=test DRY_RUN=1 GITHUB_STEP_SUMMARY=/dev/null; "$@" bash "$script" >"$work/out.txt" 2>&1); }

# 1. The whole tree by tag: the sources and the docs go back, the workflows stay as they are.
run env TO=round-1 || fail "whole tree"
[ "$(cat src/a.txt)" = "src v1" ] && [ "$(cat docs/PLAN.md)" = "docs v1" ] || fail "whole tree: the files"
[ "$(cat .github/workflows/build.yml)" = "name: build v3" ] || fail "whole tree: the workflows must stay current"
git log -1 --format=%s | grep -q "Roll back to round-1" || fail "whole tree: the commit"
ok "the whole tree by tag, workflows preserved"

# 2. Undo: roll back to the commit that was there.
run env TO="$v3" || fail "undo"
[ "$(cat src/a.txt)" = "src v3" ] || fail "undo: the files"
ok "undo by commit"

# 3. One path only.
run env TO=round-2 WHAT="docs" || fail "one path"
[ "$(cat docs/PLAN.md)" = "docs v2" ] && [ "$(cat src/a.txt)" = "src v3" ] || fail "one path: only docs"
ok "one path"

# 4. Paths separated by commas and newlines, handled one by one.
git checkout -q main && git reset -q --hard origin/main
run env TO=round-1 WHAT=$'docs,\nsrc' || fail "two paths"
[ "$(cat docs/PLAN.md)" = "docs v1" ] && [ "$(cat src/a.txt)" = "src v1" ] || fail "two paths: both"
ok "paths by comma and newline"

# 5. A bad version is refused before anything moves.
git checkout -q main && git reset -q --hard origin/main
if run env TO=nowhere; then fail "bad version accepted"; fi
grep -q "not a tag, a commit or a branch" "$work/out.txt" || fail "bad version: the words"
ok "a bad version is refused"

# 6. A missing path is refused.
if run env TO=round-1 WHAT="nothing-here"; then fail "missing path accepted"; fi
grep -q "does not exist" "$work/out.txt" || fail "missing path: the words"
ok "a missing path is refused"

# 7. A name git would not take as a branch is refused, and so is an option in a path's place.
if run env TO=round-1 BRANCH="bad name"; then fail "bad branch accepted"; fi
grep -q "not a branch name" "$work/out.txt" || fail "bad branch: the words"
if run env TO=round-1 WHAT="--force"; then fail "option as a path accepted"; fi
ok "a bad branch name and an option are refused"

# 8. Nothing to do: the branch already has that version's docs (the first roll-back pushed, as
# the workflow's would have; the dry run leaves that to the test).
run env TO=round-2 WHAT=docs || fail "no-op first"
git push -q origin HEAD:main 2>/dev/null
run env TO=round-2 WHAT=docs || fail "no-op second"
grep -q "nothing to do" "$work/out.txt" || fail "no-op: the words"
ok "nothing to do is said"

# 9. A new branch is made from the run's branch and the preview names it.
git checkout -q main && git reset -q --hard origin/main
run env TO=round-1 BRANCH=look-first || fail "new branch"
[ "$(git rev-parse --abbrev-ref HEAD)" = "look-first" ] || fail "new branch: the checkout"
grep -q "a new branch from main" "$work/out.txt" || fail "new branch: the words"
grep -q "Roll back preview" "$work/out.txt" || fail "new branch: the preview"
ok "a new branch, with the preview"

echo "rollback script: $pass cases pass"
