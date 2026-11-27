#!/usr/bin/env bash
# Which tests can a change affect? Reads the files a git range changes and prints, on standard output, one of:
#   ALL     run the whole suite (shared code, the test helpers, project files, or a file this script does not know)
#   NONE    run no tests (the change is docs only)
#   a dotnet test filter naming the test classes to run
# and, on standard error, one line per changed file saying what it brings in. See CONTRIBUTING.md.
#
#   tools/test-scope.sh                      origin/master...HEAD, plus uncommitted and untracked files
#   tools/test-scope.sh --base <ref>         the same, measured from the merge-base of <ref> and HEAD, so a long-lived
#                                            branch keeps the scope of its own new work (same as <merge-base sha>...HEAD
#                                            plus your uncommitted and untracked files)
#   tools/test-scope.sh <range>              only that range, e.g. HEAD~1..HEAD or <base sha>...HEAD
#   tools/test-scope.sh --paths < files      the changed paths come from standard input, one a line (tests use it)
#   tools/test-scope.sh --run [range] [-- dotnet test arguments]   runs the chosen tests too
#
# Test classes are found by reading the test files (top-level "class X" or "record X"), so there is no list to keep.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

app=AuBtechReviewAgent
tests=AuBtechReviewAgent.Tests
run=0
from_stdin=0
range=""
base=origin/master
base_given=0
extra=()
while [ $# -gt 0 ]; do
  case $1 in
    --run) run=1 ;;
    --paths) from_stdin=1 ;;
    --base) [ $# -ge 2 ] || { echo "--base needs a ref" >&2; exit 2; }; base=$2; base_given=1; shift ;;
    --base=*) base=${1#--base=}; base_given=1 ;;
    --) shift; extra=("$@"); break ;;
    -h|--help) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) range=$1 ;;
  esac
  shift
done

if [ "$base_given" = 1 ] && [ -n "$range" ]; then echo "Give --base or a range, not both." >&2; exit 2; fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
: > "$work/selected"   # test files whose classes run
full=""                # the reason for the whole suite, once one is found
docs=0
code=0

if [ "$from_stdin" = 1 ]; then
  tr -d '\r' > "$work/changed"
elif [ -n "$range" ]; then
  git diff --name-only --no-renames "$range" > "$work/changed"
else
  # A range A...HEAD diffs against the merge-base already; this spells out what --base chooses.
  mergebase=$(git merge-base "$base" HEAD) || { echo "No common ancestor of $base and HEAD." >&2; exit 2; }
  { git diff --name-only --no-renames "$mergebase" HEAD
    git diff --name-only --no-renames HEAD
    git ls-files --others --exclude-standard; } > "$work/changed"
fi
sort -u -o "$work/changed" "$work/changed"
sed -i.bak '/^$/d' "$work/changed" && rm -f "$work/changed.bak"

# Every test source file (not bin/ or obj/).
find "$tests" \( -path "$tests/bin" -o -path "$tests/obj" \) -prune -o -name '*.cs' -print | sort > "$work/all"

# The top-level classes and records a C# file declares.
declared() { sed -nE 's/^(public |internal )?((sealed|static|abstract) )*(partial )?(class|record) ([A-Za-z_][A-Za-z0-9_]*).*/\6/p' "$@" 2>/dev/null | sort -u; }

# Test files that name any word listed in the file $1.
naming() { [ -s "$1" ] && xargs grep -lwF -f "$1" < "$work/all" || true; }

say() { printf '  %-60s %s\n' "$1" "$2" >&2; }
pick() { cat >> "$work/selected"; }
folder() { grep "^$tests/$1/" "$work/all" | pick || true; }

module() {
  local name=$1
  grep -E "^$tests/Modules/$name[^/]*\.cs$" "$work/all" | pick || true
  # Tests anywhere that name a type or component of this module.
  { find "$app/Modules/$name" -name '*.cs' -print0 | xargs -0 cat | declared
    find "$app/Modules/$name" -name '*.razor' -exec basename {} .razor \; ; } | sort -u > "$work/module-types"
  naming "$work/module-types" | pick
}

