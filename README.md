🛸 Mothership Homelab: Core Cluster IaC

This repository contains the Infrastructure as Code (IaC) blueprints to automate the provisioning and configuration of a resilient, bare-metal K3s Kubernetes cluster on a Proxmox VE hypervisor node.
🎯 Project Goal

The primary objective is a zero-intervention deployment pipeline that builds a production-grade baseline operating system template and auto-scales an orchestrating Kubernetes environment.

    Packer Layer: Bakes a completely sanitized, lightweight Ubuntu 24.04 golden base image containing pre-staged binaries, public SSH access configurations, and virtualization agents.

    Terraform Layer: Consumes the golden template image to dynamically map resource pools, provision virtual hardware topology, inject custom network maps, and initialize automatic cluster nodes registration.

📂 Repository Blueprint
Plaintext
vm_provisioning/
├── packer-k3s/                  # Dedicated directory for baking the base OS image
│   ├── http/
│   │   └── user-data            # Ubuntu Template Subiquity Autoinstall configuration
│   │   └── meta-data            # Empty file, no file extension but it is required by Cloud-Init
│   ├── Makefile                 # Packer-specific build pipeline automation commands
│   ├── packer.pkrvars.hcl       # Variable values specifically loaded into the Packer engine
│   ├── README.md                # Packer module technical documentation
│   └── ubuntu-vm-k3s.pkr.hcl    # Core Packer HCL automation recipe for Template 777
├── main.tf                      # Core Proxmox Provider & K3s Control Plane VM declaration
├── Makefile                     # Root command routing matrix for total cluster lifecycle
├── README.md                    # Master project documentation and architecture manual
├── terraform.tfvars.hcl         # Shared HCL schema values file
├── variables.tf                 # Strict structural schema declarations for Terraform input contracts
└── workers.tf                   # Automated worker-node scaling array loops & cloud-init snippets                   # Universal command routing matrix for lifecycle operations

📋 Prerequisites & Workstation Setup

Before initiating a template compilation or applying an infrastructure block layer, your underlying environment must fulfill the following technical baselines:
1. Mandatory Local Workstation Tools

Ensure your execution host has the standard infrastructure toolsets installed and available in its tracking path:

    Packer (v1.10+)

    Terraform (v1.7+)

    GNU Make (Standard shell scripting execution matrix)

    OpenSSH Client (With an active backend authentication agent)

Bash

# Example for Fedora Linux workstations
sudo dnf install -y packer terraform make openssh-clients

2. Proxmox Hypervisor Storage Targets

Your targeted Proxmox host system must have the standard storage allocations configured to map the volume requests initialized by the HCL code:

    local: Must house the vanilla Ubuntu live-server installer ISO (local:iso/ubuntu-24.04.4-live-server-amd64.iso).

    local-lvm: Used as the active target block pool storage backend where the VM root operating system virtual disks (scsi0) reside.

3. Local Private Key & Agent Configurations

The deployment sequence relies completely on secure public key verification loops.

    Ensure your authentication signature (id_ed25519) exists on your machine at ~/.ssh/id_ed25519.

    Ensure your public verification payload matches the key assigned inside your http/user-data autoinstall template.

    Start and bind your local runtime environment agent so Terraform can tap into the communication loop seamlessly over the SSH channel:

Bash

eval $(ssh-agent -s)
ssh-add ~/.ssh/id_ed25519

    [!IMPORTANT]
    To prevent accidental leaks of private network gateways, cloud tokens, and cluster keys to public spaces, NEVER commit your local terraform.tfvars file. Keep your private variables isolated locally; the underlying .gitignore block is configured to filter out structural *.tfvars extensions cleanly.
