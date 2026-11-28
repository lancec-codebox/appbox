#!/bin/sh
# Entry of the IndexNode CPU image (0.13.83): `indexnode serve` in the foreground, so the container app restarts it if it ever exits.
# Settings from the environment (container app): INDEXNODE_SAS_URL and INDEXNODE_SERVE_TOKEN (secrets; never written to disk here),
# INDEXNODE_SEARCH_TOKEN (optional), PORT (8080), INDEXNODE_THREADS (default: the cores), INDEXNODE_CACHE_GB (the frames cache, default 2:
# a container's disk is small), INDEXNODE_SERVE_ARGS (default --download-service).
set -eu
[ -n "${INDEXNODE_SAS_URL:-}" ] || { echo "serve-entry: INDEXNODE_SAS_URL is not set" >&2; exit 2; }
/opt/indexnode/indexnode config set index.local-cache-gb "${INDEXNODE_CACHE_GB:-2}" > /dev/null
THREADS="${INDEXNODE_THREADS:-$(nproc)}"
echo "serve-entry: $(/opt/indexnode/indexnode --version) on :${PORT:-8080}, $THREADS threads, cache ${INDEXNODE_CACHE_GB:-2} GB, args ${INDEXNODE_SERVE_ARGS---download-service}"
exec /opt/indexnode/indexnode serve --port "${PORT:-8080}" --threads "$THREADS" ${INDEXNODE_SERVE_ARGS---download-service}
