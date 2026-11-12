#!/bin/sh
# Grafana screenshot for a time range.
# Default: the pinned Playwright image, attached to the compose network (tools/screenshots/capture.mjs).
# Alternative: CHROME=/path/to/chrome uses a locally installed Chrome in headless mode against the published Grafana
# port (no image pull; useful when disk or registry access is short).
# Usage: sh tools/screenshots/run.sh <from-epoch-ms> <to-epoch-ms> <file.png>
set -eu
export MSYS_NO_PATHCONV=1
root=$(pwd)
url_query="orgId=1&from=$1&to=$2&timezone=utc&kiosk&theme=light&refresh="

if [ -n "${CHROME:-}" ]; then
  out="$root/docs/images/$3"
  profile="${TMPDIR:-/tmp}/slo-chaos-lab-chrome"
  if command -v cygpath >/dev/null 2>&1; then out=$(cygpath -w "$out"); profile=$(cygpath -w "$profile"); fi
  # virtual-time-budget lets every panel query and paint before the capture; the window is tall enough that no
  # resize (which makes chart canvases redraw) happens.
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --window-size=1600,1650 --virtual-time-budget=20000 \
    --user-data-dir="$profile" --screenshot="$out" \
    "http://localhost:${GRAFANA_PORT:-58300}/d/slo-ledger/ledger-posting-slos?$url_query" 2>/dev/null
  echo "wrote $3"
  exit 0
fi

docker run --rm --network slo-chaos-lab_default \
  -e GRAFANA_URL=http://grafana:3000 -e FROM_MS="$1" -e TO_MS="$2" -e OUT="$3" \
  -v "$root/tools/screenshots:/work" -v "$root/docs/images:/out" -w /work \
  mcr.microsoft.com/playwright:v1.64.1-noble \
  sh -c "[ -d node_modules/playwright ] || npm install --no-audit --no-fund --silent; node capture.mjs"
