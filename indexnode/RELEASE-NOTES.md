# IndexNode release notes

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
