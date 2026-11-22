#!/usr/bin/env python3
"""Static QDQ int8, per-channel quantization of a face recognizer (w600k_r50 and kin).

  python scripts/quantize_face.py --model w600k_r50.onnx --calib crops_a crops_b --out w600k_r50_int8.onnx [--method minmax|percentile]

--calib: folders of aligned 112x112 RGB crops written by 'indexnode cast reembed --dump-crops N' (.npy uint8 HxWx3), or .png/.jpg
(needs Pillow). Preprocessing is the model's own: (x - 127.5) / 127.5, RGB, NCHW. Conv and MatMul are quantized (QDQ, per channel,
int8 weights, uint8 activations); never dynamic quantization. Needs: pip install onnx onnxruntime numpy
"""
import argparse
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.quantization import CalibrationDataReader, CalibrationMethod, QuantFormat, QuantType, quantize_static


def load_crop(p: Path) -> np.ndarray:
    if p.suffix.lower() == ".npy":
        return np.load(p)
    from PIL import Image
    return np.asarray(Image.open(p).convert("RGB").resize((112, 112)))


class Reader(CalibrationDataReader):
    def __init__(self, files, input_name):
        self.it = iter(files); self.name = input_name

    def get_next(self):
        p = next(self.it, None)
        if p is None:
            return None
        x = (load_crop(p).astype(np.float32) - 127.5) / 127.5
        return {self.name: np.ascontiguousarray(x.transpose(2, 0, 1)[None])}


def count_ops(path):
    m = onnx.load(str(path)); ops = {}
    for n in m.graph.node:
        ops[n.op_type] = ops.get(n.op_type, 0) + 1
    return ops


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True, type=Path)
    ap.add_argument("--calib", required=True, nargs="+", type=Path)
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--method", choices=["minmax", "percentile"], default="minmax")
    a = ap.parse_args()
    files = sorted(f for d in a.calib for f in (d.iterdir() if d.is_dir() else [d]) if f.suffix.lower() in (".npy", ".png", ".jpg", ".jpeg"))
    if not files:
        raise SystemExit("no calibration crops found")
    src = onnx.load(str(a.model)); ver = next((o.version for o in src.opset_import if o.domain in ("", "ai.onnx")), 0)
    model_path = a.model
    if ver < 13:   # per-channel DequantizeLinear needs 'axis', which ORT rejects below opset 13 (InvalidGraph)
        src = onnx.version_converter.convert_version(src, 13); model_path = a.out.with_suffix(".opset13.onnx"); a.out.parent.mkdir(parents=True, exist_ok=True)
        onnx.save(src, str(model_path)); print(f"converted opset {ver} -> 13")
    sess = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    name = sess.get_inputs()[0].name; del sess
    method = CalibrationMethod.MinMax if a.method == "minmax" else CalibrationMethod.Percentile
    print(f"calibrating on {len(files)} crops, method {a.method}")
    a.out.parent.mkdir(parents=True, exist_ok=True)
    quantize_static(str(model_path), str(a.out), Reader(files, name), quant_format=QuantFormat.QDQ,
                    op_types_to_quantize=["Conv", "MatMul"], per_channel=True,
                    activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8, calibrate_method=method)
    if model_path != a.model: model_path.unlink()
    ops = count_ops(a.out)
    print(f"ops: {sum(ops.values())} total; Conv {ops.get('Conv', 0)}, MatMul {ops.get('MatMul', 0)}, QuantizeLinear {ops.get('QuantizeLinear', 0)}, DequantizeLinear {ops.get('DequantizeLinear', 0)}")
    print(f"size: {a.model.stat().st_size / 1e6:.1f} MB -> {a.out.stat().st_size / 1e6:.1f} MB  ({a.out})")


if __name__ == "__main__":
    main()
