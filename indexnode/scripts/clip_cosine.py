#!/usr/bin/env python3
"""Cosine between our clip.f16 rows and transformers' SigLIP2 image embeddings for N frames.
usage: clip_cosine.py <out dir with frames/ and clip.f16> [N=5] [ffmpeg|pil]
  ffmpeg (default): the reference model gets the same 224x224 pixels the node feeds (decoded with the node's ffmpeg filter, which is
                    the squash resize the checkpoint's SiglipImageProcessor does: no padding, bilinear, x/255, mean 0.5, std 0.5)
  pil: the reference does its own resize with PIL (SiglipImageProcessor's resample=2, bilinear)
needs torch, transformers, pillow, numpy; ffmpeg on PATH"""
import sys, glob, subprocess, numpy as np, torch
from PIL import Image
from transformers import AutoModel
d = sys.argv[1]; n = int(sys.argv[2]) if len(sys.argv) > 2 else 5; mode = sys.argv[3] if len(sys.argv) > 3 else "ffmpeg"
raw = np.fromfile(d + "/clip.f16", dtype=np.float16).astype(np.float32).reshape(-1, 768)
files = sorted(glob.glob(d + "/frames/*.jpg"))
idx = np.linspace(0, len(files) - 1, n).astype(int)
m = AutoModel.from_pretrained("google/siglip2-base-patch16-224").eval()
def prep(p):
    if mode == "pil":
        a = np.asarray(Image.open(p).convert("RGB").resize((224, 224), Image.BILINEAR), dtype=np.float32)
    else:
        b = subprocess.run(["ffmpeg", "-v", "error", "-i", p, "-vf", "scale=224:224:flags=bilinear", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], capture_output=True, check=True).stdout
        a = np.frombuffer(b, np.uint8).reshape(224, 224, 3).astype(np.float32)
    return torch.from_numpy(((a / 255.0 - 0.5) / 0.5).transpose(2, 0, 1))
with torch.no_grad():
    ref = m.get_image_features(pixel_values=torch.stack([prep(files[i]) for i in idx]))
    ref = (ref if torch.is_tensor(ref) else ref.pooler_output).numpy()
cos = []
for k, i in enumerate(idx):
    a, b = raw[i], ref[k]
    cos.append(float(a @ b / np.linalg.norm(a) / np.linalg.norm(b)))
print(mode, "frames", list(map(int, idx)), "cosines", [round(c, 5) for c in cos], "min", round(min(cos), 5))
