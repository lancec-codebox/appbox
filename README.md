# Media Helper

A small open-source Android app that fetches a timed transcript for a video link (YouTube, Bilibili, TikTok and the other sites yt-dlp supports) and hands it to a web app on the same phone.

Web apps wake it with a link and read the result from a local API; they never run yt-dlp themselves. The protocol is in [docs/PROTOCOL.md](docs/PROTOCOL.md). The first client is LinguaWeave, a language-learning app, which uses the Chinese transcript to teach the words in a show while you watch it.

Install: download the APK from this repository's Releases page and allow your browser or file manager to install apps. The helper updates its bundled yt-dlp by itself.

## Building

```
./gradlew :app:testDebugUnitTest :app:assembleDebug   # APK: app/build/outputs/apk/debug/media-helper-<version>-debug.apk
./gradlew :app:connectedDebugAndroidTest              # one smoke test of the local server, needs an emulator or phone
```

JDK 17, Android SDK 36. Releases: push a tag `vX.Y.Z`; the workflow builds `media-helper-X.Y.Z.apk`, signed with the key from the repository secrets (see `docs/RELEASE.md`), and attaches it to a GitHub Release.

## How it works

`JobActivity` takes the `ref12media://open` link, `HelperService` (foreground) runs the NanoHTTPD server on `127.0.0.1:38917`, `JobManager` runs one job at a time through `YtDlpFetcher` (youtubedl-android: list tracks with `--dump-single-json`, pick one with `TrackSelector`, download only that subtitle file, convert with `SubtitleConverter`). Results are cached 30 days in app storage.

Track choice: first requested language among manual captions, then auto-captions. For auto-captions YouTube lists machine translations under every language; when the video marks its spoken language (`xx-orig`) only that track counts, so an English video is not returned as "Chinese" machine translation.

yt-dlp: checked for an update when a job starts and the last check is over a day old; on a yt-dlp failure it is updated once and the job retried. The library bundles QuickJS and passes `--js-runtimes quickjs:...` itself, so YouTube's JS challenges are covered.
