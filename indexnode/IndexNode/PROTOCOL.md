# Drama index: work protocol (v1, blob storage)

Nodes (Medusa on Android, and the desktop node) share work through one **Azure Blob Storage container**, reached by a **container SAS URL** that is entered into each node. No account name or key is ever built into an app. The SAS URL looks like:

`https://<account>.blob.core.windows.net/<container>?sv=...&sig=...`

Today that is the owner's Nexis drive container **media-wus2**: hexad variable `media_wus2_sas_url`, Key Vault backed. Everything lives under the prefix **`index/`**. Use `x-ms-version: 2021-12-02` or newer.

## Nexis ghosting applies

A Nexis dehydrate timer runs on this container every 30 minutes with `--min-dehydrate-size 2000000 --expiry 20m`. It turns any blob of 2 MB or more that hasn't been modified or touched for 20 minutes into a "ghost": the data is kept as uncommitted blocks, which is free.

The owner wants that for the index videos. So:

- **Small blobs (under 2 MB) are never ghosted:** job files, markers, the chunk indexes and manifests (`frames/NNNN.json`, `manifest.json`) and `info.json`. Read and write them normally.
- **Big blobs (`video.mp4`, `compact.mp4`, the `frames/` and `keyframes/` `.mp4` chunks, the `sscd/` and `clip/` `.f16` chunks) may be ghosted.** Nexis keeps its state in blob index tags: `state` (`active`, `transitioning` or `ghost`), `block_prefix`, `size`, `snapshot`, `last_access`, `last_refresh_time`. Before reading a big blob, a node must:
  1. **Touch it:** set the Nexis tag `last_access` to the current UTC time (Nexis `Timestamp` format), keeping the other tags. Do this again every 5 minutes while the blob is in use. Nexis skips a blob whose `last_access` is newer than its expiry.
  2. **Hydrate it if `state` is `ghost`:** commit its uncommitted blocks back. Reuse Nexis's own logic: `Q:\src\codebox\Nexis.Azure.Utilities` (`DehydrateOperation` with `requireHydrated`, `DriveOperationBase`). Don't reimplement it from memory.
- **Never write Nexis's tags** except `last_access`.
- **Index tags all start with `idx_`,** to stay clear of Nexis's tags. Azure allows at most 10 tags per blob.

## Jobs

One blob per job: `index/jobs/<stage>/<jobId>.json`. The stages are `download`, `process` and `comments`. The job's state is in its index tags:

- `idx_stage`: `download` or `process`
- `idx_state`: `pending`, `done` or `failed`
- `idx_added`: the time it was added, in sortable UTC
- `idx_due`: the earliest time it may run, in sortable UTC; for most jobs, the same as `idx_added`

`jobId` is the YouTube video id (11 characters), or a hash of the URL otherwise. `nodeId` is the node's name plus 6 random hex characters, fixed per install.

**Find work:** Find Blobs by Tags on the container (`?restype=container&comp=blobs&where=`), with ``"idx_stage"='download' AND "idx_state"='pending' AND "idx_due" <= '<now>'``. This needs the SAS permission `f`. If the SAS lacks it, fall back to listing `index/jobs/<stage>/` with `include=tags`. Take the oldest `idx_added` first.

**Claim:**
1. Acquire a **blob lease** of 60 s (`x-ms-lease-action: acquire`, `x-ms-lease-duration: 60`). A 409 means another node has the job; move on to the next.
2. Renew it every 30 s while working (`x-ms-lease-action: renew`).
3. If the node dies, the lease simply expires and the job is free again. No reaper is needed.
4. While working, keep a progress note in the blob's metadata, written with the lease id: `node`, `progress`, `heartbeat`.

**Finish:** write the outputs. Then rewrite the job blob with its lease id, adding `finishedAt`, `nodeId` and `elapsedSeconds`, and set `idx_state=done`. Then release the lease.

**Fail:** a node lets go of a job **immediately** when it finds it can't do it.
- **Permanent failures:** the video is removed, private, members-only, age-gated, region-blocked, or the URL isn't supported. Rewrite the job with `lastError` and `permanent: true`, set `idx_state=failed`, and release the lease. Don't retry.
- **Transient failures:** a network or 5xx error, a full disk, an upload error. Increment `attempts`, record `lastError` and release the lease at once. When `attempts` reaches 3, set `idx_state=failed`.
- **Bot blocks and rate limits** ("Sign in to confirm you're not a bot", HTTP 429): release at once, don't count the attempt, and back off as described under Nodes.

Rewriting a blob (Put Blob) drops its tags unless they're sent again in `x-ms-tags`. Always send the full `idx_*` set with each rewrite.

## Job descriptors

