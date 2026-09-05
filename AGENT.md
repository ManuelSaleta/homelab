# 🛸 Mothership Homelab: AI Workspace & Architectural Guidelines
<!-- agent: Any agent is allowed. Treat this file as your Architectural Decision Record (ADR) and prime directive. -->

This file serves as the system instruction context and prime directive for AI collaborators working within the `Mothership` IaC homelab repository. It outlines project conventions, architectural state boundaries, safety constraints, storage topology, and deployment patterns.

---

## 1. Repository Philosophy & Core Principles

1. **Zero-Intervention Pipeline**: IaC manifests must favor automated provisioners (`Packer` -> `Terraform` -> `K3s`) over manual runtime steps.
2. **Cluster & Storage Decoupling**: Compute is transient; persistent data is static. Sensitive and stateful data lives **outside** the K3s cluster lifecycle on dedicated infrastructure (`micro-nas` LXC container `CT 250` at `192.168.50.250`).
3. **NFS Over Local Storage**: Stateful workloads in K3s must consume remote NFS exports from `micro-nas` rather than `hostPath` or local volumes to guarantee cross-node workload mobility and zero data loss on cluster teardown.
4. **Strict Namespace Isolation**: Workloads must be isolated by functional domain:
   - `media`: Plex Media Server, Navidrome Music Server.
   - `networking`: Pi-hole DNS, Cloudflared Tunnel, Homepage Dashboard, Karakeep, Uptime Kuma, Vaultwarden.
   - `monitoring`: Kube-Prometheus-Stack, Grafana, Loki, Alloy.
5. **Dynamic Domain Parameterization**: **Never hardcode static root domains** (e.g., `example.com`, `freesalty.com`) into Kubernetes manifests or ingress rules. Always use `${DOMAIN_NAME}` or `${HOMELAB_DOMAIN}`, which the root `Makefile` dynamically populates via `envsubst` from `terraform.tfvars`.
6. **Strict Resource Budgeting**: The physical Proxmox host (`mothership`) has **16 GB of physical RAM**. Memory allocations must be calculated conservatively (~9.4 GB host headroom reserved for ZFS ARC, Linux page cache, and hypervisor overhead).

---

## 2. Architectural Topology & Fleet Profiles

| Node Name | Type & ID | IP Address | vCPU / RAM | Role & Scope |
| :--- | :--- | :--- | :--- | :--- |
| **mothership** | Physical Host | `192.168.50.200` | Physical (16 GB) | Proxmox VE Hypervisor, NVMe (`local-lvm`) + 1TB HDD (`sda`) |
| **k3s-control-01** | VM `100` | `192.168.50.185` | 2 vCPUs / 3 GB RAM | K3s Manager, API Server (`:6443`), CoreDNS, Traefik Ingress |
| **k3s-worker-01** | VM `210` | `192.168.50.210` | 2 vCPUs / 3 GB RAM | K3s Worker Node, Application compute workloads |
| **micro-nas** | LXC `CT 250` | `192.168.50.250` | 1 Core / 512 MB RAM | Dedicated Storage Node: NFS Kernel Server, Syncthing, Tailscale |

> [!NOTE]
> **Base OS Standards**: All VM compute nodes are cloned from golden Packer template **ID 777** running **Ubuntu 26.04.1 LTS** (`ubuntu-26.04.1-live-server-amd64.iso`). The `micro-nas` LXC uses container template `ubuntu-26.04-standard_26.04-1_amd64.tar.zst`.

---

## 3. Directory Anatomy & Context Mapping

```text
.
├── AGENT.md                       # AI architectural guidelines & prime directive (this file)
├── README.md                      # Human-facing master workspace documentation
├── Makefile                       # Centralized build, apply, destroy & lifecycle automation
├── loki.yaml                      # Loki logging configuration reference
├── docker/
│   └── container-provisioning/    # Standalone Docker container orchestration (future/edge)
├── kubernetes/
│   ├── infrastructure/            # Core L2/L3 networking & ingress controllers
│   │   ├── metallb-config.yaml    # MetalLB IPAddressPool (192.168.50.240-250) & L2Advertisement
│   │   ├── traefik-dns-config.yaml # Traefik HelmChartConfig (Cloudflare DNS ACME / TLS)
│   │   └── cloudflared-config.yaml # Cloudflare Zero Trust tunnel deployment
│   └── applications/              # Workload manifests grouped by application folder
│       ├── homepage/              # Homepage dashboard & RBAC auto-discovery
│       ├── karakeep/              # Karakeep bookmark manager, Meilisearch, Browserless
│       ├── monitoring/            # Prometheus Stack, Loki, Alloy Helm values
│       ├── navidrome/             # Navidrome music server & NFS volume claims
│       ├── pihole/                # Pi-hole DNS server & local dnsmasq overrides
│       ├── plex/                  # Plex media server, NFS volume claims, emptyDir transcode
│       ├── uptime-kuma/           # Uptime Kuma status monitor
│       └── vaultwarden/           # Vaultwarden password vault & NFS volume claims
└── terraform/
    └── vm_provisioning/
        ├── main.tf                # Proxmox & K8s providers, k3s-control-01 VM (ID 100)
        ├── workers.tf             # k3s-worker VM definitions (ID 210) & cloud-config
        ├── micro-nas.tf           # micro-nas LXC container (CT 250) & NFS export setup
        ├── secrets.tf             # K8s secrets & configmaps injected via Terraform
        ├── variables.tf           # Variable declarations (tokens, network matrix, domain)
        ├── terraform.tfvars.example # Sanitized template for environment configuration
        └── packer-k3s/            # Golden image Packer build (Ubuntu 26.04.1 LTS, ID 777)
```

