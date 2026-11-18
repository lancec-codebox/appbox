# IndexNode release notes

## 0.9.1

- **Every new download is classified first.** Download jobs from the watch stage (channels and playlists) and `enqueue` are created `idx_state=unclassified`, with `title`, `duration`, `channelId`, `channelName`, `availability` and `source` from the listing. No node claims them (claims and 0.9.0 stage batches match `pending` only; checked in IndexNode and Medusa).
- One singleton job, `index/jobs/classify/all.json`, created by whoever creates an unclassified job, decides up to 400 per run, oldest first: stored decision, then the under-20-minutes rule, then dead videos (private/deleted/unavailable, `kind: "unavailable"`, no AI call), then the AI in batches of 40 with the channel name in the prompt. Candidates become `pending`; the rest `failed` + `permanent` with `reason: "classify: <kind> <reason>"`. Due again at once if more remain, else after 10 minutes. It is not a step, so batches and hold blobs never touch it.
- The watch stage no longer creates per-channel classify jobs; existing ones run once.
- `indexnode classify set <videoId> drama|not [--reason R]` (owner override; a stored decision always wins), `classify list --rejected [--since 1d]`, `classify requeue --source <id>`; `status` shows unclassified, classified-today and rejected-today. `requeue` no longer resurrects classify rejections.
- Existing pending downloads are not reclassified unless `classify requeue --source` says so. Medusa's "Add to index" for one shared video still writes `pending`.

## 0.9.0

- **Stage batches.** A node takes a batch of dramas (up to `--batch N`, default 6, or about `--batch-hours H`, default 2, of video; best first: priority, then the oldest pending step's idx_added) and runs ONE STAGE AT A TIME across the whole batch: frames, sscd, thumbs, transcribe, ocrsubs, cast, sync (only this node's stages and pending steps). `--lanes N` (default 2) dramas run side by side inside a stage and share that stage's single loaded model; the next stage starts when the current one is done for every drama, and the stage's models are unloaded first, so one stage's models are in memory at a time. Intermediate data stays in a local batch work folder until the batch ends. As each stage finishes for a drama its outputs are uploaded and its step job marked done, so the store always shows each drama's finished stages; a crash resumes every drama from its last finished stage without redoing it. A failed stage is recorded as before, releases only the steps that need it, and the rest of the batch carries on. Downloads, comments, watch and the AI nodes are not part of batches (they run on `--workers`-free independent loops, one job at a time). `--workers`, `--jobs` and `--max-heavy` (and the config keys) are accepted but ignored, with a one-line warning.
- **Hold blobs (`index/holds/<videoId>.json`).** A batch holds a drama with one small blob, not a tag per step. The node creates it (`If-None-Match: *`) if missing, then takes a 60 s blob lease on it; the lease is the hold, renewed about every 40 s with the batch heartbeat. A blob whose lease has expired is taken over by acquiring the lease (atomic, one winner). Metadata `idx_node`, `idx_batch`, `idx_since`, `idx_until` (last renewed, informational) and tags `idx_hold_node`, `idx_hold_until` (search hints) are re-sent on every create and renewal. It is deleted under the lease on a clean stop or when the drama's last batch stage is done, so a dead node loses its holds within a minute and the folder stays tiny. Claimers list `index/holds/` with metadata (a consistent listing) and skip leased dramas; after taking a step's 60 s lease they re-check the job body (`finishedAt` set = already done) and the hold, so a stale tag query can never re-run a finished step. Fixes the Windows BatchTests race (`b:frames` run again). Also fixed: the plain workers now share their retry back-offs, so a job one worker just failed is not retried by the other in the same run (CI: Comments/Network tests).
- **Models are loaded once per process.** Every read-only model (SSCD, OCR detection and recognition, SCRFD/ArcFace/YuNet/SFace face models, CLIP/DINO vision, SenseVoice ASR) is loaded once and used by all workers: `OrtOptions.Shared` returns one ONNX Runtime `InferenceSession` per model file, device and thread count, and `Transcribe.SharedRecognizer` one SenseVoice recogniser. Thread-safety: ONNX Runtime sessions allow concurrent `Run` calls, so ONNX models need no lock and no per-worker session; each call builds its own tensors. The sherpa-onnx recogniser is called with a stream per decode (the session beneath it is the same ONNX Runtime one), also lock-free. The voice-detection (Silero VAD) detector is stateful, so each piece keeps its own small instance. Workers stay independent in everything else. Measured on the real models (SSCD int8, OCR det+rec, SCRFD, ArcFace, SenseVoice, one run each): 0.9 GB for one worker, 1.57 GB for two separate sets, 0.92 GB for two workers sharing.
- `indexnode control` usage lists gpu-hold and --max-per-ip N|off.
- `indexnode --version`.
- `indexnode run` also writes its log to a rolling file: %LOCALAPPDATA%\indexnode\logs\run-<node>.log on Windows, ~/.local/share/indexnode/logs/run-<node>.log on Linux (2 files of up to 20 MB; INDEXNODE_LOG_DIR overrides the folder).

