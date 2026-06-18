# 🛸 Mothership Homelab: Core Cluster IaC

This repository contains the Infrastructure as Code (IaC) blueprints to automate the provisioning and configuration of a resilient, bare-metal K3s Kubernetes cluster on a Proxmox VE hypervisor node.
🎯 Project Goal

The primary objective is a zero-intervention deployment pipeline that builds a production-grade baseline operating system template and auto-scales an orchestrating Kubernetes environment.

    Packer Layer: Bakes a completely sanitized, lightweight Ubuntu 24.04 golden base image containing pre-staged binaries, public SSH access configurations, and virtualization agents.

    Terraform Layer: Consumes the golden template image to dynamically map resource pools, provision virtual hardware topology, inject custom network maps, and initialize automatic cluster nodes registration.

## 📂 Repository Topology

This workspace is structured to enforce a strict separation of concerns across our self-hosted infrastructure lifecycle, breaking down workloads from bare-metal template generation up to containerized application orchestration.

```text
.
├── README.md                           # Global Homelab System Blueprint
│
├── terraform/
│   ├── TODOs.txt
│   └── vm_provisioning/
│       ├── main.tf                     # Proxmox Provider & Control-Plane Allocation
│       ├── workers.tf                  # Cluster Scale Architecture (Worker Nodes)
│       ├── micro-nas.tf                # Tiny NAS file server (tailscale)
│       ├── variables.tf                # Parameter Boundaries
│       ├── secrets.tf                  # Secrets required by services, pihole, cloudflare, etc
│       ├── terraform.tfvars            # Bare-Metal Environment Secret Variables
│       ├── Makefile                    # Automation Hooks (`make apply`, `make plan`)
│       │
│       └── packer-k3s/                 # Automated OS Image-Baking Paradigm
│           ├── ubuntu-vm-k3s.pkr.hcl  # Packer Image Configuration (Bakes K3s Runtime)
│           ├── packer.pkrvars.hcl      # Packer Target Definitions
│           └── http/
│               ├── user-data           # Cloud-Init Automated Automation Configuration
│               └── meta-data           # Cloud-Init Target Spec
│
├── kubernetes/                         # Unified Orchestration Layer
│   ├── README.md
│   ├── TODO_expose_kube_dashboard.txt
│   │
│   ├── infrastructure/                 # Foundational Cluster Core Network Plugs
│   │   ├── metallb-config.yaml         # Bare-Metal Layer-2 Core Network Pool (.240-.250)
│   │   └── cloudflared-tunnel.yaml     # Cloudflare Edge Gateway Outbound Daemon
│   │
│   └── applications/                   # Declarative K3s Application Layer (GitOps-Ready)
│       ├── README.md
│       ├── TODOS.txt
│       ├── pihole-deployment.yaml      # Cluster DNS, Ad-Blocking, & Local Split-Horizon Routing
│       ├── homepage-deployment.yaml    # Main Landing Infrastructure Command Dashboard
│       └── grafana-exposure.yaml       # Metrics Visual Logging Pipeline
│
└── docker/                             # Isolated Legacy Container Standalone Sandboxes
    └── container-provisioning/
        ├── docker-compose.yaml
        ├── Makefile
        └── README.md
```

## Diagram

- TODO: Generate a proper one using mermaid.js?

```text
========================================================================================
                 [ LOCAL WORKSTATION: RHL(Fedora) / Neovim / Zsh ]
                                      │
                                      │ (IaC Deployment: `make apply` -> Packer + Terraform)
                                      ▼
========================================================================================
 [ BARE-METAL HYPERVISOR: Proxmox VE ("mothership") ] ── (Storage: local-lvm)
   │
   └── [ COMPUTE LAYER: K3s Kubernetes Cluster ]
         │
         ├── Control-Plane & Worker Nodes (192.168.50.X)
         │     │
         │     ├── 🛠️ INFRASTRUCTURE NAMESPACE (`networking`)
         │     │     ├── [ MetalLB L2 LoadBalancer Controller ]
         │     │     │     ├── IP Pool: .240 ──► Native Traefik Edge Proxy
         │     │     │     └── IP Pool: .242 ──► Pi-hole DNS Core (Port 53)
         │     │     │
         │     │     ├── [ cloudflared Daemon Pod ]
         │     │     │     └── Outbound Encrypted Tunnel (CloudflareSecureToken)
         │     │     │
         │     │     └── [ Traefik Ingress Controller ] (IP: 192.168.50.240)
         │     │           └── Watches K3s Control Plane API for Ingress Rules 🔄
         │     │
         │     └── 🚀 APPLICATION NAMESPACE
         │           ├── Pi-hole DNS Server (Internal web service bound to Traefik Ingress)
         │           ├── Homepage Dashboard
         │           └── Grafana Stack
         │
         └── [ STATE STORAGE LAYER ]
               └── Persistent Volume HostPaths (/var/data/pihole/config, etc.)
========================================================================================
```

