# ==============================================================================
# Purpose: Provision the baseline infrastructure for the k3s cluster manager
# - Clones cleanly from the golden Packer template (ID 777)
# - Pinpoints 2 CPU cores and 3GB of RAM.
# - Leverages Cloud-init to pass unique network definitions and settings.
# Docs: https://registry.terraform.io/providers/bpg/proxmox/latest/docs
# ==============================================================================

terraform {
  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "~> 0.106"
    }
    # 🎯 ADDED: Official HashiCorp Kubernetes Provider definition
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 2.31"
    }
  }
}

provider "proxmox" {
  endpoint  = var.proxmox_endpoint
  insecure  = true
  api_token = var.proxmox_api_token

  ssh {
    agent    = true   # Reads your local Fedora ssh-agent
    username = "root" # The host OS user on your Proxmox machine
    node {
      name    = "mothership"
      address = var.proxmox_host_ip
    }
  }
}

# 🎯 ADDED: Target your K3s cluster context directly using your local kubeconfig
provider "kubernetes" {
  config_path    = "~/.kube/config"
  config_context = "default"
}

# ==============================================================================
# 1. CLUSTER CONTROL PLANE MANAGEMENT
# ==============================================================================
resource "proxmox_virtual_environment_vm" "k3s_control" {
  name        = "k3s-control-01"
  description = "Lightweight K3s Kubernetes Control Node cloned from template 777"
  tags        = ["Kubernetes", "K3s", "manager"]
  node_name   = "mothership"
  vm_id       = 100
  on_boot     = true

  # ============================================================================
  # THE TEMPLATE CLONE ENGINE
  # ============================================================================
  clone {
    vm_id   = var.proxmox_template_vm_id # This is the VM ID of the template you built with Packer (default 777)
    full    = true                       # Creates a complete independent disk allocation on local-lvm
    retries = 3
  }

  # Hardware Layout Blocks (Ensures exact compliance or overrides template definitions)
  cpu {
    cores = 2
    type  = "host"
  }

  memory {
    dedicated = 3072 # 3 GB RAM
  }

  # Storage Mapping (Must point to the exact target block you laid down in Packer)
  disk {
    datastore_id = "local-lvm"
    interface    = "scsi0"
    size         = 30 # Overrides template disk size if it was smaller than 30GB
    discard      = "on"
  }

  network_device {
    bridge = "vmbr0"
    model  = "virtio"
  }

  # Cloned VMs with Cloud-Init should boot straight from the primary operating disk
  boot_order = ["scsi0"]

  # ============================================================================
  # INITIALIZATION MATRICES (Cloud-Init customization layer)
  # ============================================================================
  initialization {
    datastore_id = "local-lvm" # Tells Proxmox where to spawn the ephemeral cloud-init drive

    # Forces cloud-init to respect gman and locks down your public key file 
    user_account {
      username = "gman"
      keys     = [trimspace(file("/home/gman/.ssh/id_ed25519.pub"))]
    }

    # 🎯 FORCE STATIC IP CONFIGURATION FOR THE MANAGER
    ip_config {
      ipv4 {
        address = "${var.k3s_manager_ip}/24"
        gateway = var.default_gateway_ip
      }
    }
  }
}