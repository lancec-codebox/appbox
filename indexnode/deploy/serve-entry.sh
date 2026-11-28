#!/bin/sh
# Entry of the IndexNode CPU image (0.13.83): `indexnode serve` in the foreground, so the container app restarts it if it ever exits.
# Settings from the environment (container app): INDEXNODE_SAS_URL and INDEXNODE_SERVE_TOKEN (secrets; never written to disk here),
# INDEXNODE_SEARCH_TOKEN (optional), PORT (8080), INDEXNODE_THREADS (default: the cores), INDEXNODE_CACHE_GB (the frames cache, default 2:
# a container's disk is small), INDEXNODE_SERVE_ARGS (default --download-service).
set -eu
[ -n "${INDEXNODE_SAS_URL:-}" ] || { echo "serve-entry: INDEXNODE_SAS_URL is not set" >&2; exit 2; }
/opt/indexnode/indexnode config set index.local-cache-gb "${INDEXNODE_CACHE_GB:-2}" > /dev/null
# 0.13.90: INDEXNODE_PERSIST_DIR (optional; an Azure Files share the container app mounts): the frame codes copy and the frames cache survive a restart.
# Restored from it at start; written back every INDEXNODE_PERSIST_EVERY seconds (300): codes logs by their appended tails (--append; IndexNode trims a torn tail on
# its first sync), frames files whole (.part files are never copied). -rt, not -a: an SMB share takes no owners or modes.
H="${INDEXNODE_CONFIG_DIR:-/opt/indexnode-home}"; P="${INDEXNODE_PERSIST_DIR:-}"
if [ -n "$P" ] && [ -d "$P" ]; then
  for d in frame-codes frames-cache; do
    mkdir -p "$H/$d" "$P/$d"; t0=$(date +%s)
    if rsync -rt --exclude '*.part' "$P/$d/" "$H/$d/"; then echo "serve-entry: $d restored from $P: $(du -sm "$H/$d" | cut -f1) MB in $(( $(date +%s) - t0 )) s"; else echo "serve-entry: $d not restored (rsync failed); it fills from the store"; fi
  done
  ( while sleep "${INDEXNODE_PERSIST_EVERY:-300}"; do
      rsync -rt --append --delete "$H/frame-codes/" "$P/frame-codes/" || echo "serve-entry: frame-codes write-back failed"
      rsync -rt --delete --exclude '*.part' "$H/frames-cache/" "$P/frames-cache/" || echo "serve-entry: frames-cache write-back failed"
    done ) &
elif [ -n "$P" ]; then echo "serve-entry: INDEXNODE_PERSIST_DIR $P is not a folder: no persistent cache"; fi
THREADS="${INDEXNODE_THREADS:-$(nproc)}"
echo "serve-entry: $(/opt/indexnode/indexnode --version) on :${PORT:-8080}, $THREADS threads, cache ${INDEXNODE_CACHE_GB:-2} GB, args ${INDEXNODE_SERVE_ARGS---download-service}"
exec /opt/indexnode/indexnode serve --port "${PORT:-8080}" --threads "$THREADS" ${INDEXNODE_SERVE_ARGS---download-service}