## 📋 Prerequisites & Workstation Setup

Before initiating a template compilation or applying an infrastructure block layer, your underlying environment must fulfill the following technical baselines:

1. Mandatory Local Workstation Tools

Ensure your execution host has the standard infrastructure toolsets installed and available in its tracking path:

    Packer (v1.10+)

    Terraform (v1.7+)

    Kubectl

    GNU Make (Standard shell scripting execution matrix)

    OpenSSH Client (With an active backend authentication agent)

Bash

## 📋 Example for Linux workstations

> [!IMPORTANT] This targets a Fedora/RHL flavor. Adjust accordingly

```bash
sudo dnf install -y packer terraform make openssh-clients
```

2. Proxmox Hypervisor Storage Targets

The targeted Proxmox host system must have the standard storage allocations configured to map the volume requests initialized by the HCL code:

    local: Must house the vanilla Ubuntu live-server installer ISO (local:iso/ubuntu-24.04.4-live-server-amd64.iso).

    local-lvm: Used as the active target block pool storage backend where the VM root operating system virtual disks (scsi0) reside.

3. Local Private Key & Agent Configurations

The deployment sequence relies completely on secure public key verification loops.

    Ensure your authentication signature (id_ed25519) exists on your machine at ~/.ssh/id_ed25519.

    Ensure your public verification payload matches the key assigned inside your http/user-data autoinstall template.

    Start and bind your local runtime environment agent so Terraform can tap into the communication loop seamlessly over the SSH channel:

```bash
    eval $(ssh-agent -s)
    ssh-add ~/.ssh/id_ed25519
```

> [!important] To prevent accidental leaks of private network gateways, cloud tokens, and cluster keys to public spaces, NEVER commit your local terraform.tfvars file. Keep your private variables isolated locally; the underlying .gitignore block is configured to filter out structural \*.tfvars extensions cleanly.

# 🏴‍☠️ Proxmox VE Command-Line Toolkit (`qm` & `pct`)

When managing the underlying virtualization layer for the cluster nodes directly on the Proxmox host (`mothership`) In my case, use these native CLI utilities.

## 🖥️ Virtual Machine Management (ProxMox) (`qm`)

These commands control your KVM/QEMU Virtual Machines (like your K3s control-plane and worker nodes).

```bash
# Core Lifecycle Controls
qm start <vmid>          # Power on a specific VM (e.g., qm start 100)
qm shutdown <vmid>       # Gracefully shut down a VM via ACPI/Guest Agent
qm stop <vmid>           # Force kill power to a VM (hard reset)
qm reboot <vmid>         # Bounce the VM state instantly

# Auditing & State Mapping
qm list                  # Print a complete matrix of all VMs, resource allocations, and statuses
qm status <vmid>         # Get detailed runtime status of a single node
qm config <vmid>         # Dump the entire hardware layout configuration file for a VM

# Templates & Cloning (Automating cattle steps manually)
qm template <vmid>       # Convert an existing VM into a golden read-only template
qm clone <vmid> <newid>  # Create an instant linked or full clone from a template ID
```

### LXC Container Management

```bash
# Core Lifecycle Controls
pct start <vmid>         # Power on an LXC container
pct shutdown <vmid>      # Request a graceful initialization shutdown
pct stop <vmid>          # Instantly kill an LXC runtime execution environment
pct reboot <vmid>        # Cycle the container operating system

# Configuration & Execution Hooks
pct list                 # List all containers on the host node
pct config <vmid>        # Read the environment resource definitions for an LXC
pct exec <vmid> <cmd>    # Execute a command directly inside the container without SSH
                         # Example: pct exec 999 apt-get update

# Direct Container Shell Access
pct enter <vmid>         # Drop straight into a root shell inside the running container
```

## 📡 Proxmox API Shell & Diagnostics (pvesh & pve)

### Cluster & API Auditing

```bash
pvesh get /cluster/status   # Query the API directly to check cluster health
pvesh get /nodes            # List all hardware hosts in the architecture
pveversion -v               # Print detailed package versions for running services (kernel, pve-manager, qemu)
```

### Storage Volume Auditing

```bash
pvesm status                # Scan all storage backends (local, local-lvm) and read usage metrics
```