## 0.8.8

- Nodes report their public IPv4/IPv6, IPv6 /64, country, colo, checkedAt and since in the registry.
- Download jobs and downloaded.json record the network of each attempt (success and failure).
- Optional control.json downloads.maxPerIp (default off) limits running downloads per IPv4 or IPv6 /64 across nodes; `indexnode control --max-per-ip N|off`.
- `indexnode status --json` includes the network fields.

## 0.8.7

- **Fix: no more requests for YouTube's machine-translated subtitle tracks.** The download passed `--sub-langs en.*,...` with `--write-auto-subs`; `en.*` matches every auto-translated track (en-ar, en-ja, ...), dozens of timedtext requests per video, and YouTube rate-limited the IP. Now the info JSON written by the main download is read first and yt-dlp gets exact keys only: uploaded = the Chinese keys present (zh, zh-Hans, zh-Hant, zh-CN, zh-TW, zh-HK) plus every `en` / `en-*` key in `subtitles`; automatic = only `*-orig` tracks of `automatic_captions` (zh-orig, en-orig). No wanted track, no call. A subtitle failure still never fails the download (only a bot block in the uploaded pass does).
- `--ai-session-id` / `x-session-id`: every AI call goes through `AiClient.PostAsync`, which sets the header whenever a session id is configured; tests now pin it for every call type (describe, spoiler check, rewrite, glossary, translate batches, classify; chat, responses, auto and the text fallback).

## 0.8.6

- GPU routing is opt-in. CPU nodes no longer hold sscd/clip/cast for 30 minutes waiting for a GPU node: they claim them at once, and the finished `frames` step writes no `gpu-ready` markers (the event-triggered GPU job has no other trigger, so it is no longer woken or billed by default).
- Switch: `indexnode control gpu-hold <duration>|off` stores `"gpu": {"hold": "30m"}` in `index/control.json` (absent = off) and takes effect on every CPU node without a restart. A node's `--gpu-fallback <duration>|off` overrides it. `--gpu` nodes are unchanged. After turning it on, `indexnode gpu markers` writes markers for jobs framed while it was off.

## 0.8.5

- Job priority: an integer (default 0, -1000..1000, higher first) in the blob tag `idx_priority` (missing or unparsable = 0). Nodes claim by priority descending, then `idx_added`, then name; waits for earlier steps and GPU routing are unchanged. Jobs a job creates inherit its priority (download -> step and comments jobs; watch/classify -> downloads).
- `enqueue --priority N`, `sources add|import --priority N`, `sources priority <source id|url> N` (re-tags the source's pending download, comments and step jobs; older jobs are matched by a yt-dlp listing, run on a machine YouTube allows), `priority set N --job|--from|--channel [--stage S] [--with-steps]` (pending only, under the lease; reports skipped), `priority list`, `status` counts, `sources list` shows the priority.
- A video in several sources gets the highest priority; an explicit `priority set` wins until a source priority of the video is changed again. Download jobs record `sources`. Medusa still claims in the old order.

## 0.8.4

- Spoiler rewrite no longer writes one character's text onto another (4 of 163 dramas had the female lead carrying the male lead's label/roleAtStart/description). The rewrite prompt now names the flagged cluster ids ("one entry for each of these clusters, with its own id: [1, 4]") instead of an example with id 0. Answers are applied only for flagged ids, each once; unflagged ids and repeats are ignored and logged; a flagged id with no answer keeps its original text.
- Identity guard: a rewritten entry is rejected (original label, roleAtStart and description kept) when its label names another cast entry's character (en, pinyin or zh) and not its own, or its roleAtStart / description equals another entry's. Relation labels such as "Pei Zimo's mother" pass.
- `describe.json` `spoilerCheck.log`: per round, the flagged ids, the answered ids, the rejected ones (with the reason) and the ignored ones.
- PromptVersion `describe-6`: redo `describe` for the affected dramas (hSNVVbkE6Vk, -HisxTigQLA, 0e2lNKJprjQ, 2MaOKBb0_eU).

