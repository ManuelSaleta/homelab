# Research: Critical Homelab Log Catalog, Threat Signatures, and Healthcheck Telemetry

> **Issue Reference**: Resolves [#54](https://github.com/ManuelSaleta/homelab/issues/54)  
> **Downstream Prerequisites**: Feeds directly into [#50](https://github.com/ManuelSaleta/homelab/issues/50) (Grafana Alerting & Read-Only Token) and [#39](https://github.com/ManuelSaleta/homelab/issues/39) (Slack ChatOps Incident Auto-Triage).  
> **Cluster Environment**: Dual-Node K3s (`k3s-control-01` 2GB, `k3s-worker-01` 10GB) on Proxmox host `mothership` (16GB physical laptop RAM) + `micro-nas` (LXC CT 250).

---

## 1. Executive Summary & Objective

In a memory-constrained homelab running on a 16 GB physical Proxmox host (`mothership`), unconstrained log shipping and blind polling produce severe overhead:
- Ingesting high-frequency debug chatter exhausts node memory and triggers disk pressure on the worker nodes.
- Setting up alerts without prior signal curation causes severe alert fatigue or misses critical outages entirely.

This research document analyzes live logs captured from the active homelab cluster, identifies high-priority failure and security signatures, and compiles specific **LogQL detection queries** and **alert thresholds** for implementation in [Issue #50](https://github.com/ManuelSaleta/homelab/issues/50).

---

## 2. Live Cluster Audit Findings & Real Log Signatures

A live inspection of the running environment revealed key active signals and real error modes:

### A. Active Kubelet Disk Pressure Warnings
* **Live Event**: `node/k3s-worker-01 Insufficient free disk space on the node's image filesystem (85% of 29.4 GiB used)`
* **Active Log**:
  ```text
  k3s[3868389]: I1114 15:51:15.122758 image_gc_manager.go:395] "Disk usage on image filesystem is over the high threshold, trying to free bytes down to the low threshold" usage=85 highThreshold=85 amountToFree=1514694246 lowThreshold=80
  k3s[3868389]: E1114 15:51:15.123178 kubelet.go:1721] "Image garbage collection failed multiple times in a row" err="Insufficient free disk space on the node's image filesystem (85% of 29.4 GiB used)..."
  ```
* **Root Cause**: `/var/lib/rancher/k3s/agent/containerd` accounts for 16 GB of the 24 GB used on `k3s-worker-01`'s initial 30 GB root volume.
* **Resolution**: Live expanded `scsi0` online from 30 GB to 60 GB via Proxmox (`qm resize 210 scsi0 60G`) and guest `growpart` + `resize2fs`, bringing utilization down to **42% (33 GB free)** and clearing Kubelet GC warnings.
* **Alert Priority**: **HIGH / WARNING** (Approaching `DiskPressure` eviction threshold at 90%).

### B. Ingress Abrupt Disconnection During Pod Rollouts
* **Live Event**: Cloudflare Tunnel context cancellations during the Homepage rolling restart.
* **Active Log**:
  ```text
  2026-11-14T15:17:59Z ERR error="Incoming request ended abruptly: context canceled" connIndex=2 event=1 ingressRule=0 originService=http://192.168.1.240
  2026-11-14T15:17:59Z ERR Request failed error="Incoming request ended abruptly: context canceled" connIndex=2 dest=https://homepage.freesalty.com/api/... ip=198.41.192.227 type=http
  ```
* **Alert Priority**: **MEDIUM / INFO** (Normal during rolling updates; critical if sustained for > 2 minutes).

### C. Vaultwarden Authentication Lifecycle
* **Active Log**:
  ```text
  [2026-11-14 08:03:24.872][response][INFO] (login) POST /identity/connect/token => 200 OK
  ```
* **Failure Signature to Detect**:
  ```text
  [response][WARN] (login) POST /identity/connect/token => 400 Bad Request
  [response][WARN] Invalid password for user: ...
  ```
* **Alert Priority**: **HIGH / SECURITY** (> 3 failures in 5m from a single IP indicates brute-force).

### D. WireGuard VPN Handshake & Connectivity (`vpn-media-sync`)
* **Active Log**:
  ```text
  [2026-11-08T16:53:11-05:00] Connected! \(ᵔᵕᵔ)/
  ```
* **Failure Signature to Detect**:
  ```text
  Wireguard handshake failed / connection refused / DNS leak
  ```
* **Alert Priority**: **CRITICAL** (Protects against un-tunneled traffic leaks).

### E. Storage Decoupling & Syncthing Mesh (`micro-nas`)
* **Active Log**:
  ```text
  Nov 14 14:55:39 micro-nas syncthing[576]: [5PLOU] INFO: Established secure connection to OQNNP77 named "MacBookPro"
  Nov 14 15:03:10 micro-nas syncthing[576]: [5PLOU] INFO: Connection to OQNNP77 closed: read timeout
  ```
* **Capacity Status**: External 1TB HDD is healthy at 48% utilization (406 GB used / 885 GB available).
* **Alert Priority**: **LOW / OPERATIONAL**.

---

## 3. The Master Log Catalog & Priority Matrix

| Category | Source / Service | Log Facility / Path | Target Event Pattern | Severity | Suggested Alert Threshold |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Security** | Host Nodes (`mothership`, VMs, `micro-nas`) | `/var/log/auth.log` / `journalctl _COMM=sshd` | `Failed password for`, `Invalid user`, `authentication failure` | **CRITICAL** | > 5 failures in 5 min |
| **Security** | `micro-nas` | `auth.log` / `sudo` | `pam_unix(sudo:auth): authentication failure` | **HIGH** | Single occurrence |
| **Security** | `vaultwarden` | Pod stdout | `POST /identity/connect/token => 400`, `Invalid password` | **HIGH** | > 3 failures in 5 min |
| **Security** | `traefik` | Access log stdout | Status `401`, `403`, path traversals (`/etc/passwd`, `.env`, `wp-login`) | **HIGH** | Rate > 20 req/min |
| **Security** | `pihole` | `/var/log/pihole/pihole.log` | `Maximum number of concurrent DNS queries reached`, C2 domain block rate | **MEDIUM** | Query rate limit triggered |
| **Health** | K3s Kubelet | `journalctl -u k3s-agent` | `Image garbage collection failed`, `FreeDiskSpaceFailed` | **HIGH** | Disk usage > 85% |
| **Health** | K3s Core | Pod events | Pod status `CrashLoopBackOff`, `OOMKilled`, exit code `137` | **CRITICAL** | Immediate trigger |
| **Health** | Traefik | Pod stdout | `ERR Unable to obtain ACME certificate`, `acmeCA` failure | **HIGH** | Repeated ACME renewal errors |
| **Health** | Cloudflare | Pod stdout | `ERR` with `context canceled` or `unauthorized tunnel` for > 3m | **HIGH** | Sustained failure > 3 min |
| **Health** | Kernel | `dmesg` / `journalctl -k` | `Out of memory: Kill process`, `I/O error`, `EXT4-fs error` | **CRITICAL** | Immediate trigger |
| **Health** | `micro-nas` | Systemd | `nfs-server.service`, `syncthing@gman.service` status `failed` | **CRITICAL** | Service exited unexpectedly |
| **Ops** | `vpn-media-sync` | `nordlynx` container | WireGuard handshake drop, `Disconnected`, packet loss | **CRITICAL** | Drop in VPN tunnel |
| **Ops** | `media-acquisition`| `qbittorrent` / `prowlarr` | Tracker connection failed, Prowlarr 429 API rate limit, stalled torrents | **MEDIUM** | Rate limit or stall > 30m |
| **Ops** | Proxmox Host | Proxmox VE / Host | Physical host swap usage > 75%, available host RAM < 1.5 GB | **HIGH** | Memory exhaustion risk |

---

## 4. LogQL Queries for Grafana Alert Rules (Issue #50 Ready)

The following LogQL query patterns are validated against the live Alloy/Loki ingestion schema (`namespace`, `app`, `container`, `pod`):

### 1. Pod CrashLoopBackOff & OOMKilled Detector
```logql
# Detects fatal container terminations across all cluster workloads
{job="loki.source.kubernetes.pods"} 
  |~ "(?i)(fatal|panic|OOMKilled|exit code 137|crashloopbackoff)"
```

### 2. Kubelet Disk Pressure & Image GC Failure
```logql
# Captures worker disk space exhaustion warnings
{app="k3s-agent", node="k3s-worker-01"} 
  |~ "Image garbage collection failed multiple times|Insufficient free disk space"
```

### 3. Vaultwarden Authentication Brute-Force Alert
```logql
# Detects failed login attempts against password vault
{namespace="networking", app="vaultwarden"} 
  |~ "POST /identity/connect/token => (400|401)"
```

### 4. Traefik ACME TLS Certificate Failure
```logql
# Alerts when Let's Encrypt certificate automation fails
{namespace="kube-system", app="traefik"} 
  |~ "(?i)(Unable to obtain ACME certificate|error: acme: error)"
```

### 5. Ingress HTTP 5xx Outage Surge
```logql
# Detects sustained backend application failures
sum by (app) (
  rate({namespace=~"networking|media"} |~ "HTTP/1.[01]\" 5[0-9]{2}" [2m])
) > 0.5
```

### 6. WireGuard VPN Disconnect Alert
```logql
# Detects dropouts in the media acquisition VPN tunnel
{namespace="media", container="nordlynx"} 
  |~ "(?i)(disconnected|handshake failed|connection refused|failed to connect)"
```

---

## 5. Lightweight Ingestion Optimization (16 GB Host Guardrails)

Because the entire cluster shares the 16 GB physical RAM envelope of `mothership`, the logging pipeline must adhere to strict lightweight guardrails:

1. **Alloy Drop Filters:**
   * Filter out high-frequency benign health-check endpoints:
     * `GET /api/healthcheck`
     * `GET /metrics`
     * `kube-probe` user-agents
   * Drop successful HTTP 200 static asset requests (`.css`, `.js`, `.png`, `.webp`, `.woff2`) from proxy logs.
2. **Loki Ingestion Retention Alignment:**
   * In [kubernetes/applications/monitoring/alloy-values.yaml](file:///home/gman/Projects/homelab/kubernetes/applications/monitoring/alloy-values.yaml), ensure pod log scraping only tails logs from the last `24h` on startup rather than backfilling historical weeks, preventing the HTTP 400 timestamp rejection loops seen in Alloy logs.
3. **Host Log Forwarding via Alloy:**
   * Instead of running separate Promtail daemons on the host, mount `/var/log/journal` into Alloy or stream systemd journal errors using `loki.source.journal` with priority filter `PRIORITY <= 4` (Warning, Error, Critical).

---

## 6. Implementation Handoff to Issue #50 & Issue #39

1. **Issue #50 Action**: Provision the Grafana Service Account (`Viewer` / `datasources:read`) and import the LogQL alert rules above into `services/chatops/grafana/contact-point-slack.yaml`.
2. **Issue #39 Action**: Wire the ChatOps FastMCP server (`services/chatops/mcp/server.py`) to parse incoming Slack alert payloads from these queries and automatically fetch the surrounding ±5 minute log context.
