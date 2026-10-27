# Homelab Docker Stack: Isolated NordVPN Media Acquisition Pipeline

This directory orchestrates the containerized media acquisition pipeline defined under `../containers/`:

- **[`../containers/nordlynx/compose.yaml`](../containers/nordlynx/compose.yaml):** Dedicated NordVPN WireGuard (`NordLynx`) container (`ghcr.io/bubuntux/nordlynx`) that owns the shared network namespace, enforces an `iptables` killswitch, disables IPv6, and allows LAN outbound routing (`NET_LOCAL=192.168.1.0/24`) for `rsync` to `micro-nas` (`192.168.1.250`).
- **[`../containers/prowlarr/compose.yaml`](../containers/prowlarr/compose.yaml):** Centralized indexer aggregator (`lscr.io/linuxserver/prowlarr:latest`) sharing the `nordlynx` network stack (`127.0.0.1:9696`).
- **[`../containers/qbittorrent/compose.yaml`](../containers/qbittorrent/compose.yaml):** Headless BitTorrent client (`lscr.io/linuxserver/qbittorrent:latest`) sharing the `nordlynx` network stack (`127.0.0.1:8080`) and staging downloads to local disk.
- **[`../containers/vpn-media-sync/compose.yaml`](../containers/vpn-media-sync/compose.yaml):** Custom `.NET 10` (`net10.0`) orchestrator container that verifies the VPN tunnel, queries Prowlarr (`/api/v1/search`), validates completed media via `ffprobe`, and performs restricted `rsync` (`rrsync -wo`) to `micro-nas`.

## Initial Setup (Unconfigured by Default)

1. Copy `.env.example` to `.env` and populate `NORDLYNX_PRIVATE_KEY`.
2. Copy `../containers/vpn-media-sync/config.example.toml` to `../containers/vpn-media-sync/config.toml` and configure your Prowlarr API key, qBittorrent password, and restricted SSH key path (`id_ed25519_mediasync`).
3. Validate or launch the stack:
   ```bash
   make config
   make up
   ```