## 0.8.3

- `frames` records the fixed black bars baked into the video (letterbox / pillarbox) as a box in `frames/manifest.json`; frames are stored unchanged. Rule: 24 frames between 5% and 95% of the duration; a row/column is a bar when at least 70% of its pixels are below 20 in at least 80% of the samples (subtitle strokes cover a minority of a row, so subtitle rows inside a bar count); a side pair is recorded when it covers at least 2% of the dimension and at least 20% remains; even size. `sscd` crops each stored frame by the box, then resizes to short side 288, so a 640x360 frame with 44+44 bars goes to the model as 678x288 (cropped 640x272, then resized). Thumbs, cast, `ocrsubs` and subtitles are unchanged.
- `frames/manifest.json` gains `cropRule` (3),
- Redo of an existing upload: only `indexnode process --job <id> --steps sscd`. When the manifest has no `cropRule`, sscd computes the box from the stored frames and writes it to `frames/manifest.json`; frames are not rerun, and no other step depends on the box (the frame size and count do not change).
- DINOv2 is not part of ingest (duplicates use SSCD): the opt-in `dino` step stays, but `processed.json` no longer waits for `dino`/`clip` outputs (a step-by-step upload without them was never marked processed), a download no longer queues `dino` and `clip` step jobs, and `unprocessed` no longer lists dino as missing.

## 0.8.2

