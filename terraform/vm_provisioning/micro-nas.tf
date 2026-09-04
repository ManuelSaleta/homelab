# ==============================================================================
# Purpose: Provision a micro private NAS for secure Obsidian Vault storage
# - Clones cleanly from the golden Packer template (ID 777)
# - Pinpoints 1 CPU core and 512MB of RAM to save resources for K3s.
# - Attaches a dedicated secondary storage block for note persistence.
# - Leverages Cloud-init to auto-join the private Tailscale mesh network.
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

    # Declaratively partition and prepare the secondary storage disk block
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
      - [ /dev/sdb, /mnt/export/storage, "ext4", "defaults,nofail", "0", "2" ]

    runcmd:
      ##############################################################################
      # Sequence 1 - 4: Syncthing w/ Tailscale setup
      # Sequence 5 - 8: NFS server & /mnt/export/storage/<app> shares setup
      ##############################################################################

      # 1. Install Tailscale, Syncthing, and nfs-kernel-server
      - curl -fsSL https://tailscale.com/install.sh | sh
      - DEBIAN_FRONTEND=noninteractive apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y syncthing nfs-kernel-server

      # 2. Configure Tailscale
      - tailscale up --authkey="${var.tailscale_auth_key}" --accept-dns=false

      # 3. Provision Syncthing for gman via systemd unit template
      - mkdir -p /home/gman/.config/syncthing
      - chown -R gman:gman /home/gman/.config
      - systemctl enable --now syncthing@gman.service

      # 4. Modify config to allow GUI access over the Tailscale network
      - sleep 5
      - sed -i 's/127.0.0.1:8384/0.0.0.0:8384/' /home/gman/.config/syncthing/config.xml
      - systemctl restart syncthing@gman.service

      # 5. Create persistent application directories under /mnt/export/storage
      - mkdir -p /mnt/export/storage/vaultwarden
      - mkdir -p /mnt/export/storage/navidrome/data
      - mkdir -p /mnt/export/storage/navidrome/music
      - mkdir -p /mnt/export/storage/plex/config
      - mkdir -p /mnt/export/storage/plex/media
      - mkdir -p /mnt/export/storage/obsidian

      # 6. Set permissions (1000 for standard non-root container workloads, gman for Obsidian sync)
      - chown -R 1000:1000 /mnt/export/storage/vaultwarden
      - chown -R 1000:1000 /mnt/export/storage/navidrome
      - chmod -R 775 /mnt/export/storage/navidrome/music
      - chown -R 1000:1000 /mnt/export/storage/plex
      - chmod -R 775 /mnt/export/storage/plex/media
      - chown -R gman:gman /mnt/export/storage/obsidian

      # 7. Write export rules to /etc/exports and apply
      - echo "/mnt/export/storage/vaultwarden 192.168.50.0/24(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
      - echo "/mnt/export/storage/navidrome/data 192.168.50.0/24(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
      - echo "/mnt/export/storage/navidrome/music 192.168.50.0/24(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
      - echo "/mnt/export/storage/plex/config 192.168.50.0/24(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
      - echo "/mnt/export/storage/plex/media 192.168.50.0/24(rw,sync,no_subtree_check,no_root_squash)" >> /etc/exports
      - exportfs -rav

      # 8. Enable and start NFS server
      - systemctl enable --now nfs-server
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
  vm_id       = 250 # Safe, distinct ID isolated away from the manager (100) and worker blocks
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
    dedicated = 1536 # 1.5 GB RAM is sufficient for a lightweight Tailscale node and file server
  }

  network_device {
    bridge = "vmbr0"
    model  = "virtio"
  }

  # Target the freshly generated golden Packer template
  clone {
    vm_id   = var.proxmox_template_vm_id
    full    = true
    retries = 3
  }

  # Cloned VMs should boot directly straight off their primary OS storage block
  boot_order = ["scsi0"]

  # OS Disk layer (cloned from the Packer foundation base)
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

    # Forces cloud-init to respect gman and locks down the public key file
    user_account {
      username = "gman"
      keys     = [trimspace(file("/home/gman/.ssh/id_ed25519.pub"))]
    }

    # Static LAN IP mapping to prevent collisions with the K3s cluster blocks
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
