#!/usr/bin/env bash
# Runs the browser checks of the accessibility job side by side against one running app, so the job waits for the
# slowest check instead of the sum of them. axe-core, the print check and the paged report check share a lane; the
# smoke test and the link and button check each have their own. Each lane writes its own log, printed in a group when all are done, and the script
# fails when any lane fails. The checks only read the app: none of them changes what another one looks at.
#   cd tools/a11y && BASE_URL=http://localhost:5038 CTA_KEYS=/path/keys.json bash run-checks.sh
set -u
cd "$(dirname "$0")"
logs=$(mktemp -d)

(node axe-check.mjs && node print-check.mjs && node paged-check.mjs) > "$logs/axe-print-pages.log" 2>&1 &
pids[0]=$!; names[0]=axe-print-pages
node smoke.mjs > "$logs/smoke.log" 2>&1 &
pids[1]=$!; names[1]=smoke
node cta-check.mjs > "$logs/cta.log" 2>&1 &
pids[2]=$!; names[2]=cta

fail=0
for i in 0 1 2; do
  if wait "${pids[$i]}"; then result=passed; else result=failed; fail=1; fi
  echo "::group::${names[$i]} ($result)"
  cat "$logs/${names[$i]}.log"
  echo "::endgroup::"
  [ "$result" = failed ] && echo "::error::${names[$i]} failed; open its group above for the reason"
done
exit $fail
