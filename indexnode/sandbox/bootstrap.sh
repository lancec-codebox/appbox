#!/usr/bin/env bash
# IndexNode sandbox bootstrap (0.13.21): turns a bare Ubuntu 24.04 box (with .NET 10 libs and curl; no python, no nc) into an `indexnode serve` instance
# answering POST /v1/check on a port that wakes the sandbox on HTTP (the downloader's inline sample check).
#
#   bootstrap.sh --binary-url URL --sha256 HEX --sas-file F --token-file F [--port 8080] [--threads N] [--models-url URL]
#
# Every option can also come from the environment: INDEXNODE_BINARY_URL, INDEXNODE_BINARY_SHA256, INDEXNODE_SAS_FILE, INDEXNODE_TOKEN_FILE, INDEXNODE_PORT, INDEXNODE_THREADS.
# The SAS URL and the token are read from FILES only: never from argv (visible in ps) and never exported into the environment of the service.
# Idempotent: a re-run installs the binary to releases/indexnode-<sha12> and swaps the symlink (same sha256: nothing is downloaded), stops the old loop and every old serve (SIGTERM, 10 s, SIGKILL), restarts, and checks the answering server is the new one.
set -euo pipefail

BINARY_URL="${INDEXNODE_BINARY_URL:-}"; SHA256="${INDEXNODE_BINARY_SHA256:-}"; SAS_FILE="${INDEXNODE_SAS_FILE:-}"; TOKEN_FILE="${INDEXNODE_TOKEN_FILE:-}"
PORT="${INDEXNODE_PORT:-8080}"; THREADS="${INDEXNODE_THREADS:-}"
INSTALL_DIR="${INDEXNODE_INSTALL_DIR:-/opt/indexnode}"; CONFIG_DIR="${INDEXNODE_CONFIG_DIR:-/root/.config/indexnode}"; LOG="${INDEXNODE_LOG:-/var/log/indexnode-serve.log}"

usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-2}"; }
die() { echo "bootstrap: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --binary-url) BINARY_URL="${2:?}"; shift 2 ;;
    --sha256) SHA256="${2:?}"; shift 2 ;;
    --sas-file) SAS_FILE="${2:?}"; shift 2 ;;
    --token-file) TOKEN_FILE="${2:?}"; shift 2 ;;
    --port) PORT="${2:?}"; shift 2 ;;
    --threads) THREADS="${2:?}"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "bootstrap: unknown argument: $1" >&2; usage 2 ;;
  esac
done

[ -n "$BINARY_URL" ] || die "--binary-url (or INDEXNODE_BINARY_URL) is required"
[ -n "$SHA256" ] || die "--sha256 (or INDEXNODE_BINARY_SHA256) is required"
[ -n "$SAS_FILE" ] && [ -r "$SAS_FILE" ] || die "--sas-file must name a readable file"
[ -n "$TOKEN_FILE" ] && [ -r "$TOKEN_FILE" ] || die "--token-file must name a readable file"
case "$PORT" in ''|*[!0-9]*) die "--port must be a number" ;; esac
[ -n "$THREADS" ] || THREADS="$(nproc 2>/dev/null || echo 2)"
case "$THREADS" in ''|*[!0-9]*) die "--threads must be a number" ;; esac
command -v curl >/dev/null || die "curl is required"
command -v sha256sum >/dev/null || die "sha256sum is required"

umask 077
mkdir -p "$INSTALL_DIR/releases" "$CONFIG_DIR" "$(dirname "$LOG")"
BIN="$INSTALL_DIR/indexnode"   # a SYMLINK to releases/indexnode-<sha12>: a running process's file is never overwritten in place (the single-file binary loads assemblies lazily from itself)

# 1. the binary, checked against its sha256 and installed to a versioned path (a re-run with the same hash downloads nothing)
want="$(printf '%s' "$SHA256" | tr 'A-F' 'a-f')"
REL="$INSTALL_DIR/releases/indexnode-${want:0:12}"
have=""; [ -f "$REL" ] && have="$(sha256sum "$REL" | cut -d' ' -f1)"
if [ "$have" != "$want" ]; then
  tmp="$REL.new.$$"; trap 'rm -f "$tmp"' EXIT
  curl -fsSL --retry 3 --retry-delay 2 -o "$tmp" "$BINARY_URL" || die "download of the binary failed"
  got="$(sha256sum "$tmp" | cut -d' ' -f1)"
  [ "$got" = "$want" ] || die "sha256 mismatch for the binary (expected $want, got $got)"
  chmod 0755 "$tmp"; mv -f "$tmp" "$REL"; trap - EXIT
else
  echo "bootstrap: binary already at $want"
fi
WANT_VERSION="$("$REL" --version 2>/dev/null | head -n1)"; [ -n "$WANT_VERSION" ] || die "the new binary does not run (--version printed nothing)"

