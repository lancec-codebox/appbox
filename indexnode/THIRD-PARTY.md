# Third-party models and libraries

- **InsightFace buffalo_l** (SCRFD det_10g, ArcFace w600k_r50): the default `cast` face models. Non-commercial weights licence; used for internal data processing only. Select `cast.model: yunet-sface` to avoid them.
- **YuNet** (OpenCV Zoo, MIT) and **SFace** (OpenCV Zoo, Apache-2.0): the selectable alternative face models.
- **QRCoder** 1.6.0 (MIT): QR images for `medusa-setup`.
- Other models (SigLIP, SSCD, SenseVoice, PaddleOCR) and NuGet packages keep their own licences; see their sources in `IndexNode/Tools/Models.cs` and `IndexNode.csproj`.
