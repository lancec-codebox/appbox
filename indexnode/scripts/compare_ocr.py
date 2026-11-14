#!/usr/bin/env python3
"""Share of reference OCR cues matched by ours with CER <= 0.1 in a time window.
usage: compare_ocr.py ref_ocr_lines.json ours/lines.json START END"""
import json, sys

def lev(a, b):
    prev = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        cur = [i]
        for j, y in enumerate(b, 1):
            cur.append(min(cur[-1] + 1, prev[j] + 1, prev[j - 1] + (x != y)))
        prev = cur
    return prev[-1]

ref = json.load(open(sys.argv[1]))["zh"]
ours = json.load(open(sys.argv[2]))["zh"]
t0, t1 = float(sys.argv[3]), float(sys.argv[4])
ref = [c for c in ref if t0 <= c["start"] and c["end"] <= t1]
ours = [c for c in ours if t0 <= c["start"] and c["end"] <= t1]
ok = 0; cers = []
for r in ref:
    best = 9.0
    for o in ours:
        if o["end"] < r["start"] or o["start"] > r["end"]:
            continue
        txt = o.get("text")
        best = min(best, lev(r["text"], txt) / max(1, len(r["text"])))
    cers.append(best)
    ok += best <= 0.1
print(f"window {t0:.0f}-{t1:.0f}s: reference cues {len(ref)}, ours {len(ours)}, matched (CER<=0.1) {ok} = {100*ok/max(1,len(ref)):.1f}%")
extra = [o for o in ours if not any(not (o["end"] < r["start"] or o["start"] > r["end"]) for r in ref)]
print(f"our cues with no reference cue overlapping: {len(extra)}")
