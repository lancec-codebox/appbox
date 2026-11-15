# IndexNode release notes

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
