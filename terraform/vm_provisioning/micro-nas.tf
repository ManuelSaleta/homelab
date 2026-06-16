# ==============================================================================
# Purpose: Provision a micro private NAS for secure Obsidian Vault storage
# - Clones cleanly from the golden Packer template (ID 777)
# - Pinpoints 1 CPU core and 512MB of RAM to save resources for K3s.
# - Attaches a dedicated secondary storage block for note persistence.
# - Leverages Cloud-init to auto-join your private Tailscale mesh network.
# Docs: https://registry.terraform.io/providers/bpg/proxmox/latest/docs
# ==============================================================================

# ==============================================================================
# 1. INITIALIZATION MATRICES (Cloud-Init customization layer)
# ==============================================================================
resource "proxmox_virtual_environment_file" "nas_cloud_config" {
  content_type = "snippets"
  datastore_id = "local"
  node_name    = "mothership"

  source_raw {
    file_name = "micro-nas-cloud-config.yaml"

    data = <<-EOF
    #cloud-config
    hostname: "micro-nas"
    
    # Declaratively partition and prepare the secondary storage disk block for Obsidian
    disk_setup:
      /dev/sdb:
        table_type: 'gpt'
        layout: true
        overwrite: false

    fs_setup:
      - filesystem: ext4
        device: /dev/sdb
        partition: auto

    mounts:
      - [ /dev/sdb, /mnt/obsidian-vault, "ext4", "defaults,nofail", "0", "2" ]

    runcmd:
      # 1. Install the official Tailscale engine binary
      - curl -fsSL https://tailscale.com/install.sh | sh
      
      # 2. Force instant node provisioning into your private tailnet mesh
      # --accept-dns=false prevents the overlay from hijacking your local cluster dns records
      - tailscale up --authkey="${var.tailscale_auth_key}" --accept-dns=false
    EOF
  }
}

# ==============================================================================
# 2. THE STORAGE VM DEPLOYMENT (Cloned securely from Template 777)
# ==============================================================================
resource "proxmox_virtual_environment_vm" "micro_nas" {
  name        = "micro-nas"
  description = "Managed by Terraform - Micro Private NAS for Obsidian Vault via Golden Template"
  tags        = ["Storage", "Tailscale", "nas"]
  node_name   = "mothership"
  vm_id       = 250 # Safe, distinct ID isolated away from your manager (100) and worker blocks
  on_boot     = true

  # Enables the QEMU Guest Agent to communicate IP metadata cleanly
  agent {
    enabled = true
  }

  # Hardware Layout Blocks (Scaled down to maximize available K3s worker footprint)
  cpu {
    cores = 1
    type  = "host"
  }

  memory {
    dedicated = 1024 # 1 GB RAM is sufficient for a lightweight Tailscale node and file server
  }

  network_device {
    bridge = "vmbr0"
    model  = "virtio"
  }

  # Target your freshly generated golden Packer template
  clone {
    vm_id   = var.proxmox_template_vm_id
    full    = true
    retries = 3
  }

  # Cloned VMs should boot directly straight off their primary OS storage block
  boot_order = ["scsi0"]

  # OS Disk layer (cloned from your Packer foundation base)
  disk {
    datastore_id = "local-lvm"
    interface    = "scsi0"
    size         = 20
    discard      = "on"
  }

  # 🎯 DEDICATED DATA STORAGE DISK (Maps to /dev/sdb inside the VM)
  disk {
    datastore_id = "local-lvm"
    interface    = "scsi1"
    size         = 20 # Plenty of space for years of markdown/vault notes
    discard      = "on"
  }

  initialization {
    datastore_id      = "local-lvm" # Location where the cloud-init drive ISO spins up
    user_data_file_id = proxmox_virtual_environment_file.nas_cloud_config.id

    # Forces cloud-init to respect gman and locks down your public key file
    user_account {
      username = "gman"
      keys     = [trimspace(file("/home/gman/.ssh/id_ed25519.pub"))]
    }

    # Static LAN IP mapping to prevent collisions with your K3s cluster blocks
    ip_config {
      ipv4 {
        address = "192.168.50.250/24"
        gateway = var.default_gateway_ip
      }
    }

    dns {
      servers = ["${var.default_gateway_ip}", "1.1.1.1"]
    }
  }
}