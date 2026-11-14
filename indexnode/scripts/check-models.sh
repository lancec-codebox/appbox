#!/usr/bin/env bash
# Reruns the real-model checks on short cuts of the test drama. Needs: dotnet, ffmpeg in the tools dir, python3 with
# rapidfuzz (compare_*.py) and torch+transformers+pillow+numpy (clip_cosine.py), and the unpacked drama-ref pack.
# usage: REF=<dir with input.mp4, full_sensevoice.json, ref_ocr_lines.json> [PY=python3] [WORK=/tmp/indexnode-check] [THREADS=4] scripts/check-models.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; REF="${REF:?set REF}"; PY="${PY:-python3}"; WORK="${WORK:-/tmp/indexnode-check}"; T="${THREADS:-$(nproc)}"
mkdir -p "$WORK"; cd "$HERE/../IndexNode"; dotnet build -v q -nologo >/dev/null
BIN="$HERE/../IndexNode/bin/Debug/net10.0"
# one native onnxruntime (sherpa-onnx's 1.28.2) serves both packages: no LD_PRELOAD or LD_LIBRARY_PATH needed
dev() { dotnet "$BIN/indexnode.dll" dev "$@"; }
cut() { [ -f "$WORK/$1.mp4" ] || ffmpeg -v error -y -ss "$2" -t "$3" -i "$REF/input.mp4" -c copy "$WORK/$1.mp4"; }
cut c20_30 1200 600; cut c20_25 1200 300; cut c90 5400 300
echo "== ocr 20:00-30:00"; dev ocr --video "$WORK/c20_30.mp4" --out "$WORK/ocr" --offset 1200 --threads "$T"
"$PY" "$HERE/compare_ocr.py" "$REF/ref_ocr_lines.json" "$WORK/ocr/lines.json" 1200 1800
echo "== clip 90:00-95:00"; dev clip --video "$WORK/c90.mp4" --out "$WORK/clip" --threads "$T"
"$PY" "$HERE/clip_cosine.py" "$WORK/clip" 5 ffmpeg   # same 224x224 pixels in, so only the model is compared
"$PY" "$HERE/clip_cosine.py" "$WORK/clip" 5 pil      # reference does its own PIL resize (for information)
dev search a man wearing a silver mask --out "$WORK/clip" --offset 5400 --top 5 | tee "$WORK/top5.txt"
"$PY" "$HERE/contact_sheet.py" "$WORK/clip" "$WORK/top5.txt" 5400 "$WORK/silver-mask.jpg"
echo "== ocr profile, 1 worker, 200 frames (ms per frame by phase)"
dev ocrprof --video "$WORK/c20_30.mp4" --band "$(python3 -c "import json;b=json.load(open('$WORK/ocr/lines.json'))['band'];print(b['y'],b['height'],sep=',')")" --frames 200
echo "== transcribe 20:00-25:00"; dev asr --video "$WORK/c20_25.mp4" --out "$WORK/asr" --offset 1200 --threads "$T"
"$PY" "$HERE/compare_asr.py" "$REF/full_sensevoice.json" "$WORK/asr/sensevoice.zh.json" 1200 1500
echo "== transcribe + sync 20:00-30:00"; dev asr --video "$WORK/c20_30.mp4" --out "$WORK/asr30" --offset 1200 --threads "$T"
dev sync --lines "$WORK/ocr/lines.json" --asr "$WORK/asr30/sensevoice.zh.json" --from 1200 --to 1800
echo "== sync, reference OCR + reference transcript"
dev sync --lines "$REF/ref_ocr_lines.json" --asr "$REF/full_sensevoice.json" --from 1200 --to 1800
echo "== dubbed: set DUBBED_CLIP=<english-dubbed video> to run"
[ -n "${DUBBED_CLIP:-}" ] && dev asr --video "$DUBBED_CLIP" --out "$WORK/dub" --threads "$T" || echo "(skipped)"
echo "== model hashes"; (cd "$(dirname "$(find "${INDEXNODE_CONFIG_DIR:-$HOME/.config/indexnode}" -name silero_vad.onnx | head -1)")" && sha256sum *)