# 2. stop the old service: the loop AND every old serve process (the loop's child outlives the loop). SIGTERM, up to 10 s, then SIGKILL; the port must be free afterwards.
OLD_RE="$INSTALL_DIR/(indexnode|releases/indexnode-[0-9a-f]+) serve"
alive() { local s; s="$(awk '{print $3}' "/proc/$1/stat" 2>/dev/null)"; [ -n "$s" ] && [ "$s" != "Z" ]; }   # a zombie is already dead
old_pids() { { [ -f "$PIDFILE" ] && cat "$PIDFILE"; pgrep -f "$INSTALL_DIR/serve-loop.sh"; pgrep -f "$OLD_RE"; } 2>/dev/null | grep -E '^[0-9]+$' | grep -vx "$$" | sort -u | while read -r p; do alive "$p" && echo "$p"; done; true; }
port_busy() { (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; }
PIDFILE="$INSTALL_DIR/serve-loop.pid"
OLD_PIDS="$(old_pids | tr '\n' ' ')"
if [ -n "$OLD_PIDS" ]; then
  echo "bootstrap: stopping the old service (pids $OLD_PIDS)"
  [ -f "$PIDFILE" ] && kill "$(cat "$PIDFILE")" 2>/dev/null || true   # the loop first: it must not restart what we stop
  pkill -f "$INSTALL_DIR/serve-loop.sh" 2>/dev/null || true
  # shellcheck disable=SC2086
  kill $OLD_PIDS 2>/dev/null || true
  for _ in $(seq 1 20); do [ -z "$(old_pids)" ] && break; sleep 0.5; done
  if [ -n "$(old_pids)" ]; then
    echo "bootstrap: old service still running after 10 s (pids $(old_pids | tr '\n' ' ')): SIGKILL"
    # shellcheck disable=SC2046
    kill -9 $(old_pids) 2>/dev/null || true; sleep 1
  fi
  [ -z "$(old_pids)" ] || die "could not stop the old service (pids $(old_pids | tr '\n' ' '))"
fi
for _ in $(seq 1 10); do port_busy || break; sleep 0.5; done
if port_busy; then die "port $PORT is still in use after the old service was stopped (another process holds it)"; fi

# 3. swap the symlink (atomic rename; a regular file from an older bootstrap is replaced the same way), keep the two newest releases
ln -sfn "$REL" "$BIN.new.$$" && mv -T -f "$BIN.new.$$" "$BIN"
n=0; for f in $(find "$INSTALL_DIR/releases" -maxdepth 1 -name 'indexnode-????????????' -printf '%T@ %p\n' | sort -rn | cut -d' ' -f2-); do n=$((n + 1)); [ "$n" -le 2 ] || [ "$f" = "$REL" ] || rm -f "$f"; done
echo "bootstrap: installed $WANT_VERSION ($REL)"

export INDEXNODE_CONFIG_DIR="$CONFIG_DIR" INDEXNODE_REGION="${INDEXNODE_REGION:-westus2}"

# 2. the config, written straight from the two files (umask 077: user-only). Nothing secret is ever on a command line or in an exported variable. A re-run rewrites it (this box only serves).
json_escape() { sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\r\n'; }
{
  printf '{\n  "Sas": "%s",\n' "$(json_escape < "$SAS_FILE")"
  printf '  "Serve": { "Token": "%s" },\n' "$(json_escape < "$TOKEN_FILE")"
  printf '  "StorageRegion": "%s"\n}\n' "$INDEXNODE_REGION"
} > "$CONFIG_DIR/config.json.new"
[ "$(wc -c < "$CONFIG_DIR/config.json.new")" -gt 60 ] || die "the SAS or token file is empty"
chmod 600 "$CONFIG_DIR/config.json.new"; mv -f "$CONFIG_DIR/config.json.new" "$CONFIG_DIR/config.json"

# 3. ffmpeg (its tarball is .tar.xz: the box needs xz) and the models serve loads (SSCD int8, SenseVoice + tokens; about 0.3 GB, not the whole set)
if ! command -v xz >/dev/null; then
  echo "bootstrap: installing xz-utils"
  if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi
  $SUDO apt-get update -qq && $SUDO apt-get install -y -qq xz-utils || die "xz is missing and xz-utils could not be installed"
fi
"$BIN" tools install --ffmpeg
"$BIN" tools install --models serve || echo "bootstrap: models are fetched on first start instead"

# 4. the service: a tiny restart loop (nohup), the log in /var/log/indexnode-serve.log. A re-run stops the old loop first.
RUNNER="$INSTALL_DIR/serve-loop.sh"
cat > "$RUNNER" <<EOF
#!/usr/bin/env bash
export INDEXNODE_CONFIG_DIR="$CONFIG_DIR" INDEXNODE_REGION="$INDEXNODE_REGION"
while true; do
  "$BIN" serve --port $PORT --threads $THREADS >> "$LOG" 2>&1
  echo "\$(date -u +%FT%TZ) serve exited (\$?), restarting in 2 s" >> "$LOG"
  sleep 2
done
EOF
chmod 0755 "$RUNNER"
nohup "$RUNNER" >/dev/null 2>&1 &
LOOP_PID=$!
echo "$LOOP_PID" > "$PIDFILE"
echo "bootstrap: indexnode serve $WANT_VERSION starting on port $PORT with $THREADS threads (log: $LOG)"

# 5. wait for health, and for the NEW server: /v1/health must report this binary's version and a pid that is not one of the old ones (the same version on a re-run: the pid tells). An old process answering is a failure, not "healthy".
for _ in $(seq 1 120); do
  h="$(curl -fsS "http://127.0.0.1:$PORT/v1/health" 2>/dev/null || true)"
  if [ -n "$h" ]; then
    gv="$(printf '%s' "$h" | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p')"; gp="$(printf '%s' "$h" | sed -n 's/.*"pid": *\([0-9]*\).*/\1/p')"
    if [ "$gv" != "$WANT_VERSION" ]; then die "the server answering on port $PORT is version '$gv', not '$WANT_VERSION' (an old process is still running? see $LOG)"; fi
    case " $OLD_PIDS " in *" $gp "*) die "the server answering on port $PORT is the OLD process (pid $gp)" ;; esac
    [ -n "$gp" ] || die "/v1/health does not report a pid: cannot tell the new server from an old one"
    echo "bootstrap: healthy (version $gv, pid $gp)"; exit 0
  fi
  sleep 2
done
die "serve did not become healthy in 4 minutes; see $LOG"
