# 🛸 Mothership Homelab: Master Workspace Hub

This centralized repository acts as the single source of truth for the Infrastructure as Code (IaC) blueprints, orchestration configurations, and automation manifests powering the `Mothership` bare-metal homelab environment.

The primary objective is a zero-intervention deployment pipeline that bakes a lightweight base operating system template, provisions high-performance cluster compute nodes on Proxmox VE, and instantly scales a self-healing Kubernetes ecosystem.

---

## 🏗️ Architectural Topology

```mermaid
flowchart TB
    classDef k3s fill:#326ce5,stroke:#fff,stroke-width:2px,color:#fff;
    classDef proxmox fill:#e46623,stroke:#fff,stroke-width:2px,color:#fff;
    classDef storage fill:#8d6e63,stroke:#fff,stroke-width:2px,color:#fff;
    classDef network fill:#2e7d32,stroke:#fff,stroke-width:2px,color:#fff;
    classDef app fill:#455a64,stroke:#fff,stroke-width:1px,color:#fff;
    classDef client fill:#0288d1,stroke:#fff,stroke-width:1px,color:#fff;

    subgraph Clients["fa:fa-globe Ingress & Clients"]
        direction LR
        Fedora["fa:fa-desktop Fedora Workstation (LAN)"]:::client
        MacRemote["fa:fa-laptop Mac Remote (Tailscale)"]:::client
        CloudflareEdge["fa:fa-cloud Cloudflare Zero Trust"]:::client
    end

    subgraph Proxmox_Mothership["fa:fa-server Proxmox Host: 'mothership' (16 GB Physical RAM)"]
        direction TB

        subgraph Storage_Layer["fa:fa-database Storage Decoupling: micro-nas (LXC CT 250)"]
            MicroNAS["fa:fa-hdd micro-nas LXC (512 MB RAM)\nNFS Kernel Server | Tailscale | Syncthing"]:::storage
            ExtHDD[("fa:fa-database External 1TB HDD\n/mnt/export/storage")]:::storage
            MicroNAS --- ExtHDD
        end

        subgraph K3s_Cluster["fa:fa-microchip Dual-Node K3s Cluster"]
            direction LR
            ControlPlane["fa:fa-shield-alt k3s-control-01 (VM 100)\n3 GB RAM | 2 vCPUs\nAPI Server, Ingress, CoreDNS"]:::k3s
            WorkerNode["fa:fa-cogs k3s-worker-01 (VM 210)\n3 GB RAM | 2 vCPUs\nCompute & Application Workloads"]:::k3s
        end

        subgraph Ingress_Networking["fa:fa-network-wired Networking & Routing"]
            direction LR
            Traefik["Traefik (Ingress)"]:::network
            MetalLB["MetalLB (L2 VIPs)"]:::network
            Cloudflared["Cloudflared (Tunnel)"]:::network
        end

        subgraph Applications["fa:fa-cubes Kubernetes Application Stack"]
            direction TB
            subgraph Media_Namespace["Media Namespace"]
                Plex["fa:fa-film Plex Media Server (:32400)"]:::app
                Navidrome["fa:fa-music Navidrome Music (:4533)"]:::app
            end
            subgraph Networking_Namespace["Networking Namespace"]
                Homepage["fa:fa-home Homepage Dashboard (:3000)"]:::app
                PiHole["fa:fa-shield-virus Pi-hole DNS (:53 / :80)"]:::app
                Vaultwarden["fa:fa-key Vaultwarden (:80)"]:::app
                Karakeep["fa:fa-bookmark Karakeep (Meili + Chrome)"]:::app
                UptimeKuma["fa:fa-heartbeat Uptime Kuma (:3001)"]:::app
            end
            subgraph Monitoring_Namespace["Monitoring Namespace"]
                Monitoring["fa:fa-chart-line Prometheus, Grafana, Loki & Alloy"]:::app
            end
        end
    end

    Clients --> Ingress_Networking
    Ingress_Networking --> Applications
    K3s_Cluster --- Ingress_Networking
    Applications -. "NFS PV Mounts (all_squash, UID 1000)" .-> MicroNAS
    Fedora -. "Obsidian Sync" .-> MicroNAS
    MacRemote -. "Tailscale Mesh" .-> MicroNAS
```

