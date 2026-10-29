#!/usr/bin/env bash
# Writes <apk>.sha256 next to a release APK, in `sha256sum` format ("<hex>  <file name>").
# Used by .github/workflows/medusa.yml; the app's updater verifies the download against it.
set -euo pipefail
apk="${1:?usage: make-release-assets.sh path/to/medusa-X.Y.Z.apk}"
test -f "$apk" || { echo "no such file: $apk" >&2; exit 1; }
dir="$(dirname "$apk")"; name="$(basename "$apk")"
( cd "$dir" && sha256sum "$name" > "$name.sha256" )
echo "$dir/$name.sha256"
