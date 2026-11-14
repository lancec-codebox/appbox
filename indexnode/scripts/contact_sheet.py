#!/usr/bin/env python3
"""A sheet of the search hits' frames. usage: contact_sheet.py <clip dir> <top.txt (dev search output)> <offset> <out.jpg>"""
import sys, re, json, glob
from PIL import Image, ImageDraw
d, top, off, out = sys.argv[1], sys.argv[2], float(sys.argv[3]), sys.argv[4]
times = [e["t"] for e in json.load(open(d + "/frames.json"))]
files = sorted(glob.glob(d + "/frames/*.jpg"))
tiles = []
for line in open(top):
    m = re.match(r"(?:(\d+):)?(\d+):(\d+)\s+([\d.]+)", line)
    if not m: continue
    t = int(m[1] or 0) * 3600 + int(m[2]) * 60 + int(m[3]) - off
    i = min(range(len(times)), key=lambda k: abs(times[k] - t))
    im = Image.open(files[i]).convert("RGB"); im.thumbnail((240, 427))
    ImageDraw.Draw(im).text((4, 4), line.strip()[:22], fill=(255, 255, 0)); tiles.append(im)
sheet = Image.new("RGB", (sum(t.width for t in tiles), max(t.height for t in tiles)))
x = 0
for t in tiles: sheet.paste(t, (x, 0)); x += t.width
sheet.save(out)