# 📋 Prerequisites & Local Workstation Setup

Before executing automation targets via the root Makefile, ensure the environment matches these baselines:

## 1. Toolchain Installation (Fedora/RHEL Workstations)

```bash
    sudo dnf install -y packer terraform make openssh-clients kubectl helm
```

## 2. Local SSH Key & Verification Loops

Secure authentication loops rely entirely on public key checks. Ensure the signature exists locally and is bound to the running SSH agent before starting deployments:

```bash
    eval $(ssh-agent -s)
    ssh-add ~/.ssh/id_ed25519
```

- Security Isolation: NEVER commit local \*.tfvars files. Private keys, network maps, and gateway tokens must remain locally isolated on the workstation to prevent accidental public configuration leaks.

## 3. Hypervisor Storage Allocations

The target Proxmox host system must have the following configuration targets:

    local: Storage target hosting the baseline Ubuntu installation media image (local:iso/ubuntu-24.04.4-live-server-amd64.iso).

    local-lvm: Block pool backend allocation targeted for virtual node root disks (scsi0).

---

---

---

# 📀 Phase 1: Packer Template Generation

Directory Context: terraform/vm_provisioning/packer-k3s/

Automate the installation of an identical, immutable base OS template (ID 777) using Ubuntu's native Subiquity Autoinstall engine hosted on the Proxmox pool.
🕹️ Deep Dive: The Packer boot_command Sequence

```bash
    boot_command = [
        "<esc><wait3>",
        "c<wait3>",
        "linux /casper/vmlinuz autoinstall <wait>",
        "\"ds=nocloud;s=http://{{ .HTTPIP }}:{{ .HTTPPort }}/\" <wait3>",
        "---<enter><wait3>",
        "initrd /casper/initrd<enter><wait3>",
        "boot<enter>"
    ]
```

The macro simulates keyboard console input during POST initialization to intercept the standard boot loader screen, forcing GRUB into an unattended configuration pipeline via `Packer/Terraform`. This helps inject the custom cloud-init config. via `user-data` file defined in `packer-k3s/http`. [Read more about it here](./terraform/vm_provisioning/packer-k3s/README.md)

---

# 📦 Phase 2: Compute Provisioning via Terraform

Directory Context: `terraform/vm_provisioning/*`

Consumes the golden template image to provision resource-mapped virtual hardware topologies, inject network configurations, and handle automatic node registration using the baked `ID 777` template.
Compute Fleet Profiles

- Manager Plane (k3s-control-01): 2 Cores, 3GB RAM, DHCP Network Allocation.
- Worker Pools (k3s-worker-0[1-N]): 2 Cores, 2GB RAM, Static Networking Matrix (starting at .210 / .211).
- Decentralized Storage Block (micro-nas): Automation target spinning up dedicated sync spaces. Outside of the K3s cluster, this ensures data safety and lets me play around with pods, replicas and svcs without fear of data loss.

---

# 🌐 Phase 3: Kubernetes Core Infrastructure

Directory Context: `kubernetes/infrastructure/*`

Because bare-metal K3s nodes do not feature a native cloud load balancer controller out of the box, core networking elements handle internal service mapping.

1. MetalLB Load Balancer Layer

MetalLB hooks directly into the physical Layer 2 routing fabric to assign real external IP addresses to cluster services.
Upstream Installation Target:

```bash
kubectl apply -f kubernetes/infrastructure/metallb-config.yaml
```