---

## 4. Security, Secrets & State Immutability

1. **Zero Credential Commits**: Never generate, hardcode, or commit `.tfvars` files, private keys (`id_ed25519`), Cloudflare tokens, Proxmox tokens, or Tailscale auth keys.
2. **Secret Lifecycle Pipeline**: Secrets are declared in local `terraform.tfvars` -> injected into Kubernetes via Terraform `kubernetes_secret_v1` (`secrets.tf`) -> consumed securely by application pods via `secretKeyRef` or Secret volume projections.
3. **PersistentVolume Immutability**: Kubernetes PersistentVolume sources (e.g., modifying NFS paths or server IPs) cannot be updated in-place. If changing PV/PVC definitions, always instruct deletion (`kubectl delete pvc,pv`) followed by re-creation.
4. **Container Non-Root Context**: Stateful application containers must enforce standard non-root UID/GID `1000:1000`:
   ```yaml
   securityContext:
     runAsUser: 1000
     runAsGroup: 1000
     fsGroup: 1000
   ```
5. **NFS Root Squashing & Network Isolation**: All NFS shares on `micro-nas` enforce `all_squash,anonuid=1000,anongid=1000` and restrict access strictly to the K3s node IPs (`192.168.50.185` and `192.168.50.210`).
6. **Syncthing Network Isolation**: Syncthing GUI administration is strictly bound to the host's private Tailscale IP (`[TAILSCALE-IP]:8384`), keeping port 8384 closed on the physical LAN.

---

## 5. Storage Architecture & Decoupling ("What Lives Where")

Persistent data is mounted under `/mnt/export/storage` on `micro-nas` (`192.168.50.250`). As long as `micro-nas` (CT 250) remains intact, the entire K3s cluster can be destroyed and rebuilt with **zero data loss**.

| Application | Micro-NAS Target Path | PV Name | PVC Name | Access Mode | Description |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Vaultwarden** | `/mnt/export/storage/vaultwarden` | `vaultwarden-nas-pv` | `vaultwarden-nas-pvc` | `ReadWriteOnce` | SQLite database (`vaultwarden.db`), RSA keys, attachments |
| **Navidrome (Data)** | `/mnt/export/storage/navidrome/data` | `navidrome-config-pv` | `navidrome-config-pvc` | `ReadWriteOnce` | SQLite database (`navidrome.db`), cache, player states |
| **Navidrome (Music)** | `/mnt/export/storage/navidrome/music` | `navidrome-music-pv` | `navidrome-music-pvc` | `ReadOnlyMany` | Audio library files (MP3, FLAC, AAC) |
| **Plex (Config & DB)** | `/mnt/export/storage/plex/config` | `plex-config-pv` | `plex-config-pvc` | `ReadWriteOnce` | Server metadata, preferences, media database (20Gi) |
| **Plex (Media)** | `/mnt/export/storage/plex/media` | `plex-media-pv` | `plex-media-pvc` | `ReadOnlyMany` | Video library (500Gi, read-only to prevent deletion) |
| **Obsidian Vault** | `/mnt/export/storage/obsidian` | *N/A (Syncthing)* | *N/A* | User Space | Markdown notes synchronized via Syncthing over Tailscale |

### Ephemeral Scratch Spaces
- **Plex Transcoding**: Transcoding must **never** write to NFS shares (to avoid saturating network I/O). Always mount a dedicated ephemeral `emptyDir: {}` volume to `/transcode`.

### Non-NFS Exceptions
- **Pi-hole**: Uses local node `hostPath: /var/data/pihole/config` (tied to worker node).
- **Uptime Kuma**: Uses standard single-pod PVC (`uptime-kuma-pvc`) with deployment strategy `Recreate`.