while IFS= read -r path; do
  case $path in
    *.md|*.MD|docs/*|LICENSE|NOTICE|CITATION.cff|.zenodo.json|.github/ISSUE_TEMPLATE/*)
      docs=1; say "$path" "docs: no tests" ;;
    "$tests"/Support/*|"$tests"/*.csproj|"$tests"/*.props)
      full="$path is shared by every test"; say "$path" "whole suite" ;;
    "$tests"/Security/*)
      code=1; say "$path" "security tests (always run)" ;;
    "$tests"/*.cs)
      code=1
      if [ ! -f "$path" ]; then full="$path was removed"; say "$path" "whole suite"; continue; fi
      echo "$path" | pick
      declared "$path" > "$work/types"
      naming "$work/types" | pick
      say "$path" "its own tests and the tests that use its classes" ;;
    "$app"/Llm/*|"$app"/Pipeline/*|"$app"/Verification/*|"$app"/Synthesis/*|"$app"/Report/*)
      full="$path is shared code"; say "$path" "whole suite (shared code)" ;;
    "$app"/Components/*|"$app"/Styles/*|"$app"/wwwroot/*|"$app"/package.json|"$app"/package-lock.json|"$app"/tailwind.config.js)
      code=1; folder Web; say "$path" "Web tests" ;;
    "$app"/Modules/*/*)
      code=1; name=${path#"$app"/Modules/}; name=${name%%/*}
      if [ -d "$app/Modules/$name" ]; then module "$name"; say "$path" "$name module tests"
      else full="the $name module was removed"; say "$path" "whole suite"; fi ;;
    "$app"/Api/*|"$app"/Evidence/*|"$app"/Mcp/*|"$app"/Runs/*|"$app"/Screening/*|"$app"/Search/*|"$app"/Sources/*|"$app"/Web/*)
      code=1; sub=${path#"$app"/}; sub=${sub%%/*}; folder "$sub"; say "$path" "$sub tests" ;;
    .github/*|tools/a11y/*|tools/ReleaseCheck/*|tools/ScreeningEval/*|deploy/*|Dockerfile|.dockerignore|.gitignore)
      code=1; say "$path" "no test of its own (its CI job or the build checks it)" ;;
    *)
      full="$path is not in the map"; say "$path" "whole suite (not in the map)" ;;
  esac
done < "$work/changed"

# Only files that hold tests matter for the filter.
grep -lE '\[(Fact|Theory)' $(cat "$work/all") | sort > "$work/testfiles"

if [ -n "$full" ]; then
  scope=ALL
  echo "Whole suite: $full." >&2
elif [ "$code" = 0 ]; then
  scope=NONE
  echo "No tests: the change is docs only." >&2
else
  # Always: the module kit and every test that runs a whole fake review, the file-length check, the attack set.
  { grep -E "/(ModuleKitTests|FileLengthTests)\.cs$" "$work/all"
    folder Security
    xargs grep -lE '\b(ModuleKit|FormalKit)\.' < "$work/all" || true
    if [ "$docs" = 1 ]; then grep -E "/DocPathsTests\.cs$" "$work/all"; fi
  } | pick
  sort -u "$work/selected" | comm -12 - "$work/testfiles" > "$work/run"
  declared $(cat "$work/run") > "$work/classes"
  declared $(cat "$work/testfiles") > "$work/every"
  if [ "$(wc -l < "$work/classes")" -ge "$(wc -l < "$work/every")" ]; then
    scope=ALL
    echo "Whole suite: the change reaches every test class." >&2
  else
    scope=$(sed 's/.*/FullyQualifiedName~.&./' "$work/classes" | paste -sd'|' -)
    echo "$(wc -l < "$work/classes" | tr -d ' ') of $(wc -l < "$work/every" | tr -d ' ') test classes:" \
      "$(paste -sd' ' "$work/classes")" >&2
  fi
fi

if [ "$run" = 0 ]; then echo "$scope"; exit 0; fi
case $scope in
  NONE) echo "Nothing to test." >&2 ;;
  ALL) dotnet test AuBtechReviewAgent.sln ${extra[@]+"${extra[@]}"} ;;
  *) dotnet test AuBtechReviewAgent.sln --filter "$scope" ${extra[@]+"${extra[@]}"} ;;
esac