Configuration Sync: From the project root, apply localized L2 IP pool definitions via the consolidated configurations (kubernetes/infrastructure/metallb-config.yaml).

---

# 🚀 Phase 4: Kubernetes Applications & Services

Directory Context: `kubernetes/applications/*`

## 1. Ad-Blocking Engine: Pi-hole Deployment

Establishes a single service instance that co-locates core DNS filtering networks and web administration interfaces.

Dedicated IP Profile: 192.168.50.242 (DNS: 53/UDP & 53/TCP | Web Panel: 80/TCP)

- Web Admin Panel Path: https://pihole.freesatly.com/admin (this will be different for everybody of course)
- Persistent Storage Architecture: Currently mapped to local node hostPath space (/var/data/pihole/config). Data strictly belongs to the physical worker host running the active pod. For absolute cross-node mobility, transition this layer to a distributed engine (e.g., Longhorn or NFS).
- Runtime Administrative Passwords: Update access configurations directly via the execution namespace:

```bash
 kubectl exec -it -n networking deployment/pihole-dns-server -- pihole setpassword
```

## 2. Observability Suite: Monitoring Stack

Deploys a comprehensive performance tracking layer via clean Helm upgrade and installation operations.

- Engine Scope: Orchestrates Prometheus, Grafana, Loki, and Alloy pipelines securely.
- Workspace Flush (Danger Target): Wipe all logging workloads in the environment completely:

```bash
    kubectl delete all --all -n monitoring
```

# 📁 Storage Architecture & Decoupling: "What is Where"

