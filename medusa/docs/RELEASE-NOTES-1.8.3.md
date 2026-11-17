# Medusa 1.8.3

The Index node status headline now says what the node is actually doing.

- **One headline, one precedence:** Downloading (title, then video % / subtitles / uploading %) > Backing off until <time> \u2014 <reason> (with a **Clear back-off** button) > Waiting (not on Wi-Fi, not charging, battery low, storage low, outside the schedule window) > Downloads stopped by the owner > Daily cap reached (n/cap) / Budget spent > No downloads queued > Idle: next check in N min. The notification shows the same text. The details below no longer repeat the back-off line.
- **Live:** the screen refreshes about every 1.5 s from the worker's shared state, progress included.
- **History with times:** every recent job and the last run show local date/time and the Medusa version that ran them; entries recorded by an older version say "older version". Last run says what it did, e.g. "downloaded 2, failed 1, backing off".
- Tests for each headline state, the precedence between combinations, and the history formatting.