`download`:
```json
{ "v": 1, "stage": "download", "jobId": "dQw4w9WgXcQ", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  "maxHeight": 480, "format": null, "subs": true, "addedAt": "2026-11-13T19:00:00Z", "attempts": 0 }
```
- `maxHeight` defaults to 480. `format`, when set, is a raw yt-dlp format string that takes priority over `maxHeight`.
- The default format: **resolution first, then codec**. `-S "res:H" -f "bv*+ba[acodec=opus][abr<=80]/bv*+ba/b" --merge-output-format mp4`, with H = `maxHeight`. `res:H` takes the largest resolution ≤ H, where yt-dlp's `res` is the **smaller** dimension, so `maxHeight` 480 gives 854x480 landscape or 480x854 portrait (most of these dramas are portrait); yt-dlp's default codec order then prefers AV1 over VP9 over H.264 at that height, and its default `lang` ordering keeps the original-language audio. (The earlier codec-first selector, `bv*[height<=H][vcodec^=av01]+…`, picked 240p AV1 over 480p VP9 on 2026-11-13; don't filter by codec.) AV1 is roughly 40–60% the size of H.264 at the same height. `downloaded.json` records `vcodec`, `acodec` and the format ids.

`process`:
```json
{ "v": 1, "stage": "process", "jobId": "dQw4w9WgXcQ", "steps": ["frames", "sscd", "clip", "transcribe", "ocrsubs"],
  "compress": { "codec": "av1", "crf": 38, "preset": 8, "audioKbps": 48, "keepOriginal": false },
  "addedAt": "...", "attempts": 0 }
```
- `steps` run in that order. A step is skipped when its output already exists.
- `frames`, `sscd` and `transcribe` work on `video.mp4`, before `compress`.
- **Frame sets per step** (keyframe test, 2026-11-13, on a 2.5 h drama with 2,519 keyframes, one per 3.6 s on average):
  - `sscd` (copy detection) needs about 1 frame per second (~9,100 frames): keyframes alone found the right moment for 13 of 60 edited screenshots, against 53 of 60 for dense frames.
  - `clip` (search) embeds the same frames as `sscd` (already decoded; ~10.7k), and search results collapse to the best frame per shot. Keyframes alone miss short inserts, though they cover 97% of frames at SigLIP similarity 0.80 or more.
  - cast/faces uses keyframes only: all 12 main characters found at about a sixth of the cost.
  - `ocrsubs` decodes every frame (4 fps band).
  - Keyframes only: the AV1 decoder ignores `-skip_frame nokey`; drop non-key packets first (`-c copy -bsf:v noise=drop='not(key)'`), then decode (~15 s per drama).
- **`transcribe`:**
  - Extract the audio: 16 kHz mono via ffmpeg, kept only as a local temp file.
  - Run our own speech recognition, always, even when subtitles exist: it gives the timing, the text where there are no burned-in subtitles, and the only text for dramas without them.
  - **Dubbing:** many uploads are dubbed, usually into English, often badly, and some re-uploads carry only the dub. Record the spoken language the model detects (SenseVoice reports it per segment) in `asr/<model>.zh.json` as `language` (the majority) plus per-segment languages. A video whose speech is mostly not Chinese is marked `dubbed: true` and `speechLanguage` in `processed.json`; its burned-in Chinese subtitles, if any, still give the original text, and copy detection still groups it with the original. The viewer prefers an original-audio upload of the same drama when one exists.
  - Write `asr/<model>.zh.vtt` and `asr/<model>.zh.json`, the JSON holding segments with word or character timings and confidences.
  - The model is chosen per job: `"transcribe": { "model": "<id>" }`. Default: SenseVoice-Small (sherpa-onnx int8, Silero VAD); ~22 core-minutes per hour of drama, punctuation, no invented segments (2026-11-13 comparison with Paraformer and Whisper turbo). A node without the model, or too slow for it (for example Medusa), never claims a job that needs it.
- **`clip`:** general image embeddings for every sampled frame, the same frames as `sscd`, for semantic search ("a wedding in red", "a fight in the rain", "a woman in a silver mask") and for attributes.
  - Model: SigLIP 2 base (Apache-2.0, multilingual text tower, so Chinese and English queries both work), via ONNX, in float16.
  - Output: `clip.f16` (N×768, L2-normalised, where row i matches `frames.json[i]`) and `clip.json` (model id, input size, preprocessing).
  - Face crops get the same embedding when the cast step runs, used for the mask, hair and veil attributes.
  - Tested 2026-11-13 on 120 frames x 3 query types: SigLIP 2 beats CLIP B/32 everywhere and searches Chinese almost as well as English (CLIP can't). Judged by a vision model on 40 queries, SigLIP 2's top result fully fits 35% of the time, at least partly 67%, and 78% of queries have a full fit in the top 8 (CLIP: 15%, 60%, 62%). It finds what a scene looks like, not which of many look-alike scenes it is. Search results collapse to the best frame per shot.
- **`ocrsubs`:** reads subtitles burned into the picture.
  - Find the subtitle band: the region where text appears over and over across frames, usually the bottom fifth, sometimes the top.
  - Sample that band at 4 fps and run Chinese OCR on every sampled frame (no change gating: it lost 5-11% of short cues, and ungated is already ~44 frames/s per core). PP-OCRv5 mobile, which reads simplified and traditional in one model, through ONNX (for example RapidOCR) with detection at the band's native size (`Det.limit_type: max`, `Det.limit_side_len: 480`; RapidOCR's default `min 736` upscales the band ~21x in pixels and was 8x slower).
  - Merge runs of the same line into timed cues, and keep each line's confidence. Keep English lines separate when a video shows both.
  - Write:
    - `ocr/subs.zh.vtt`, exactly as read (often traditional Chinese);
    - `ocr/subs.zh-Hans.vtt`, converted with OpenCC (`tw2sp`/`t2s`; say which);
    - `ocr/lines.json`, with cue times, text as read, simplified text, confidence and boxes;
    - `ocr/subs.en.vtt`, when there are English lines.
  - **Text priority (owner decision, 2026-11-13): burned-in subtitles first.** Where an OCR'd Chinese subtitle line exists, it is the line's text; our transcript (`transcribe`) supplies the timing and the text for speech with no burned-in subtitle (about 13% of speech in the test drama), with its names corrected to the spellings the OCR found. Dramas without burned-in subtitles use the transcript alone. Uploaded subtitles stay supporting evidence.
  - Why: on the test drama the OCR was right on all 40 lines where it and the transcript disagreed most (25 sound-alike errors and 9 wrong words in the transcript, every character name wrong only in the transcript), at a tolerant CER of 12.6% between them. Low-confidence OCR lines can be checked by a vision model reading stacked line strips (3.6% CER vs OCR, catches some OCR misreads); one-minute frame grids read worse.
  - **Audio/video sync (`sync.json`)**, computed after `transcribe` and `ocrsubs`, no extra model: burned-in subtitles are timed to the original audio, so for each OCR cue of 4+ Chinese characters, find the transcript's matching characters within ±4 s and take (transcript time of the cue's first character − cue start). Write per-5-minute windows `{ "t", "offset" (median), "n", "iqr" }` plus `offset`, `drift` (max − min window median) and `suspect` (|offset| > 0.25 s or drift > 0.3 s). The test drama measured +0.04..+0.10 s in every 15-minute window (≈180 cues each, IQR about ±0.1 s), i.e. in sync. Without burned-in subtitles there is no measurement yet (a lip-sync model is a later option). Playback correction is deferred; meanwhile the viewer prefers an in-sync upload of the same drama (copy detection groups them).
