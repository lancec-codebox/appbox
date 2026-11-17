# Medusa 1.8.5

- The index node reports its public IPv4/IPv6, IPv6 /64, country, colo, transport (wifi/cellular/ethernet) and VPN flag; refreshed on start, every 30 minutes, before a download claim and on a network change.
- Download job attempts and downloaded.json record the network used, on success and failure.
- Honors control.json downloads.maxPerIp: waits (shown as "Waiting: N downloads already running from this IP") instead of starting a download when other nodes on the same IPv4 or IPv6 /64 are at the cap. Off by default.
- Companion: IndexNode 0.8.8.
- Not verified on a phone or emulator.
