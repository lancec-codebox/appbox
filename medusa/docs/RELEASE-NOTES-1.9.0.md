# Medusa 1.9.0

- **Index downloads are written as one zip.** A node download and a library "store to index" write `video.mp4` and `download.zip` (info.json, description.txt, thumb.jpg, subs/ as stored zip members) instead of loose files, with the same blob metadata as IndexNode 0.10.0's download stage (idx_v, idx_sha256, idx_inputs, idx_node, idx_at, idx_summary). The former downloaded.json is the zip's idx_summary.
- **Ordering.** video.mp4 first, download.zip last: the zip is the completion signal, so a half-written drama is never taken for complete. Store-to-index creates the zip with If-None-Match: * (409/412: already in the index, nothing replaced).
- Needs IndexNode 0.10.0 nodes and a repacked index (indexnode repack): update the phone after the repack, as described in INDEX-NODE.md.
- Not checked on a phone.