- `compress` (optional, not in the default steps now that downloads are already AV1; for videos that came only as H.264) re-encodes to AV1 with SVT-AV1 (`libsvtav1`) at the given CRF and preset, plus Opus audio, into mp4.

`comments`:
```json
{ "v": 1, "stage": "comments", "jobId": "dQw4w9WgXcQ", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
  "maxComments": 1000, "sort": "top", "round": 0, "addedAt": "...", "attempts": 0 }
```
- yt-dlp `--write-comments --skip-download`, with `--extractor-args "youtube:max_comments=<N>,all,all,all;comment_sort=<sort>"`, no cookies. Each round takes the top comments, then a second pass sorted by `new`.
- Output: `index/videos/<jobId>/comments/<UTC yyyyMMddTHHmmssZ>.json`, a snapshot holding `{ "v": 1, "count", "commentCount" (YouTube's total), "comments": [{ "id", "parent", "author", "authorId", "text", "likes", "timestamp", "pinned", "byUploader" }] }`. Snapshots are never overwritten, so changes over time are kept.
- **No continuous watching.** One round at download, then the job is `done`. At most one follow-up, and only when the name vote leaves a **lead** unconfirmed: the AI step rewrites the job once with `round: 1`, `reason: "lead-names"`, `idx_state=pending` and `idx_due` = download + 7 days. After that round the job is `done` regardless. Supporting-cast names are never a reason to fetch comments (they come from other evidence, e.g. the same face as a named lead in another drama). Next parts and sequels come from the followed channel (watch stage), never from re-fetching comments.
- Comments mention cast names, other parts and sequels, and sentiment; later steps mine them for that.