Resource Engine Context: [terraform/vm_provisioning/micro-nas.tf](file:///home/gman/Projects/homelab/terraform/vm_provisioning/micro-nas.tf)

The core principle of this homelab is **Cluster & Storage Decoupling**: Compute is transient, but persistent data is static. Sensitive and stateful data (Obsidian notes, Vaultwarden passwords, Navidrome music, Plex media/metadata) live strictly **outside** the K3s cluster lifecycle on the dedicated `micro-nas` LXC container (`192.168.50.250`, CT 250).

### 1. Physical Hardware & Memory Allocation (16 GB Host Footprint)

```text
PROXMOX HOST: mothership (16 GB Physical RAM Total)
├── 🚀 nvme0n1 (238.5G NVMe SSD) -> VG: `pve` (Pool: `local-lvm`)
│   ├── Host Root (`/`) & Proxmox Hypervisor Overhead
│   ├── VM 100 [k3s-control-01]: 30G NVMe | 3,072 MB (3 GB) RAM (Control Plane & Core Services)
│   ├── VM 210 [k3s-worker-01]:  30G NVMe | 3,072 MB (3 GB) RAM (Workloads & App Stack)
│   ├── CT 250 [micro-nas]:      15G NVMe |   512 MB (0.5 GB) RAM (LXC NFS & Syncthing Engine)
│   └── ⚡ Available Host Headroom: ~9.4 GB RAM for ZFS ARC, Linux page cache, and future workloads
│
└── 💽 sda (931.5G / 1TB External HDD) -> Mounted to `/mnt/export/storage`
    └── CT 250 [micro-nas]: Dedicated Thin Disk Mount Point (Stateful NFS Shares)
```

### 2. Standardized Storage Pattern: `/mnt/export/storage/<application-name>`

Inside `micro-nas` (`192.168.50.250`), persistent storage is mounted under `/mnt/export/storage`. Applications consume isolated NFS shares using hardened `all_squash` rules scoped strictly to the K3s node IPs (`192.168.50.185` and `192.168.50.210`):

| Application | Micro-NAS File Path | NFS Export Rule (`/etc/exports`) | Consumer / Protocol | Description |
| :--- | :--- | :--- | :--- | :--- |
| **Vaultwarden** | `/mnt/export/storage/vaultwarden` | `/mnt/export/storage/vaultwarden 192.168.50.185(...) 192.168.50.210(...)` | K3s NFS PV (`vaultwarden-nas-pv`) | Encrypted password vault database & RSA keys |
| **Navidrome (Data)** | `/mnt/export/storage/navidrome/data` | `/mnt/export/storage/navidrome/data 192.168.50.185(...) 192.168.50.210(...)` | K3s NFS PV (`navidrome-config-pv`) | SQLite database (`navidrome.db`), cache & artwork |
| **Navidrome (Music)** | `/mnt/export/storage/navidrome/music` | `/mnt/export/storage/navidrome/music 192.168.50.185(...) 192.168.50.210(...)` | K3s NFS PV (`navidrome-music-pv`) | Audio tracks, albums, and FLAC/MP3 files |
| **Plex (Config & DB)** | `/mnt/export/storage/plex/config` | `/mnt/export/storage/plex/config 192.168.50.185(...) 192.168.50.210(...)` | K3s NFS PV (`plex-config-pv`, 20Gi) | Server metadata, SQLite DB, agent state |
| **Plex (Media Library)**| `/mnt/export/storage/plex/media` | `/mnt/export/storage/plex/media 192.168.50.185(...) 192.168.50.210(...)` | K3s NFS PV (`plex-media-pv`, 500Gi) | Movies, TV series, video content |
| **Obsidian Vault** | `/mnt/export/storage/obsidian` | N/A (Syncthing user space) | Syncthing / Tailscale Mesh | Markdown notes synchronized across devices |

### 3. File Permissions & Security Hardening
- **Container UID/GID**: NFS exports for K8s container workloads are permissioned with `chown -R 1000:1000` to match standard non-root container workloads.
- **Root Squashing**: All NFS exports enforce `all_squash,anonuid=1000,anongid=1000`, ensuring client-side root processes can never execute with root privileges on the NAS host filesystem.
- **Syncthing Tailscale Isolation**: Syncthing's web admin GUI is bound strictly to the host's private Tailscale IP (`[TAILSCALE-IP]:8384`), keeping port 8384 closed on the physical LAN.

> [!IMPORTANT]
> **Zero Data Loss Guarantee**: You can safely run `make destroy-workers` or `make destroy-all` to completely tear down and re-provision the K3s cluster. As long as `micro-nas` (CT 250) remains intact, zero persistent application or note data will ever be lost.

---

# 🛑 Operations & Lifecycle Troubleshooting

## 1. Safe Dual-Node Migration & Node Draining

When scaling down from 2 workers to 1 worker (transitioning to the dual-node topology), gracefully evict running pods before applying Terraform changes:

```bash
# 1. Gracefully cordon and drain k3s-worker-02 (evicts pods to worker-01 and control-01)
make drain-worker-02

# 2. Apply Terraform to decommission VM 211 and reconfigure cluster resources
make t-apply-infra

# 3. Clean up the node registration from Kubernetes
kubectl delete node k3s-worker-02
```

Every single time the primary manager plane node (k3s-control-01) is destroyed and re-provisioned, it mints a fresh, randomized cluster authorization hash. You must manually sync this hash to allow worker nodes to register cleanly.

- Step A (Run on Manager Host Node): Extract the token:

  ```bash
      sudo cat /var/lib/rancher/k3s/server/node-token
  ```

- Step B (Update & Sync Local Host Workstation): Paste that token value right into the local workstation `terraform.tfvars` file under the `k3s_share_token` parameter block.

- Step C (Force Refresh Node Subsystems if Agent Sync Desyncs):
  ```bash
      sudo systemctl daemon-reload && sudo systemctl restart k3s-agent
  ```

## 2. Diagnostic Layout Validations

- Verify split-horizon route validation loops directly across the local LAN interface:
  ```bash
      curl -I -H "Host: pihole.freesalty.com" [http://192.168.50.240/admin/](http://192.168.50.240/admin/)
  ```

---

---

---

# ⚙️Docker Subsystem Block

- At the moment I haven't had a need to use `docker`. But you might, or I might in the future.

---

---

---

# 🛠️ Hypervisor & Cluster Native CLI Shortcuts

## Proxmox Host CLI Commands (qm & pct)

```bash
    # VM Management Controls
    qm list                  # Print a complete matrix of all virtual machines and allocations
    qm start <vmid>          # Power on a specific VM node instance (e.g., qm start 100)
    qm shutdown <vmid>       # Gracefully trigger standard OS guest power-off via Agent

    # Container Operations
    pct list                 # List running LXC containers on host hardware
    pct enter <vmid>         # Drop straight into a root shell inside a running LXC container
```

## Kubernetes CLI Commands

Drop these native diagnostic profiles straight into the workstation shell configuration file (e.g., `~/.zshrc` or `~/.bashrc`).

You will inevitably run into issues, and situations requiring you to work directly inside the cluster.
These are some of the more common kubectl I found myself repeating; turned into aliases for simplicity.

```bash
    # Cluster Routing Switchers
    alias kcontext-default="kubectl config use-context default"
    alias kcurr-context="kubectl config get-contexts"

    # Global System Diagnostics
    alias kinfo="kubectl cluster-info"
    alias kver="kubectl version --client"
    alias knodes="kubectl get nodes -o wide"

    # Resource Lookups
    alias kall-net="kubectl get all -n networking"
    alias kpods="kubectl get pods -o wide"
    alias ksvc="kubectl get svc --all-namespaces"
    alias kingress="kubectl get ingress --all-namespaces"

    # Real-Time Stream Tailing
    alias klogs="kubectl logs -f --tail=100"
    alias klogs-net="kubectl logs -f --tail=100 -n networking"

    # Interactive Pod Shell Drop-In
    alias kexec="kubectl exec -it"

    alias kcontext-MOTHERSHIP="kubectl config use-context default"
    alias kcurr-context="kubectl config get-contexts"

    # Global System Diagnostics

    alias kinfo="kubectl cluster-info"
    alias kver="kubectl version --client"
    alias knodes="kubectl get nodes -o wide"
    alias khealth="kubectl get componentstatuses"

    # Resource Lookups

    alias kall-net="kubectl get all -n networking"
    alias kpods="kubectl get pods -o wide"
    alias kdeployments="kubectl get deployment"
    alias ksvc="kubectl get svc --all-namespaces"
    alias kingress="kubectl get ingress --all-namespaces"

    # Ingress Controller Rules

    alias kwhitelist="kubectl get configmap -n ingress-nginx-internal ingress-nginx-controller -o jsonpath='{.data.whitelist-source-range}'"

    # Cluster Node Endpoint Extractors

    alias kips="kubectl get nodes -o jsonpath='{.items[*].status.addresses[?(@.type == \"InternalIP\")].address}'"
    alias kips-external="kubectl get nodes -o jsonpath='{.items[*].status.addresses[?(@.type == \"ExternalIP\")].address}'"

    # Real-Time Stream Tailing

    alias klogs="kubectl logs -f --tail=100"
    alias klogs-net="kubectl logs -f --tail=100 -n networking"

    # Interactive Pod Shell Drop-In

    alias kexec="kubectl exec -it"

    # Deployment Rollout Revisions Trace

    alias krev-pihole="kubectl rollout history deployment/pihole-dns-server -n networking"
    alias krev-tunnel="kubectl rollout history deployment/cloudflared-tunnel -n networking"

    # Local Proxy Management

    alias kproxy="kubectl proxy"
    alias kkill="pkill -9 -f 'kubectl proxy'"

    # Modern On-Demand Token Generator (Valid for 1 hour)

    alias ktoken="kubectl -n kubernetes-dashboard create token admin-user"

    # Inspect config-map value for a deployment

    alias homepage-config="kubectl get configmap homepage-config -n networking -o yaml"

    # Search ENVIRONMENT variables for a given service

    kubectl exec -it deployment/karakeep-server -n networking -- env | grep DISABLE
```

# Application Specific

## Karakeep

- Quickly generate a robust string for this value on your terminal using `openssl rand -base64 36`

By default `sign-ups are disabled. To add an additional user:

1. Log into your account at https://karakeep.freesalty.com.
2. Navigate to the Admin Settings page (located in your profile/settings menu).
3. Find the Users List tab and click the Create User button.
4. Input their details (Name, Email, and a temporary password) and hit Create.

### Karakeep Troubleshooting

debugging the browser plugin, if you ran `karakeep-browser.yaml`, but the browser engine claims is `not configured` on the admin panel.

- Start with a network test:

  ```bash
    kubectl exec -it deployment/karakeep-server -n networking -- wget -qO- http://karakeep-browser-service:3000/json
  ```

- Make sure you `karakeep` main deployment manifest, `karakeep-deployment.yaml` in this instance has the websocket env set to:

```
    # Link to Browserless chrome for web scraping
    - name: BROWSER_WEBSOCKET_URL
        value: "ws://karakeep-browser-service:3000"
```

#### Watch live Browser session:

In your karakeep dashboard, drop a JS heavy website like reddit.com.

At the same time run the following command to see the webscrapping engine go brrrr.

```bash
    kubectl port-forward svc/karakeep-browser-service 3000:3000 -n networking
```

2. Stream the Live Container Logs

Open up a couple of split terminal panes (using tmux or your terminal tabs) and tail the log streams for all three components simultaneously while you add the link.

Pane 1: Watch Karakeep coordinate the job

```bash
    kubectl logs deployment/karakeep-server -n networking -f --tail=20
```

    Look for: Logs indicating a new link parsing job has been added to the queue and an upstream connection request sent to the browser wrapper.

Pane 2: Watch Browserless execute the render

```bash
    kubectl logs deployment/karakeep-browser -n networking -f --tail=20
```

    Look for: Session started, logs about Chrome binaries launching, navigation events to reddit.com, and session closure metrics.

Pane 3: Watch Meilisearch build the full-text index

```bash
    kubectl logs deployment/karakeep-meili -n networking -f --tail=20
```

---

# Homepage:

todo

## Homepage Troubleshooting

todo

---

# Grafana:

todo

## Grafana Troubleshooting

todo

---

# Pihole:

todo

## Pihole Troubleshooting

todo

---

# Uptime-kuma:

todo

## Uptime-kuma Troubleshooting

todo

---

# VaultWarden (Bitwarden):

todo

## VaultWarden (Bitwarden) Troubleshooting

todo

---

# Plex Media Server:

Resource Context: [kubernetes/applications/plex/plex-deployment.yaml](file:///home/gman/Projects/homelab/kubernetes/applications/plex/plex-deployment.yaml)

- Web Ingress URL: `https://plex.freesalty.com/`
- Port Profile: `32400/TCP`
- Persistent Storage:
  - Media Library: `/mnt/export/storage/plex/media` (500Gi NFS PV, read-only mount)
  - Config & DB: `/mnt/export/storage/plex/config` (20Gi NFS PV)
  - Transcoding Scratch Space: Dedicated ephemeral `emptyDir` mounted at `/transcode` (preserves NAS I/O)

---

# Navidrome Music Server:

Resource Context: [kubernetes/applications/navidrome/navidrome-deployment.yaml](file:///home/gman/Projects/homelab/kubernetes/applications/navidrome/navidrome-deployment.yaml)

- Web Ingress URL: `https://music.freesalty.com/`
- Port Profile: `4533/TCP`
- Persistent Storage:
  - Music Library: `/mnt/export/storage/navidrome/music` (200Gi NFS PV, read-only mount)
  - Application Data: `/mnt/export/storage/navidrome/data` (5Gi NFS PV)

---
