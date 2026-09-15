#!/usr/bin/env bash
# The rounds' tags, made and pushed: one annotated tag on the last commit of each round from 15
# (the first in the repository's history), its message the changelog's line for the round. The
# table is the history read into rounds (CHANGELOG.md, docs/PLAN.md §79.1); the newest round in
# CHANGELOG.md is tagged at HEAD, which is why this runs on that round's last commit, and each
# round after it is tagged the same way when it closes. Run from the repository root by someone
# whose push reaches the tags:
#
#   bash .github/scripts/tag-rounds.sh            makes the tags that are missing and pushes them
#   bash .github/scripts/tag-rounds.sh --no-push  only makes them
#
# A tag that exists is left as it is; one that exists at a different commit is reported and
# left. A tag push runs the workflow file as it is at the tagged commit: the historical build.yml
# files trigger on branches only, so the old rounds start no build, and the newest round's tag
# builds and releases (the release job in build.yml). Idempotent: run it again after a round
# closes and only the new tag is made and pushed.
set -euo pipefail

push=1
case "${1:-}" in
  --no-push) push=0 ;;
  "") ;;
  *) echo "usage: tag-rounds.sh [--no-push]" >&2; exit 2 ;;
esac

# round  the last commit of the round
rounds='
  15 b746a8dd26e6708c228611267045943121560d03
  16 1063192f5a8004463779ba052e509b2238cc39cb
  17 69fb75b3d55f6569f31f719b2a3d5641e00a8ba6
  18 6eb592b35dbc8a699054edac2358ca4fbb779097
  19 eeca7065e048e92e1d40cbcc8cf5b88e3bf4d492
  20 a7b8af589703b7038f052369da82721364a8a432
  21 66e77f82c5e2fc62a2bb0d32d7fde96e6f4fe7ac
  22 63d9ae1919bed5322aa594870a5bdae7ada679ba
  23 0ef94ef43385d00725ce9a37229d241436fb4c92
  24 f116d5c45d26f24125e89ea5749987ee317a1ee4
  25 57f6d36b15c800c43ca3f037062a090eb8099157
  26 0289deb48aa78d74892d9da8a18ce1cfb5d3fe3a
  27 a4137ad2c3f5edb88385d79b473537afa775b366
  28 21c6690e7093773722d149f17020768e5d68d5eb
  29 40d4f9e4668045ad8e9033f7b98e845d15eda083
  30 8e3bdf4a86fed1847ad0b45e9d122bd16ce26037
  31 6569ed4ce0f33b5012f1bea47d369d15c04e032b
  32 4d44649da9cec0f3471d150e9b0ef1399fa8fb22
  33 150fa61a1b26fbacaa57e735e102f1963844c738
  34 e491a8abb5faa054cff9b88b483d77e0ff1a07e4
  35 acdd5789a9781e7bb7a694f54b889c9be2190569
  36 c956c96bccec66acea98b666310178a473ca03e3
  37 29028405514af77b026aa8d016dbc792c120b667
  38 e737136cf5c8893cc3043cd5e407264d4d2b3ec8
  39 94e3d9455213cf2c7925db21239110cccd2076e4
  40 218454801be7856d971b0ca2d310419be57a4a5f
  41 be7f7ad70b9db6a2eb08711b1a4a21787136c882
  42 bce51bb207e5518e300513fcfd4ce3f76572d5e0
  43 90d6a9bd869d1ba9094e24e2000bdeb5c5b6ffae
  44 95c0fc7e9d8d40d5755ac3bbf9f468e7e55a7920
  45 7dd813eff61852cfd40996f09d5616f041d3b821
  46 87d35d0c3e7acb0efc490059d8bb1f441317becb
  47 73923c220873b69603dedd65cf5d3d9401b6a75e
  48 6e9790045a24d27d9958fb4d8512aee7a985db09
  49 4af562986f762faf6ad9a4d9a39975e3832851ee
  50 c977e3d70338a0bcec865b823bfbf23b7b37dbf3
  51 a133ed0dd1bc2f7ff3ed6cdffd07157ab130f35e
  52 43cffa0f0edb8ea1fa2c37d178f24d9b9caf0e38
  53 388cd9a1bf5155a9f68107c66423f3d99d075f69
  54 69fc8deaebc0abb40d796daeb0a9aa5cd9fdd154
  55 2982ee101898d1bc860674d95f7f19de8e1f2ca7
  56 95148d3393e5a4e3c112447f7ddb8ee55ee06c18
  57 ee7f25456856fdb312706b83132339ab3718c5c0
  58 ae9f90f66eda461265d3a850bbbefc5151c861e7
  59 06d5d020f52b735890fa0f0e98b174c537bedcc8
  60 de4fc6663048ae47a86c7ece7a84413a355ea618
'

[ -f CHANGELOG.md ] || { echo "run this from the repository root (no CHANGELOG.md here)" >&2; exit 1; }
git fetch -q --tags origin 2>/dev/null || echo "note: the remote's tags could not be fetched; working from the local ones"

newest="$(grep -m1 -oE '^## Round [0-9]+ ' CHANGELOG.md | grep -oE '[0-9]+')"
last_in_table="$(printf '%s\n' "$rounds" | awk 'NF { n = $1 } END { print n }')"

heading() { grep -m1 "^## Round $1 — " CHANGELOG.md | sed 's/^## //'; }

make_tag() {  # round commit
  local n="$1" commit="$2" tag="round-$1" head
  head="$(heading "$n")"
  [ -n "$head" ] || { echo "CHANGELOG.md has no entry for round $n" >&2; return 1; }
  if existing="$(git rev-parse --verify --quiet "refs/tags/$tag^{commit}")"; then
    if [ "$existing" = "$commit" ]; then echo "  $tag exists"; else echo "  $tag exists at ${existing:0:7}, not at ${commit:0:7} — left as it is"; fi
    return 0
  fi
  git tag -a "$tag" "$commit" -m "$head" -m "The last commit of round $n. CHANGELOG.md has the entry; docs/PLAN.md the design; docs/REVIEW.md what was found."
  echo "  $tag made at ${commit:0:7} — $head"
}

echo "The rounds in the table:"
while read -r n commit; do
  [ -n "$n" ] || continue
  make_tag "$n" "$commit"
done <<< "$rounds"

# The rounds after the table: each must already be a tag (made when it closed), except the
# newest, which is HEAD — the round's last commit, where this runs.
n=$((last_in_table + 1))
while [ "$n" -lt "$newest" ]; do
  git rev-parse --verify --quiet "refs/tags/round-$n^{commit}" >/dev/null \
    || { echo "round $n is neither in the table nor a tag: add its last commit to the table" >&2; exit 1; }
  echo "  round-$n exists"
  n=$((n + 1))
done
echo "The newest round, at HEAD:"
make_tag "$newest" "$(git rev-parse HEAD)"

[ "$push" = 1 ] || { echo "Not pushed (--no-push)."; exit 0; }

remote="$(git ls-remote --tags origin 'round-*' 2>/dev/null | grep -oE 'refs/tags/round-[0-9]+$' | sed 's|refs/tags/||' || true)"
missing=()
for t in $(git tag -l 'round-*' | sort -t- -k2,2n); do
  grep -qx "$t" <<< "$remote" || missing+=("refs/tags/$t")
done
if [ "${#missing[@]}" = 0 ]; then echo "Every tag is on the remote already."; exit 0; fi
echo "Pushing ${#missing[@]} tag(s): ${missing[*]##refs/tags/}"
git push origin "${missing[@]}"