`watch` (a followed source, polled on a schedule; the job never ends):
```json
{ "v": 1, "stage": "watch", "jobId": "pl-PLxxxx", "kind": "playlist"|"channel", "id": "<playlist id or channel id UC...>", "name": "...",
  "every": "1h", "tabs": ["videos", "shorts"], "followChannels": false, "maxHeight": 480, "addedAt": "...", "lastPolled": null, "attempts": 0 }
```
- Followed sources are the owner's chosen channels and playlists. **Watch Later is never a source**: it was a one-off seed (`index/seeds/`), used only to suggest channels.
- A node claims a due `watch` job like any other (`idx_due` ≤ now), polls, then rewrites it with `idx_state=pending`, `idx_due` = now + `every` (default 1 h) and `lastPolled`.
- **Polling uses yt-dlp's flat listing**, newest first, no cookies: `--flat-playlist --playlist-end 30 --print id` on the playlist URL, or on the channel's `/videos` and `/shorts` tabs (per `tabs`), with a JS runtime. Tested 2026-11-13 from the hexad host: listings work without cookies even while video pages ask it to sign in. **Everything YouTube goes through yt-dlp** (owner rule): never YouTube's official Data API, RSS feeds or other official endpoints, which don't cover what we need and are too restrictive. `backfill` uses the same listing with a larger `--playlist-end`.
- **New videos:** a video id with no `index/jobs/download/<id>.json` and no `index/videos/<id>/` gets a download job (the source's `maxHeight`, `"source": "<watch jobId>"`). Known ids are skipped, so a video in several sources is downloaded once.
- `followChannels: true` (the owner's public "drop" playlist): the channel of every video found is added as a followed `channel` source, if not already followed, with `backfill: 0` (new uploads only). The owner drops videos into that playlist to say "follow this channel".
- `backfill`: on the first poll of a newly added source, enqueue at most this many of its existing videos, newest first (default 0: only videos published after it was added).
- Sources are added and removed with `indexnode sources add <url> [--every 1h] [--backfill N] [--follow-channels]`, `sources list`, `sources remove <id>`; a removed source's job is deleted, its downloaded videos stay.

## Outputs: `index/videos/<jobId>/`

**Download stage:**
- Files:
  - `video.mp4`: a block blob, uploaded with Put Block and Put Block List in 8 MiB blocks.
  - `info.json`: yt-dlp's `--write-info-json`, without the comments.
  - `description.txt`: the video description, in plain text.
  - `thumb.jpg`.
  - Subtitles:
    - `subs/<lang>.vtt` for uploaded subtitles in English (`en*`) and Chinese (`zh`, `zh-Hans`, `zh-Hant`, `zh-CN`, `zh-TW`, `zh-HK`).
    - `subs/<lang>.auto.vtt` for YouTube's automatic captions in the same languages, when present.
    - Missing subtitles are not an error.
- Then, written last, `downloaded.json`: `{ "v": 1, "bytes", "height", "durationSeconds", "vcodec", "acodec", "formatId", "audioLanguage", "audioNote", "audioTracks": [{ "language", "note" }], "subs": ["en", "zh-Hans.auto", ...], "embeddable": true, "nodeId", "at" }`. The audio is the original-language track when YouTube offers several (dubbed tracks are listed in `audioTracks`, not downloaded). `embeddable` comes from yt-dlp's `playable_in_embed`; the viewer plays embeddable videos through YouTube's IFrame player with our own subtitles, and falls back to the stored copy otherwise.
- Then the node creates, for the same `jobId` and only where they don't exist yet:
  - the `process` job, with the default steps;
  - the first `comments` job, due at once.

**Process stage:**
- `frames/`, `keyframes/`, `sscd/`, `clip/` chunk sets (see above); before 0.5.0 `frames.json`, `sscd.f16`, `clip.f16`: `[{ "t": seconds, "kind": "scene"|"tick" }]`, with scene changes at `scene>0.3` plus a tick every 1 s. Extract in parallel segments, one ffmpeg process per physical core. A single ffmpeg uses only about 4 cores; segmenting made it 12.5× faster on a 16-core machine.
- `sscd/NNNN.f16` (was `sscd.f16`): N×512 float16, L2-normalised SSCD (`sscd_disc_mixup`) descriptors, where row i matches frame i.
- `compact.mp4` (from `compress`): its duration must be within 1 s of the original, and it must decode end to end. When `keepOriginal` is false, delete `video.mp4` after `processed.json` is written.
- `asr/<model>.zh.vtt` and `asr/<model>.zh.json`, from `transcribe`.
- Then, written last, `processed.json`: `{ "v": 1, "steps", "frames", "model", "asrModel", "speechLanguage", "dubbed", "ocrCues", "originalBytes", "compactBytes", "nodeId", "seconds", "at" }`.

A stage's marker (`downloaded.json`, `processed.json`) is the only signal that its outputs are complete.

## Cue shaping (no "bunching")

Every subtitle track we write or serve: our transcript, OCR, uploaded or automatic YouTube subtitles, and AI translations. Each goes through one shaping pass so the subtitles stay **incremental**: short cues that follow the speech, never one cue that piles up a paragraph over many seconds and then drops away. On the test drama, the transcript had 47 cues over 7 s (the longest 26 s and 85 characters); the burned-in OCR had none.

- **Limits:**
  - Duration: at most 6 s per cue (aim for about 1.5-4 s), at least 0.7 s.
  - Chinese: at most 18 characters per line and 2 lines.
  - English and other Latin scripts: at most 42 characters per line and 2 lines.
  - Cues never overlap.
- **Splitting a long cue:**
  1. Split at sentence punctuation (。！？…), then at clause punctuation (，、；：), then by the per-character or per-word timings (SenseVoice gives them; Whisper gives word times).
  2. Each piece takes the time of its first and last character.
  3. Without timings, share the cue's time out in proportion to the characters.
  4. Never cut inside a name from the drama's glossary.
- **Rolling YouTube auto-captions** (each cue repeats the previous line plus new words): keep only the new words per cue, then shape as above.
- **Gaps:** a cue may be held up to 0.5 s past its end for reading time, but never into the next cue. Silence longer than 1.5 s shows nothing.
- **Check:** every written track records `{ "cues", "over6s", "overChars", "overlaps" }` in its JSON sidecar, all 0 after shaping. A non-zero count is a bug.

## Other subtitle languages (later)

- Subtitles beyond Chinese and English are **AI translations made on demand**, for subscribers only, never ahead of time. The first request for a (drama, language) makes it; later viewers get the cached copy.
- **Translate from the Chinese text**, which is the burned-in OCR or else our transcript, not from the English subtitles, which are already a translation. Give the model the drama's glossary: character names with their chosen spellings, places and titles, so names come out the same in every line and every language.
- **Batches:** a few hundred cues per call with their neighbours for context, never one call per line. Keep cue times, and only change the text.
- **Cache:** `index/videos/<jobId>/subs/ai/<lang>.vtt` plus `<lang>.json` (model, glossary version, when, who asked). Redo it only when the Chinese text or the glossary changes.

## People and names (actors)

Actor names come from noisy evidence, and comments especially misspell them. So a person's name is **voted on**, never taken from one source.

- **A person** is a face identity across dramas: `index/people/<personId>.json` holds:
  - `faces`: a few ArcFace centroids, one per drama cluster, so the same actor in a new drama is matched by face similarity, same-person threshold 0.5;
  - `dramas`: `[{ "jobId", "cluster", "character", "role" }]`;
  - `names`: the candidate names with their votes.
- **Name candidates:** `{ "zh", "pinyin", "en", "votes": { "credits", "description", "title", "comments", "other" }, "examples": [ { "source", "jobId", "text" } ] }`.
  - **Where votes come from:**
    - credits OCR (主演, 饰…), the strongest;
    - the upload's description;
    - other uploads' titles;
    - comments ("the male lead is …", "X's acting…").
  - **Linking a mention to a person:**
    - A mention tied to a role (male lead, female lead, the villain) or a character name counts for the person who plays that role in that drama.
    - A mention tied to nothing counts only when the drama has a single plausible person (e.g. the comment names the lead's character).
- **Merging spellings before counting:** convert to simplified (OpenCC); then treat these as the same name:
  - names with the same pinyin (homophone typos: 张若男 / 张若楠);
  - names one character apart, when the other characters and the person agree;
  - romanisations equal after removing spaces, hyphens and case (Zhang Ruonan / Zhang Ruo Nan);
  - a known stage name or English name the sources pair with the Chinese one.
  
  Keep every spelling seen as an alias with its own count, so a common misspelling is still searchable.
- **Choosing the name:** each source counts with a weight: credits 5, description 3, other titles 2, a comment 1, and at most 1 per comment author per drama. The name with the highest weighted total wins when it has at least 3 points and leads the runner-up by 2× or more. Otherwise the person shows as unconfirmed, with the top two candidates.
  - Votes add up across every drama the person appears in, so a well-known actor converges quickly.
- **Extraction:** the AI step on the desktop node reads each drama's comments in batches, never one call per comment. It outputs `{ comment id, name as written, the role, character or "unknown" it refers to, quote }`, and the votes are counted in code, not by the model.
- Don't guess an actor from a face with a model: names come only from text evidence. Faces only link people across dramas.

## Cover and portrait expression rule

Every cover image (`cover/lead1.jpg`, `cover/lead2.jpg`) and every cast portrait must show a **flattering expression**: eyes open and roughly toward the camera, mouth closed or a slight smile. Reject mid-speech, shocked or startled, crying, grimacing, blinking and closed eyes.

- The batched vision call scores every candidate head shot and every poster-sheet frame 0-10 (`describe.json` `expression`: `{ "<clusterId>": [..], "poster": [..] }`, in sheet order). For a two-shot the score is the weaker of the two faces. The score is the decider; a candidate needs at least 6.
- The pick is the best score inside the allowed window, combined with the existing rules: the start window first, widened to the first half only when nothing there scores 6; never the last 25% of the video; masked faces never; watermarked frames never. Ties go to the model's own pick, then to candidate quality. When no candidate qualifies, a cast portrait keeps the cast stage's portrait and a lead cover or `together.jpg` is left out.
- Cast portraits choose among `cast/cand_hs_<cluster>_<k>.jpg` head-shot candidates the same way.

## Nodes

- **Medusa (Android):** runs `download` only. It can also add followed sources (below), which is a blob write, not processing.
- **Desktop node:** runs any of `download`, `process` and `comments`, chosen per run.
  - Its parallelism is settable: `--jobs process=N,download=M` for concurrent jobs per stage, each with its own lease, and `--threads N` for the CPU budget, shared out among the process jobs running at once. It also carries the tools: `enqueue` (URLs or a playlist, with `--max-height` and `--format`), `status` (counts per stage and state), and `requeue` (failed back to pending).
- **Desktop node:** runs `watch`, `process` and `comments` (and `download` when told to), and is the only node that uses AI (see AI credentials).
- **Adding sources from either app:** a playlist or channel URL given to Medusa (share it to Medusa, or paste it under Index node → Sources) or to `indexnode sources add <url>` becomes a `watch` job. The node that adds it doesn't have to poll it; whichever node runs `watch` does. `indexnode sources import <channels.json> [--every 1d]` adds many channels at once (used for the Watch Later seed).
- **Run limits** (both apps; set locally, or remotely per node in `control.json` → `nodes.<nodeId>`, which wins):
  - `budget`: how many more jobs (downloads, for Medusa) the node may finish; it counts down, and at 0 the node stops claiming and says so in the registry (`state: "budget spent"`). Unset = no limit. Medusa's existing daily cap stays as a separate limit.
  - `schedule`: a 5-field cron expression in the node's local time plus a `window` (e.g. `"0 1 * * *"`, `"3h"`): the node claims new jobs only inside a window that started at a cron time; jobs already claimed finish. Unset = any time (Medusa still applies its Wi-Fi/charging conditions).
  - Desktop flags: `--budget N`, `--schedule "<cron>" --window 3h` (the existing `--max-jobs` is a per-run limit and stays); `indexnode config set budget|schedule|window`. Medusa: the same three in the Index node settings.
- **AI credentials (desktop node only):** an OpenAI-compatible endpoint: `ai.baseUrl`, `ai.key`, and model names `ai.textModel` and `ai.visionModel`, and `ai.api` (`chat`, `responses` or `auto`, the default: chat, and a model that answers `unsupported_api_for_model` is retried once on the OpenAI Responses API and remembered for the run). Set with `indexnode config set ai.base-url|ai.key|ai.text-model|ai.vision-model|ai.api <value>` (stored in the user-only config file), or per run with `--ai-base-url`, `--ai-key`, `--ai-text-model`, `--ai-vision-model`, `--ai-api`, or the environment (`INDEXNODE_AI_BASE_URL`, `INDEXNODE_AI_KEY`, ...); flag > environment > config. The key is a secret like the SAS: never logged, redacted in output. On the hexad host the endpoint can be hexad's gateway (`http://localhost:5210/v1`, no key). Steps that need AI (later: merging subtitle text and transcript, naming the cast, checking doubtful OCR lines) are skipped, not failed, when no AI is configured, and `processed.json` lists them under `skipped`. AI calls are always batched, never one per line or frame.
- **Rate limits:** wait 20–40 s, random, between YouTube downloads. After a bot block or a 429, back off for an hour.
- **The SAS URL is a secret.** Store it in the app's private storage, Android Keystore-backed on the phone, or a user-only config file or environment variable on the desktop. Never log it; redact `sig=` in all output.

## Control and node registry

**Global control:** `index/control.json` is one small blob that every node obeys, downloaders and processors alike.

```json
{ "v": 1, "mode": "run", "stages": { "download": "run", "process": "run", "watch": "run" },
  "nodes": { "<nodeId>": { "budget": 50, "schedule": "0 1 * * *", "window": "3h" } },
  "message": "optional note", "changedAt": "2026-11-13T20:00:00Z", "changedBy": "owner" }
```

The `mode` field applies to all stages; `stages` overrides it per stage. Each takes one of three values:
- `run`: work normally.
- `drain`: finish the jobs already claimed, but claim nothing new.
- `stop`: stop at once. Kill the running yt-dlp and ffmpeg, release every lease **without counting an attempt**, clean up local temp files, and stay idle.

The effective setting for a stage is the stricter of `mode` and `stages.<stage>`. A missing `control.json` means `run`.

Nodes re-read it at least every 30 s, at each lease renewal, with a conditional GET (`If-None-Match` on the ETag). They also re-read it before claiming any job. A change takes effect within 30 s. While stopped or drained, a node keeps polling and resumes on its own when the file says `run` again.

Write it with `indexnode control run|drain|stop [--stage download|process] [--message ...]`.

**Node registry:** every node keeps `index/nodes/<nodeId>.json` up to date. It writes the file at start, every 60 s, and on any change:

```json
{ "v": 1, "nodeId": "lancecm1-a1b2c3", "name": "lancecm1", "kind": "desktop" | "medusa",
  "version": "0.1.0", "platform": "windows-x64", "stages": ["download", "process"],
  "jobs": { "download": 1, "process": 2 }, "threads": 16, "conditions": "wifi+charging",
  "state": "working" | "idle" | "draining" | "stopped" | "backoff" | "offline",
  "control": "run", "running": [ { "jobId": "...", "stage": "process", "step": "sscd", "progress": "42%", "startedAt": "2026-11-13T20:00:00Z" } ],
  "traffic": { "totals": { "youtubeIn": 0, "storageRead": 0, "storageWrite": 0, "aiOut": 0, "aiIn": 0, "otherIn": 0 },
               "hours": [ { "hour": "2026-11-14T06:00Z", "youtubeIn": 0, "storageRead": 0, "storageWrite": 0, "aiOut": 0, "aiIn": 0, "otherIn": 0 } ] },
  "lastSeen": "2026-11-13T20:00:30Z", "startedAt": "..." }
```

- `traffic` (0.5.1): bytes the node moved since it started, plus the last 48 hourly buckets (UTC hour start). youtubeIn = yt-dlp output files (video, subtitles, info, thumbnail, comments); storageRead/storageWrite = blob request and response bodies; aiOut/aiIn = AI request and response bodies; otherIn = models and tools. `status --json` adds lastHour and last24h sums per node.
- `firstSeen`: when the node first registered. A node's record is never deleted or reset: at start the node loads its previous `traffic` and `firstSeen` and keeps adding, and the record stays after the node stops, so its usage stays visible.
- `placement` (detected at start, refreshed daily): `{ provider: "azure"|"unknown", region, vmSize, zone, source: "imds"|"env", reason (when unknown: timeout, refused, 404), storageRttMs (median TCP connect over 5 tries to the storage host), sameRegionAsStorage, checkedAt }`. IMDS is `GET http://169.254.169.254/metadata/instance?api-version=2021-02-01` with `Metadata: true`, 2 s, no proxy. Container Apps has no IMDS: set `INDEXNODE_REGION`. The storage region is `storage.region` (default westus2); `sameRegionAsStorage` tells the admin page whether this node's storage reads are free or billed egress.
- `instance` is a per-process GUID of the last writer. A name given with `--node-name` / `INDEXNODE_NODE_NAME` is the node id itself. When a record's `lastSeen` is under 2 minutes old and its `instance` differs, a starting node warns, still runs, and `instances` (`{ guid: lastSeen }`) lists every live process.
- Pack covers (0.5.2): `cover/lead1.jpg` (female lead) and `cover/lead2.jpg` (male lead) only; there is no together cover. Separate 9:16 crops at 360x640, no text, no compositing, with `drama.json` `cover: { lead1, lead2 }`, each `{ file, t, window, cast, focus: [x, y] }`, where `focus` is the face centre as fractions of the cover image (0..1). The app shows them side by side as a 16:9 card, each panel an 8:9 crop centred on `focus`, with a thin gutter. A lead without a flattering shot is omitted. Lead shots are unmasked, picked by the vision expression score (see the expression rule); crops centre on the face box. Covers come from before the spoiler cutoff: the start window first, widened to the first half only when needed, never the last 25%.
- A node writes `state: "offline"` when it shuts down cleanly. Any node whose `lastSeen` is more than 5 minutes old counts as offline.
- `indexnode status` lists every node: its kind, the stages it handles, its parallelism, state, running jobs, and last seen. Then come the queue counts and the control state.
- The tag `idx_kind=node` on these blobs lets them be found by tag.

## Step jobs (0.5.0)

Processing is split into one job per step: `jobs/<step>/<jobId>.json` for `sscd`, `clip`, `transcribe`, `ocrsubs`, `cast`, `sync` and `describe`, with the same `idx_stage` (= the step), `idx_state`, `idx_added`, `idx_due` tags and the same leases, attempts and retries as every other job. Steps run in parallel on different workers and nodes, retry independently and show up per step in the status (`queue.<step>`, `--status-out`).

- **Creation:** the `download` stage creates all seven step jobs at once (only where they do not exist). A legacy `process` job still works: a node that claims it creates the missing step jobs and finishes it (compatibility fan-out; a `process` job with `compress` in `steps` still runs as before).
- **`needs`:** a job lists video files (relative to `videos/<id>/`) that must exist first: `sync` waits for `asr/sensevoice.zh.json` and `ocr/lines.json`; `describe` also for `cast/clusters.json`. A node that finds a needed file missing leaves the job `pending`, releases the lease and pushes `idx_due` 2 minutes ahead (no attempt is counted).
- **Frames are decoded once, by the `frames` step, and stored as AV1 chunks** (0.5.0). `sscd`, `clip` and `cast` (and the poster) decode the stored chunks with ffmpeg instead of the video; `sscd` and `clip` wait for `frames/manifest.json`, `cast` also for `keyframes/manifest.json` (`needs`). `ocrsubs` and `transcribe` still read the video; the cast credits OCR too. Every chunk blob is a big blob: touch and hydrate it before reading (Nexis), like `video.mp4`.
- **Frame chunks** (`frames/`): one chunk per 10 minutes of video (`NNNN` = floor(time / 600 s), four digits), 1 fps.
  - `frames/NNNN.mp4`: AV1 (SVT-AV1, preset 8, `-g 10`: a key frame every 10 frames, so one frame needs at most 10 decoded), yuv420p, 360 px short side (360x640 for portrait; the long side is even), CRF 22 by default (setting `frames.crf`; measured on the test drama: median SSCD cosine 0.976 and p1 0.912 against the original, about 6.4 MB per 10 minutes).
  - `frames/NNNN.json`: `{ "v": 1, "chunk", "start", "width", "height", "codec", "encoder", "preset", "crf", "gop", "fps", "frames": [{ "i", "t", "pts", "spans": [[start, end], ...], "rep" }] }`. `i` is the frame's number over the whole video, `pts` its position in the chunk file (seconds, 1 fps), `t` the video time. **Pruned frames are not stored in the first pass; their time goes into the spans of the kept frame they resemble.** A kept frame can stand for several separate moments, so `spans` is a list (touching spans are merged). `rep` is set (to the frame it stands for) on a frame the second, SSCD pass pruned: that frame stays stored (it was already embedded) but `sscd/keep.json` lists the rows to use and its spans moved to the `rep` frame.
  - `frames/manifest.json`: `{ "v": 1, "kind": "frames", "container": "mp4", "codec": "av1", "chunkSeconds": 600, "width", "height", "crf", "count", "chunks": [{ "chunk", "start", "frames", "bytes" }], "extra": { "sampled", "fps", "gop", "pruning": { "grid", "lookback", "kept", "of" } } }`. Written last: its existence means the set is complete.
- **Pruning (30 s look-back):** (1) before embedding, in the `frames` step: each 1 fps frame gets a colour-grid fingerprint (frame at 36x64 in Lab: an 8x4 grid of mean Lab values, plus a 128-bin hue 8 x chroma 4 x lightness 4 histogram per quadrant); a frame whose fingerprint distance (mean grid L1 in units of 20 Lab, averaged with the mean histogram L1) to a kept frame of the last 30 s is below `prune.grid` (default 0.08, about 19% of the frames on the test drama) is not stored, and its second joins that frame's spans. (2) after embedding, in the `sscd` step: a row whose SSCD cosine to a kept row of the last 30 s is at least `prune.sscd` (default 0.9) is marked pruned (`sscd/keep.json`: `{ "keep": [rows], "rep": [row each stands for] }`). Settings: `frames.crf`, `prune.grid`, `prune.sscd`, `prune.lookback` (`indexnode config set <key> <value>`).
- **Keyframe chunks** (`keyframes/NNNN.mp4`, `.json`, `manifest.json`): the decoded key frames of the video, in time order, at full size (480x854 for the test drama), **AV1 all-intra** (SVT-AV1 `-svtav1-params keyint=1`; `-g 1` is ignored by SVT-AV1 and writes a 161-frame GOP), so any one can be decoded on its own. The step checks with ffprobe that every packet is a key packet and fails otherwise. The index has `i`, `t` (video time) and `pts`, no spans.
- **Vector chunks:** `sscd/NNNN.f16` and `clip/NNNN.f16`: float16 rows, L2-normalised, row i matching `frames[i]` of `frames/NNNN.json` with the same chunk name (every stored frame has a row). `sscd/manifest.json` and `clip/manifest.json`: `{ "v": 1, "step", "dim", "dtype": "float16", "normalised": true, "frames", "info": { "model", "inputSize" ... }, "chunks": [{ "chunk", "start", "rows" }] }` (the clip manifest's `info` is the old `clip.json`); written last. The `sscd` model is the static QDQ int8 one (`sscd_disc_mixup_int8_static.onnx`, per-channel, calibrated at 288x512) when it is in the tools folder, else fp32; never dynamic quantization (11x slower on CPU). Re-embedding with a new model reads the frame chunks and writes new vector chunks; no video decode.
- **Publish** joins the vector chunks (and the frame times) into the site pack's `sscd.f16`, `clip.f16`, `frames.json` as before; jobs made by 0.4 (top-level `frames.json`, `sscd.f16`, `clip.f16`) are still read.
- **Storage measured** on the 2.5 h test drama (9,080 s): frames 7,059 of 9,080 sampled kept, **81.0 MB** (CRF 22, 16 chunks); keyframes 2,519, **42.3 MB** all-intra. The `frames` step took 220 s with 4 threads. The SSCD cosine of a stored frame against the original is median 0.978, p1 0.939 (300 frames, int8 model).
- **`processed.json`** is written by whichever node finishes the last step (every step's output exists; `describe.json` is not required on a node without an AI endpoint).
- **Node stages:** `--stages sscd,clip,transcribe,ocrsubs,cast,sync,describe`, or `--stages process` for all of them (and legacy `process` jobs). `--jobs sscd=1,clip=1,transcribe=2,ocrsubs=1` sets workers per step; `--threads` is shared by the running heavy steps. Control (`drain`/`stop`) of a step falls back to the `process` setting unless the step is set itself.
- A node's `steps` list (registry) also decides which step jobs it claims (a model step it lacks is never claimed).

## Picture sources (`stills`)

Every picture in a site pack (`cover/lead1.jpg`, `cover/lead2.jpg`, the cast portrait `cast/<id>.jpg`, each still in `cast[].stills[]`) records where it came from in a `source` object in drama.json, so it can be re-cut from a full-HD copy at the same moment and crop without re-choosing:

`{ "upload": "<video id>", "t": seconds, "crop": [x, y, w, h], "focus": [x, y] }`

- `crop` is the rectangle cut from the source frame, as fractions of the frame (0-1). A still is the whole frame: `[0, 0, 1, 1]`. A cover is its 9:16 window, a portrait its 4:5 head-and-shoulders window, as actually cut.
- `focus` is the face centre as fractions of the finished picture.
- `upload` is the upload the frame was taken from. When the drama group's HD copy is another upload, map `t` through `relations.json` (the offset between the two uploads) before cutting.
- Portraits where the stage's own `portrait_<id>.jpg` is the fallback have no `source` yet.