- New opt-in step `dino` (not in the default or full steps; `process --job=<id> --steps dino`, or `stages run --video <id> --stages dino`): one DINOv2-small vector per stored frame, from the stored `frames/NNNN.mjpeg` chunks (Nexis ghosts hydrated as in 0.7.5), after sscd. Output `dino/NNNN.f16` (float16, 384 per row, same chunks and row order as `frames/`, CLS token, L2-normalised) and `dino/manifest.json` (written last: dim, model id and sha256, input size, preprocessing, chunk list).
- Model: onnx-community/dinov2-small `onnx/model.onnx` (Meta DINOv2, Apache-2.0), fetched like the other models, sha256 f22797ea...50c20a checked. `model_int8.onnx` is pinned too (`stages run --stages dino --int8`, for speed experiments only).
- Preprocessing as the reference (drama-index `fixtures/dino_rank.py`): RGB, bicubic resize to 224x392 (portrait) or 392x224 (landscape), /255, ImageNet mean/std. Frames are fully decoded and resized by ffmpeg (no -lowres), in slices of one model batch, up to `--threads` slices at once; downloads and uploads overlap with the model as in `thumbs`.
- Parity with onnxruntime + PIL bicubic on 20 real frames of each of 3 dramas: minimum cosine 0.9959 (mean 0.9995-0.9997).
- Hidden `dino-bench` command (fetches real chunks, runs the step's code on a few frames).

## 0.8.1

- `thumbs` is about 30-40x faster (a drama of 6-10k frames: 12-18 s instead of 450-690 s on a 4-core runner). One ffmpeg process per chunk reads the stored `frames/NNNN.mjpeg` as an mjpeg stream with `-lowres 3` (1/8-scale DCT decode), scales with `flags=area` and writes mjpeg; the output is split on SOI and must be exactly one standalone JPEG per row, else the step fails naming the chunk. Chunks are split into runs of whole frames, one ffmpeg each, up to `--threads` (default: cores).
- The standalone run pipelines: chunk n+1 downloads and chunk n-1 uploads while chunk n is processed; timings (download / thumbnailing us per frame / upload) are logged.
- Same files, sizes and 2 MB assertion as 0.8.0. `thumbs/manifest.json` gains `"decode": "scaled-1/8"` (`"full"` for the managed fallback used when ffmpeg is unavailable: full decode, frames in parallel, identical to 0.8.0 output).
- Thumbnails differ slightly from 0.8.0's (real chunks: mean abs 5.1-5.6 levels, max about 96, mostly edge pixels), same size or ~6% larger.
- Not used: LibJpeg.NET's classic scaled decode (scale_denom 2/4/8 returns blank images in this port).
- New hidden `thumbs-bench` command for timing and comparing the routes on local chunks.

## 0.8.0

- **New step `thumbs`: a 36x64 thumbnail of every stored frame.** `process --job=<id> --steps thumbs` reads the stored `frames/NNNN.mjpeg` chunks (ghosts are hydrated on read, 0.7.5; older AV1 chunks are decoded) and writes, per chunk, `thumbs/NNNN.mjpeg` (whole standalone JPEGs back to back, box/area downscale, quality 75, full tables in each, about 1.3 KB each; 64x36 for a landscape frame) and `thumbs/NNNN.json` (`[[off, len], ...]`, one pair per row, the same chunks and row order as `frames/` and `sscd/`; its own index, the frames JSON is untouched), then `thumbs/manifest.json` (size, quality, chunk list; written last). A chunk file must stay under Nexis's 2 MB ghosting size: the step fails with a clear error naming the chunk if one would not. Screenshot search re-ranks the copy matcher's top 10 with them (blurred copies); the UI shows frame previews from them without rehydrating ghosts.
- The step is in the default and full step lists (after sscd, before transcribe), `KnownSteps`, `processed.json`'s outputs and the node's step stages (needs `frames/manifest.json`); a redo of it replaces only `thumbs/`. `stages run --stages thumbs` runs it standalone too.

## 0.7.5

- **Reads hydrate Nexis ghosts.** Nexis ghosts every blob over 2 MB older than 20 min (0 bytes committed, `ghostd_state=ghost`, the data in uncommitted blocks). `RestStore` Get, conditional Get, file download and the new `GetRangeAsync` now check a 0-byte answer: a ghost is hydrated first (Nexis's `HydrateBlobAsync`: the uncommitted blocks with the `ghostd_block_prefix`, in name order, whose total must equal `ghostd_size`, else refused with nothing committed; Put Block List If-Match on the ETag, metadata kept; tags without `ghostd_*`, `archive_version=1`, `ghostd_state=active`; a 412 re-reads). One log line per hydration (`nexis: hydrated <blob>`, no SAS). A blob that really is empty comes back as it is. Fixes the 13 cast failures of 0.7.4 (empty `ocr/lines.json` ghosts).
- **Clear errors for empty stored data.** `StoredJson` parses stored JSON (ocr/lines.json, faces.json, clusters.json, manifests, info/downloaded/shots, cand_*.json): an empty or blank body fails with "stored blob <name> is empty (0 bytes) ..." instead of a bare JsonReaderException. An empty or short `cast/faces.f16` fails the same way (it gave a NullReferenceException in `CastLogic.Diverse`).
- **Fix: `CastLogic.Diverse` crashed (NRE) when a cluster had fewer usable faces than candidates**: the tiers repeat each other's faces, so the greedy pick ran out of unpicked faces (and could list a face twice). It now stops and never repeats.
- cast-candidates reads frame ranges through the store, so a ghosted chunk is hydrated first.

## 0.7.4

- **Fix: cast crashed with `'0' cannot be greater than -5.684341886080802E-14`** on 4 of 6 real dramas (360x640 frames). `CastStage.HeadWindow` shrinks the window to the frame with `cw *= min(fw / cw, ...)`; `cw * (fw / cw)` can land one ulp over the frame width, so the room `fw - cw` was -5.7e-14 and `Math.Clamp(v, 0, room)` throws. The window is now capped at the frame, and every window position goes through `CastStage.Within` (a room below 0 is 0); `Publish.TightWindow` uses it too.
- **Uncaught errors print their type and stack trace** (SAS signatures and cookies redacted) on stderr: the CLI's top-level handler, `process`'s per-job failure line and the stage runner's failed jobs.

## 0.7.3

- **Subtitles never rule out a candidate or portrait.** Only a subtitle that overlaps the face box counts against a frame (and only as a last tier). `CastLogic.Diverse` and publish's re-pick lose the "no subtitle" tier: they keep alone, then any frame. A band elsewhere on the frame is cropped out by the portrait and cover windows (they move or shrink above the band, never pad).
- **Pitch and eye visibility in the face quality.** `FaceMath.Pitch` (nose between the eye line and the mouth line, along the face's own vertical) and `FaceMath.EyeEnergy` (gradient energy in small patches around the eye landmarks) join `QualityOf` (pitch .15, eyes .10; size .28, detector .20, sharpness .17, yaw .10) and `Passes` (|pitch| at most .25). On Pei Zimo (24FQCFLd7K8) #4 and #7 now fail, #1 stays frontal.
- **cast stores the landmarks.** faces.json carries pitch, eyes and the 5 landmarks per face, cand_*.json pitch and eyes.
- **`indexnode cast-candidates --job <id> [--dry] [--write] [--out <dir>] [--clusters 1,2]`** rebuilds the 12 candidates of existing dramas from the stored faces, embeddings and frames (the detector runs again on the main characters' frames to get landmarks). Touches only cast's candidate files. Re-run describe afterwards: its expression scores are by candidate index.
## 0.7.2

- **Translate**: one unusable cue no longer fails a drama (the model's last non-empty answer is kept, else the cue stays untranslated; en.json has `unresolved` and `unresolvedCues`; more than 2% unresolved fails); a wholly unusable batch is retried split in halves, then quarters, and the model's actual reply is logged; a Latin word longer than a line is cut so shaping never leaves an over-long line (the "shaping left" check was right: it was the character limit, the message's first count was just 0).
- **AI JSON with repeated or junk keys** (`An item with the same key has already been added. Key: }`) parses, first wins, junk keys dropped.
- **Describe**: the spoiler rewrite answer may give text fields as objects or arrays (`The node must be of type 'JsonValue'`); read tolerantly. `describe --job <id> --out <file> [--raw-dir <dir>]` runs from the store and never writes it.
- **Publish**: a "not introduced" roleAtStart whose description says the same falls back to the label, then to a plain role line. `publish --dry-roles` counts them.

## 0.7.1

Everything since 0.7.0 (the 0.7 stages pipeline):

- **Publish needs no ffmpeg.** The 9:16 covers (360x640, centred on the face, same crop window as before) and the poster are cut in process: the stored JPEG is decoded, cropped, resampled with a Lanczos-3 filter and encoded at JPEG quality 85 (about ffmpeg's `-q:v 3`). Uses BitMiracle.LibJpeg.NET (BSD-3, managed, Windows and Linux). `Publish` never calls `tools.FfmpegAsync`.
- **A redo clears its own old outputs.** `process --job <id> --steps X` deletes the derived files of the steps it reran that the new run did not write again (old frame chunks, keyframes/, clip/, two-shot posters, an old ocr/subs.en.vtt, ...) — only after the step succeeded, so a failed redo keeps the old outputs. Never source files (video.mp4, info.json, description.txt, thumb.jpg, subs/, comments/, downloaded.json) and never another step's outputs. (subs/ai/ belongs to translate.)
- **Publish no longer reads cast/poster_cands.json**: two-person covers are gone; the poster is the two lead portraits side by side.
- **AI JSON as forced tool calls**, retried with the parse error passed back; replies parse their first complete JSON object.
- **classify, watch and availability**: channels and playlists are classified before download (ingest or check only); only the yt-dlp nodes touch YouTube; a daily availability sweep; admin source requests become watch jobs.
- **aiGenerated and aiFlag**: describe says whether a drama is AI-generated (no / partly / yes, with signs); publish carries it into the pack and the pack index; viewer comments that mention AI go to describe as a hint.
- **One-off download, `--no-process`, process by id, and `unprocessed`** (list of unprocessed, unqueued videos).
- **`--ai-session-id`** (INDEXNODE_AI_SESSION_ID): sent as x-session-id on every AI call.
- **The translate step**: our own English subtitles (glossary call, batched translation with context across batches, shaped, published as the default English track; existing English stays as the "original English" track).
- **A redo does not rewrite processed.json.**
- GPU publish step is one command again (line breaks instead of literal \n).

- **One person per portrait and cover**: a frame is used for a character's portrait or a lead cover only if no other detected face in it is larger than 25% of that character's face area (cast/faces.json, no cast rerun). A solo frame beats a crowded one even from a later window (up to the 75% limit). With no solo frame, today's choice is kept and cut tight so the other faces fall outside the crop.
- **Cast builds its 12 head-shot candidates from solo frames** (every detection of the frame counts; faces.json now has "other", the largest other face as a fraction of this face; cand_N.json has "solo"). Crowded frames only fill a gap. Publish uses cast's "solo" when present and works it out from faces.json for older dramas.
- **No burned-in subtitles in portraits and covers**: a frame with an OCR subtitle on screen (ocr/lines.json, cue times and line boxes, scaled to the frame) ranks behind frames without one, for the publish re-pick and for cast's 12 head-shot candidates (cand_N.json "sub" = the band box; faces.json "sub" flag). If only subtitled frames exist, the portrait or cover is cut so the band is outside it. Order of preference: solo and no subtitle, solo with the band cropped out, then any frame cropped tight.
- **Portrait by expression**: publish takes a character's portrait as the best candidate by describe's expression score among those that pass the rules (alone, then no burned-in subtitle; rules relax in that order when nothing passes). describe's own pick only breaks a tie. A best score under 6 is reported as a "warning:" line per character in the publish output. Lead covers keep the window rules.