> [!IMPORTANT]
> **Manual Micro-NAS Maintenance**: `micro-nas` (CT 250) is stateful and decoupled from cluster teardowns. Because cloud-init only runs on container creation, adding new persistent directories, updating ownership/permissions (`chown -R 1000:1000` / `chmod`), or adjusting `/etc/exports` on an existing `micro-nas` instance must be executed manually over SSH (`gman@192.168.50.250`).


---

## 6. Kubernetes Manifest Standards & Blueprints

When authoring or modifying Kubernetes manifests in this repository, strictly adhere to these patterns:

### Rule 1: Dynamic Domain Substitution
Ingress hosts and environment variables must reference `${DOMAIN_NAME}` or `${HOMELAB_DOMAIN}`:
```yaml
spec:
  rules:
    - host: app.${DOMAIN_NAME}
```

### Rule 2: NFS PersistentVolume & PersistentVolumeClaim Blueprint
Always set `storageClassName: ""` to prevent default dynamic provisioner conflicts:
```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: <app>-data-pv
  labels:
    type: nfs
spec:
  capacity:
    storage: <size>
  volumeMode: Filesystem
  accessModes:
    - ReadWriteOnce # or ReadOnlyMany / ReadWriteMany
  persistentVolumeReclaimPolicy: Retain
  storageClassName: ""
  nfs:
    server: 192.168.50.250
    path: /mnt/export/storage/<target-folder>
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: <app>-data-pvc
  namespace: <namespace>
spec:
  accessModes:
    - ReadWriteOnce
  storageClassName: ""
  resources:
    requests:
      storage: <size>
  volumeName: <app>-data-pv
```

### Rule 3: Ingress & Homepage Dashboard Integration
All Ingress manifests must include `gethomepage.dev` annotations for auto-discovery and Traefik routing entrypoints:
```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: <app>-ingress
  namespace: <namespace>
  annotations:
    gethomepage.dev/enabled: "true"
    gethomepage.dev/name: "<AppName>"
    gethomepage.dev/group: "<Category>" # e.g. Media, Networking, Security, Monitoring
    gethomepage.dev/icon: "<icon-name>"
    gethomepage.dev/href: "https://<app>.${DOMAIN_NAME}/"
    traefik.ingress.kubernetes.io/router.entrypoints: web, websecure
spec:
  ingressClassName: traefik
  rules:
    - host: <app>.${DOMAIN_NAME}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: <app>-service
                port:
                  number: <port>
```

---

## 7. Proxmox Hypervisor Host Maintenance Rules

When generating instructions or scripts for maintaining the Proxmox VE hypervisor host (`root@192.168.50.200`):

> [!CAUTION]
> **Always use `apt-get dist-upgrade` (or `apt full-upgrade`) on Proxmox VE.**
> **NEVER run `apt upgrade`**. Standard upgrades will fail to install new kernel transitions or Proxmox meta-packages (`proxmox-ve`, `pve-manager`, `qemu-server`), which can corrupt the hypervisor package state.

Safe upgrade command sequence:
```bash
DEBIAN_FRONTEND=noninteractive apt-get update && \
DEBIAN_FRONTEND=noninteractive apt-get dist-upgrade -y -o Dpkg::Options::="--force-confdef" -o Dpkg::Options::="--force-confold"
```

---

## 8. Centralized Lifecycle & Make Orchestration

Always prefer Makefile targets over raw CLI invocations:

| Target | Description |
| :--- | :--- |
| `make init-all` | Initialize both Packer and Terraform toolchains |
| `make p-build` | Bake fresh Ubuntu 26.04.1 VM template (ID 777) on Proxmox |
| `make t-plan-infra` | Preview VM and LXC compute infrastructure changes |
| `make t-apply-infra` | Provision Proxmox compute resources (Control, Worker, Micro-NAS) |
| `make wait-for-cluster` | Block until K3s control plane API (`:6443`) responds ready |
| `make t-apply-k3s` | Inject Kubernetes secrets and Homepage config via Terraform |
| `make infra-up` | Deploy core cluster networking (MetalLB, Traefik, Cloudflare Tunnel) |
| `make apps-up` | Deploy all application stacks with dynamic domain substitution |
| `make <app>-up` | Deploy an individual application (e.g. `make plex-up`, `make vaultwarden-up`) |
| `make promstack-install-all` | Staggered deployment of Prometheus Stack -> Loki -> Alloy |
| `make grafana-pass` | Extract and base64-decode the active Grafana admin password |
| `make drain-worker-02` | Safely cordon and drain decommissioned worker-02 before scale-down |
| `make redeploy-workers` | Safely teardown and rebuild worker compute nodes |
| `make deploy-all` | Full end-to-end orchestration (VM apply -> health check -> K3s apply) |
| `make destroy-all` | Fully tear down the compute cluster (Micro-NAS data remains safe) |
