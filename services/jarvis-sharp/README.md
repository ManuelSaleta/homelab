# 🤖 Jarvis.Sharp (`jarvis-sharp`)

**Jarvis.Sharp** is the high-performance, containerized .NET implementation of the Homelab Slack Socket Mode Gateway, FastMCP diagnostics bridge, and reactive alerting coordinator.

It runs alongside the homelab stack, maintaining zero open inbound router ports via an outbound persistent WebSocket to Slack's Edge API.

---

## 🎯 Architecture

```text
[ Slack Edge API ] <══(Outbound WSS / Socket Mode)══> [ Jarvis.Sharp Container ]
                                                       │
        ┌──────────────────────────────────────────────┴──────────────────────────────┐
        ▼                                              ▼                              ▼
 [ Media-Sync Webhook ]                      [ K3s Kubernetes API ]          [ Grafana / Loki ]
  • HMAC-SHA256 Signing                       • Pod Logs & Descriptions       • Read-Only LogQL
  • Plex REST Library Check                   • Safe Rollout Restarts         • Alerting Webhooks
```

---

## 🛡️ Core Features
1. **Zero Open Router Ports**: Connects exclusively via Slack Socket Mode (`wss://wss-primary.slack.com`).
2. **Fail-Closed Authorization**: Enforces strict `USER_WHITELIST` check on every incoming command, slash action, or button interaction.
3. **Plex Pre-Acquisition Check**: Deduplicates candidate titles against Plex before triggering downloads.
4. **Inter-Service Security**: Dispatches HMAC-SHA256 signed requests to `media-sync` (`Sync.Sharp`).
5. **Reactive Alert Triage**: Receives Grafana Unified Alerting notifications and renders interactive remediation buttons in Slack threads (`[🔄 Restart Deployment]`, `[📜 View Full Logs]`).
6. **Minimal Footprint**: Runs in a rootless, distroless chiseled container consuming under ~40 MB RAM.

---

## 🔧 Environment Variables

| Variable | Description | Default / Example |
| :--- | :--- | :--- |
| `SLACK_APP_TOKEN` | Slack App-level token (`xapp-...`) with `connections:write` | Required |
| `SLACK_BOT_TOKEN` | Slack Bot OAuth token (`xoxb-...`) with `chat:write` | Required |
| `USER_WHITELIST` | Comma-separated list of authorized Slack User IDs | Required (Fail-closed) |
| `MEDIA_SYNC_WEBHOOK_URL`| In-cluster webhook URL for `media-sync` | `http://sync.freesalty.com/api/v1/sync/trigger` |
| `WEBHOOK_SECRET_TOKEN` | Shared 32-byte secret for HMAC-SHA256 signing | Required |
| `GRAFANA_URL` | Base URL for Grafana Ingress | `https://grafana.freesalty.com` |
| `GRAFANA_TOKEN` | Read-only Grafana Service Account Bearer Token | Required |
| `GRAFANA_LOKI_DATASOURCE_UID`| Loki datasource UID in Grafana | `loki` |
| `PLEX_URL` | Local Plex Media Server endpoint | `http://192.168.1.210:32400` |
| `PLEX_TOKEN` | Plex API authentication token | Optional |

---

## 🚀 Building & Testing

### Local Development
```bash
# Build solution
dotnet build Jarvis.Sharp.slnx

# Run unit tests
dotnet test Jarvis.Sharp.slnx

# Run worker locally
dotnet run --project src/Jarvis.Sharp/Jarvis.Sharp.csproj
```

### Docker Container Build
```bash
docker build -t ghcr.io/manuelsaleta/jarvis-sharp:latest -f Dockerfile .
```

---

## 📦 Standalone Repository Roadmap

* **Target Repository**: `https://github.com/ManuelSaleta/jarvis-sharp`
* In accordance with Issue [#57](https://github.com/ManuelSaleta/homelab/issues/57), the Python prototype in `services/chatops/` remains active in the `homelab` repository until this container is deployed and verified in K3s.
