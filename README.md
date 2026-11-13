# appbox

Small open-source apps, in one repository: <https://github.com/lancec-codebox/appbox>.

| App | What it is |
| --- | --- |
| [Medusa](medusa/) | Android app that turns a video link into stone: it downloads the video and its subtitles on the phone with yt-dlp, plays it with adjustable subtitle sync, and serves both to web apps on the same phone (first client: LinguaWeave). |
| [Reveal current tab](tab-reveal/) | Brave/Chromium extension: one shortcut scrolls the current tab back into view in a long vertical tab strip. |
| [IndexNode](indexnode/) | Desktop node of the drama index, the counterpart of Medusa: downloads, frames, descriptors and AV1 compression over a shared blob container. Windows and Linux command-line tool. |

Each app lives in its own folder with its own README, build and release workflow. Releases: <https://github.com/lancec-codebox/appbox/releases> (tags `medusa-v*` for Medusa, `indexnode-v*` for IndexNode). Licence: see `LICENSE`.
