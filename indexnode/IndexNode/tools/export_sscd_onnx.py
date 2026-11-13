#!/usr/bin/env python3
"""Convert sscd_disc_mixup.torchscript.pt to ONNX once, and check it against TorchScript on 20 frames.

    pip install torch onnx onnxruntime numpy pillow
    python export_sscd_onnx.py --out sscd_disc_mixup.onnx --frames ./frames [--check-f16 sscd.f16]

The model comes from the README of facebookresearch/sscd-copy-detection:
https://dl.fbaipublicfiles.com/sscd-copy-detection/sscd_disc_mixup.torchscript.pt

Preprocessing (what IndexNode does): resize the short side to 288, ImageNet normalisation.
The ONNX model takes float32 [N,3,H,W] (H and W dynamic) and returns [N,512]; IndexNode L2-normalises it.
The check needs cosine >= 0.999 between ONNX and TorchScript on every one of 20 frames, else exit code 1.
"""
import argparse, glob, os, sys, urllib.request

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image

URL = "https://dl.fbaipublicfiles.com/sscd-copy-detection/sscd_disc_mixup.torchscript.pt"
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def preprocess(path, short=288):
    im = Image.open(path).convert("RGB")
    w, h = im.size
    s = short / min(w, h)
    im = im.resize((round(w * s), round(h * s)), Image.BICUBIC)
    a = (np.asarray(im, dtype=np.float32) / 255.0 - MEAN) / STD
    return a.transpose(2, 0, 1)[None]


def cos(a, b):
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    return float(a @ b)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="sscd_disc_mixup.torchscript.pt")
    ap.add_argument("--out", default="sscd_disc_mixup.onnx")
    ap.add_argument("--frames", help="folder of jpg/png frames for the 20-frame check")
    ap.add_argument("--check-f16", help="optional sscd.f16 written by IndexNode for the same frames (reported, not asserted)")
    ap.add_argument("--opset", type=int, default=17)
    args = ap.parse_args()

    if not os.path.exists(args.model):
        print("downloading", URL)
        urllib.request.urlretrieve(URL, args.model)
    ts = torch.jit.load(args.model, map_location="cpu").eval()

    if not os.path.exists(args.out):
        dummy = torch.randn(1, 3, 288, 512)
        torch.onnx.export(
            ts, dummy, args.out, opset_version=args.opset, input_names=["input"], output_names=["descriptor"],
            dynamic_axes={"input": {0: "n", 2: "h", 3: "w"}, "descriptor": {0: "n"}}, dynamo=False,
        )
        print("wrote", args.out, os.path.getsize(args.out), "bytes")

    sess = ort.InferenceSession(args.out, providers=["CPUExecutionProvider"])
    files = sorted(glob.glob(os.path.join(args.frames, "*.jpg")) + glob.glob(os.path.join(args.frames, "*.png")))[:20] if args.frames else []
    need = 1 if args.check_f16 else 20  # a cross-check of IndexNode's own descriptors may use fewer frames
    if len(files) < need:
        print(f"need {need} frames to check, found {len(files)}; skipping the check", file=sys.stderr)
        return 0 if not args.frames else 1
    worst = 1.0
    ref = []
    for f in files:
        x = preprocess(f)
        with torch.no_grad():
            t = ts(torch.from_numpy(x))[0].numpy()
        o = sess.run(None, {"input": x})[0][0]
        c = cos(t, o)
        worst = min(worst, c)
        ref.append(t / np.linalg.norm(t))
    print(f"ONNX vs TorchScript on {len(files)} frames: min cosine {worst:.6f}")
    if args.check_f16:
        got = np.fromfile(args.check_f16, dtype=np.float16).reshape(-1, 512).astype(np.float32)
        cs = [cos(got[i], ref[i]) for i in range(min(20, len(got)))]
        print(f"IndexNode descriptors vs TorchScript (ffmpeg vs PIL resize): min cosine {min(cs):.6f}, mean {np.mean(cs):.6f}")
    return 0 if worst >= 0.999 else 1


if __name__ == "__main__":
    sys.exit(main())
