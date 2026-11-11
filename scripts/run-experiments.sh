#!/bin/sh
# Runs the recorded experiments against the compose stack: for each experiment, the "before" variant on the
# baseline profile and the "after" variant on the hardened profile. Before every run the API and Prometheus are
# recreated (fresh process, fresh TSDB on tmpfs) so no run inherits another's burn-rate windows; the Grafana
# screenshot is taken right after the run, before the next recreate wipes the data.
# The runner itself runs in a container on the compose network: load never crosses the host's port forwarding,
# so the latencies measured are the service's, not the Docker Desktop proxy's.
# Usage (repo root, `docker compose up -d` done): sh scripts/run-experiments.sh [baseline latency jitter bandwidth reset partition]
set -eu
export MSYS_NO_PATHCONV=1
RATE=${RATE:-50}
WARMUP=${WARMUP:-60}
FAULT=${FAULT:-120}
RECOVERY=${RECOVERY:-90}
EXPERIMENTS=${*:-baseline latency jitter bandwidth reset partition}
OUT=docs/results
mkdir -p "$OUT"

chaos() {
  docker compose run --rm --no-deps -v "$(pwd)/$OUT:/results" demo "$@" \
    --api http://api:8080/ --toxiproxy http://toxiproxy:8474/ --prometheus http://prometheus:9090/ --out /results
}

for exp in $EXPERIMENTS; do
  for variant in before after; do
    if [ "$variant" = before ]; then profile=baseline; else profile=hardened; fi
    echo "=== $exp / $variant (profile $profile) ==="
    LAB_PROFILE=$profile docker compose up -d --force-recreate --no-deps api prometheus
    sleep 10
    from=$(($(date +%s) * 1000 - 10000))
    chaos run --experiment "$exp" --variant "$variant" --rate "$RATE" --warmup "$WARMUP" --fault "$FAULT" --recovery "$RECOVERY" \
      > "$OUT/$exp-$variant.log" 2>&1 || { tail -30 "$OUT/$exp-$variant.log"; exit 1; }
    to=$(($(date +%s) * 1000))
    sed -n '/^### /,/^Recovered/p' "$OUT/$exp-$variant.log"
    sh tools/screenshots/run.sh "$from" "$to" "$exp-$variant.png" || echo "screenshot failed for $exp-$variant"
  done
done
chaos report --results /results
