# indexnode serve (0.12.0)

The general query service (it was `precheck-serve`; that name still works as a hidden alias). Two queries share one warm index: the download pre-check and the screenshot search.

The service is called `indexnode`: health `name`, `Server: indexnode` header, user agent `indexnode/<version>`, and the sandbox is named `indexnode`.

Warm pre-check analysis service. Nodes (desktop, phones) sample and send; the service decides. It never writes to the store.

    indexnode serve [--port 8080] [--threads N] [--block-cache-mb 512] [--concurrency 2] [--input-local dir]

## API
- `POST /v1/precheck` multipart/form-data
  - `meta` (application/json): `{id, duration, node, sampleDownloadSeconds, window:{start,end}, frameTimes:[...], info:{id,title,channel,uploader,channel_id,upload_date,height,language,duration,subtitles:{lang:[]},automatic_captions:{lang-orig:[]},formats:[{vcodec:"none",acodec,language,format_note}]}}`
  - `frames`: repeated, 000000.jpg ... in time order (60 by default, the frames step's selection, evenly subsampled; max 200)
  - `audio`: ~100 s from the middle of the window, 16 kHz mono 16-bit PCM WAV (~3 MB; the service decodes no Opus/Ogg, so it needs no ffmpeg); optional (no audio: the metadata language is used)
  - `excludeSelf=true`: leave the upload's own centers out (tests)
  - 200: the precheck.json body (decision, reasons, top, speech with per-chunk tags, ai, seconds per stage, indexVersion, version, matchUpload, service{...}); 400: `{error}` for a bad request; 500: `{error}`
- `POST /v1/search` (multipart, or `?topCenters=&top=&trim=` in the query string): the screenshot query. Send **one** of `image` (JPEG/PNG file) or `vector` (512 values, f32 = 2048 bytes or f16 = 1024 bytes, little-endian); optional `topCenters` (default 300, max 5000), `top` (default 20, max 200), `trim=true` (crop the black bars at the screenshot's edges with the frames step's bar rule before SSCD; the applied crop is echoed). The image takes the same path as `indexnode search --image [--trim]` (`CentersCommands.EmbedImageDetailAsync`: ffmpeg decode, bicubic scale to the SSCD size, SSCD, L2) and the lookup is `CenterSearch.QueryAsync` (the CLI's); nothing is written to the store. Response: `{indexVersion, query:{kind,trim,crop,topCenters,top}, hits:[{upload,time,timestamp,score,row,center}], timings:{embedMs,scanMs,readMs,scoreMs,uploads,requests,bytes,totalMs}}`. The index does not know drama groups, so hits carry the upload id only. 400 for no query, a bad vector size or an unreadable image.
- `GET /v1/health`: `{ok, name:"indexnode", version, indexVersion, models[], uptimeSeconds, threads, requests, failures, workingSetMB, blockCache{...}}`

## Deploying (sandbox, no container image)
The sandbox runs the released single-file Linux exe (downloaded from the appbox GitHub release), like the processing nodes; there is no Dockerfile for the service.
- Run: `indexnode serve --port 8080 --threads 4` (`--threads` defaults to the CPU count).
- Port 8080, ingress anonymous (the service has no auth of its own).
- Env: `INDEXNODE_SAS_URL` = a **read-only** (r, l) container SAS of the index store. Optional `INDEXNODE_CONFIG_DIR` / `HOME`: a writable folder for the models and the search-index cache.
- On the box: **no ffmpeg/ffprobe**: a request starts no child process (see "Processes" below); only the **ASP.NET Core** 10 runtime unless the exe is published self-contained.
- Models (SSCD int8 25 MB, SenseVoice int8 ~230 MB) download on first start (sha256 checked) into the config tools folder; the service listens only after the warm-up, so `/v1/health` answers once it is ready. Needs egress to github.com and huggingface.co on first start; keep the config folder to skip the download on later starts.
- Memory: allow 2.5 GB (plus the block cache, 512 MB default); 4 vCPU.

## Required by default (`precheck.required`, default true)

A download node without a reachable service does not download. With no `precheck.service-url`, or when the service is still unreachable after the 60 s timeout and one retry, the job goes back to **pending** with a later due time: 10 min, then 20, 40, 80, at most 2 h (counted in `preCheckDeferrals` on the job; no attempt is counted, no failure, no bot back-off). The node status reads `pre-check service unreachable` and the log says so (or says that no service-url is configured). `indexnode config precheck.required false` restores the old behavior: the download goes on and the record says `precheck: skipped (service unreachable)`. `--no-precheck` still turns the pre-check off.

## Processes

`/v1/precheck` and `/v1/search` start **0 child processes**. Everything runs in the serve process against the models and caches loaded at start:

| step | how |
|---|---|
| frames (JPEG from the client) | `BitMiracle.LibJpeg.NET`, managed (`Imaging.DecodeJpeg`) |
| screenshot (JPEG or PNG) | managed JPEG decoder / `InProcessMedia.DecodePng` (all colour types, 1-16 bit, non-interlaced; interlaced PNG is refused with 400) |
| bar crop / `trim` | `Bars.Detect` on luminance computed in memory |
| crop + resize to short side 288 | `Swscale` (managed port of libswscale's default bicubic, incl. chroma handling) on the planes `JpegYuv` decodes with ffmpeg's integer IDCT |
| SSCD | the loaded int8 ONNX session (`OrtOptions.Shared`) |
| lookup | `CenterSearch` / `PreCheckSearch` on the loaded index and the shared block cache |
| AI probe | `AiProbe.Default` |
| language ID | the loaded SenseVoice (sherpa-onnx, in-process) on 16 kHz mono PCM |

Audio: the client (which has ffmpeg, it cut the sample) sends 16 kHz mono 16-bit PCM WAV, so the service decodes no Opus. Only RIFF WAV at 16 kHz is accepted; anything else is a 400.

The one remaining ffmpeg use is on the **client** (`indexnode` node doing the pre-check): cutting frames out of the downloaded sample video (AV1/H.264 decoding) and the audio clip. That is video decoding, which has no managed implementation here, and it is not on the server.
Vector parity: the in-process preprocessing reproduces the ffmpeg path pixel for pixel (JPEG decode with ffmpeg's simple_idct, swscale bicubic, chroma and YUV->RGB handling), so service vectors equal the node's (cosine 1.000000 on the test images; `SwscaleTests`, and `SscdParityTests` with INDEXNODE_E2E=1). Progressive/unusual JPEGs fall back to libjpeg decoding and the same scaler (still within one pixel level, not bit-exact).

Client sample frames: the client cuts them with the stored-frame encoder (`Frames.ProduceAsync`, JPEG `-q:v 2`, yuvj444p, `FrameSettings.JpegQ`) and uploads those files as they are: no second recompression, so the quality is the stored frames' q2.
