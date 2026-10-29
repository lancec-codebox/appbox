# Media Helper

A small open-source Android app that fetches a timed transcript for a video link (YouTube, Bilibili, TikTok and the other sites yt-dlp supports) and hands it to a web app on the same phone.

Web apps wake it with a link and read the result from a local API; they never run yt-dlp themselves. The protocol is in [docs/PROTOCOL.md](docs/PROTOCOL.md). The first client is LinguaWeave, a language-learning app, which uses the Chinese transcript to teach the words in a show while you watch it.

Install: download the APK from this repository's Releases page and allow your browser or file manager to install apps. The helper updates its bundled yt-dlp by itself.
