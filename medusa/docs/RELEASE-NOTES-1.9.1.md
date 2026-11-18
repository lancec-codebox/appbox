# Medusa 1.9.1

- **Download claim order fixed.** Azure's Find Blobs by Tags returns only the tags the query names, so the claim saw no `idx_priority` and no `idx_added`: every job looked like priority 0 with no date and downloads were taken in video-id order. The job search now runs the pending query three times (`+ idx_added >= ''`, `+ idx_priority > '0'`, `+ idx_priority < '0'`), merges the results by blob name and orders them by priority (numeric, descending), then `idx_added`, then name. The same goes for the lists behind priorities and added items (`findAll`).
- Pairs with IndexNode 0.10.2, which fixes the same thing for the desktop and batch nodes.
- Not checked on a phone.
