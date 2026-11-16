# Homelab Slack ChatOps Gateway & Antigravity MCP Server

Zero-trust Slack Socket Mode ChatOps daemon and FastMCP server for interactive `media-sync` candidate approval ([Issue #39](https://github.com/ManuelSaleta/homelab/issues/39)), Plex library deduplication ([Issue #42](https://github.com/ManuelSaleta/homelab/issues/42)), and automated Grafana `#homelab-alerts` triage.

See [ADR 002: Zero-Trust Slack ChatOps Gateway & Antigravity MCP Server Integration](../../docs/adr/002-chatops-slack-mcp-gateway.md) for full architectural details.

---

## Directory Structure

```text
services/chatops/
├── .env.example                 # Environment variable template (copy to ~/.config/homelab-chatops/chatops.env)
├── pyproject.toml               # Python package & dependency definition
├── gateway/
│   ├── app.py                   # Slack Bolt Socket Mode daemon & command/action routers
│   ├── agent_bridge.py          # Antigravity CLI (google-antigravity SDK / agy CLI) bridge
│   ├── security.py              # Fail-closed USER_WHITELIST, rate limiter, RFC 1123 & HMAC guards
│   └── state.py                 # 24-hour TTL candidate batch store & Slack Block Kit builders
├── mcp/
│   └── server.py                # FastMCP server (Plex #42, media-sync #39, K3s & SSH tools)
├── grafana/
│   └── contact-point-slack.yaml # Grafana Unified Alerting Contact Point for #homelab-alerts
├── systemd/
│   └── chatops-gateway.service  # Fedora workstation systemd user service unit
└── tests/
    └── test_chatops.py          # Unit test suite (stdlib unittest)
```

---

## Quick Start (Fedora Workstation Operational Proxy)

### 1. Register MCP Servers with Antigravity CLI (`agy`)

```bash
# Register the official Slack MCP server
agy mcp add slack npx -y @modelcontextprotocol/server-slack

# Register the Homelab Remediator & Media-Sync FastMCP server
agy mcp add homelab-remediator python3 -m services.chatops.mcp.server
```

### 2. Configure Environment Secrets

```bash
mkdir -p ~/.config/homelab-chatops ~/.local/share/homelab-chatops
cp services/chatops/.env.example ~/.config/homelab-chatops/chatops.env
chmod 600 ~/.config/homelab-chatops/chatops.env
```

### 3. Enable the Systemd User Service

```bash
mkdir -p ~/.config/systemd/user
cp services/chatops/systemd/chatops-gateway.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now chatops-gateway.service
systemctl --user status chatops-gateway.service
```

### 4. Run Unit Tests

```bash
python3 -m unittest discover -s services/chatops/tests -v
```
