# Medusa 1.8.7

- Long-running notifications no longer use the animated system download icon. The "Medusa is ready" server notification, the "Index node" status notification (also while the node downloads; its progress text and bar stay) and the "Reading subtitles from the picture" notification use a static Medusa status icon (ic_stat_medusa, white on transparent, made from the launcher's monochrome layer).
- The animated icon stays only while a library download you started is running ("Downloading media"), then the static icon returns.
- Only one Index node notification is ever shown: the status notification is not posted while the node runs (network changes, Run now, status refresh and the store worker included), and is cancelled when a run starts, so the foreground notification is the only one during a run.
- Not checked on a phone: the icon's legibility at 24 dp in the status bar.
