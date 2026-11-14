#!/usr/bin/env python3
"""CER of our transcript against a reference sensevoice json over a window.
usage: compare_asr.py full_sensevoice.json ours/sensevoice.zh.json START END  (needs rapidfuzz)"""
import json, re, sys
from rapidfuzz.distance import Levenshtein
ref = json.load(open(sys.argv[1]))["segments"]; t0, t1 = float(sys.argv[3]), float(sys.argv[4])
d = json.load(open(sys.argv[2])); ours = d["segments"]
def chars(segs):
    return "".join(re.sub(r"[^\u3400-\u9fffA-Za-z0-9]", "", s["text"]) for s in segs if t0 <= s["start"] and s["end"] <= t1 + 1)
a, b = chars(ref), chars(ours)
print(f"window {t0:.0f}-{t1:.0f}s: reference {len(a)} chars, ours {len(b)} chars, CER {Levenshtein.distance(a, b) / max(1, len(a)):.4f}; ours rtf {d.get('rtf')}")
