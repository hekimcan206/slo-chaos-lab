#!/bin/sh
# Grafana screenshot for a time range, from the pinned Playwright image attached to the compose network.
# Usage: sh tools/screenshots/run.sh <from-epoch-ms> <to-epoch-ms> <file.png>
set -eu
export MSYS_NO_PATHCONV=1
root=$(pwd)
docker run --rm --network slo-chaos-lab_default \
  -e GRAFANA_URL=http://grafana:3000 -e FROM_MS="$1" -e TO_MS="$2" -e OUT="$3" \
  -v "$root/tools/screenshots:/work" -v "$root/docs/images:/out" -w /work \
  mcr.microsoft.com/playwright:v1.64.1-noble \
  sh -c "[ -d node_modules/playwright ] || npm install --no-audit --no-fund --silent; node capture.mjs"
