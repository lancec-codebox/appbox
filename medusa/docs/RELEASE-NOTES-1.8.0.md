# Medusa 1.8.0

The phone is a node, not an admin console.

- **Index node** and **Index node settings** are now their own entries in the main navigation. General Settings keeps only app-wide settings; stored node preferences are unchanged.
- **Index node** screen: status line, Run now, last runs and recent jobs, add a video, playlist or channel with an optional priority, "Added from this phone" (state from the store, change priority, remove), Open Drama admin (in your browser).
- The full list of the index's sources is gone (it is in the Drama admin web app).
- **Priorities** (-1000..1000) as in IndexNode 0.8.6: claim order is priority, then time added, then name; changing a priority re-tags pending jobs only, under the lease, keeping every tag.
- **Fix:** a finished download now creates the comments job and the per-step jobs (with their needs and the download's priority) instead of the old `process` job, which processing nodes no longer run.
- **Status notification** (silent, low importance, on by default while the node is on) and a **Run now** action; Settings warns when Android blocks notifications and asks again when you turn the node on.
- **One Scan QR code button** takes a container SAS URL, a medusa://node-setup link or the https hand-off link, and shows the usual confirmation.
