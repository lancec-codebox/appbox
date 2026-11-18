# Medusa 1.10.0

**Queueing.** A download no longer says "fetching" while it only waits for another one (for example the index node's). Medusa's downloads, the /v2 runs and the index node now share one queue with a setting for how many go at once (Settings, "Downloads at once": heavy default 1, light default 2). A waiting /v1 job or /v2 run says it is queued, with its place and what it waits for. /v2 runs can ask for `priority` `interactive` or `background`; the index node works in the background.

New: a local capability API (`/v2`) so a native app, such as the IndexNode app for Android, can use Medusa's tools.

- Tools: yt-dlp, ffmpeg, ffprobe and the bundled Python run as queued, cancellable, time-limited runs (`/v2/runs`), with progress for yt-dlp and a streamed log. yt-dlp can be updated through the API.
- Workspaces: per-job folders with listing, Range downloads and uploads; paths cannot leave them; unused ones are deleted after 24 hours.
- Device: network type, VPN, metered, charging, battery and free space.
- Pairing: every `/v2` call needs a token. An app asks (an Android intent or `POST /v2/pair`), you tap Allow, the app gets a random token once. Settings lists the paired apps with Revoke.
- `/v1` (LinguaWeave) is unchanged.

See `docs/CAPABILITY-API.md`.
