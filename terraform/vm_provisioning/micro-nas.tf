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

      # 4. Modify config to allow GUI access over the Tailscale network only
      - sleep 5
      - TS_IP=$(tailscale ip -4) && sed -i "s/127.0.0.1:8384/$${TS_IP}:8384/" /home/gman/.config/syncthing/config.xml
      - systemctl restart syncthing@gman.service

      # 5. Create persistent application directories under /mnt/export/storage
      - mkdir -p /mnt/export/storage/vaultwarden
      - mkdir -p /mnt/export/storage/navidrome/data
      - mkdir -p /mnt/export/storage/navidrome/music
      - mkdir -p /mnt/export/storage/plex/config
      - mkdir -p /mnt/export/storage/plex/media
      - mkdir -p /mnt/export/storage/jellyfin/config
      - mkdir -p /mnt/export/storage/obsidian

      # 6. Set permissions (1000 for standard non-root container workloads, gman for Obsidian sync)
      - chown -R 1000:1000 /mnt/export/storage/vaultwarden
      - chown -R 1000:1000 /mnt/export/storage/navidrome
      - chmod -R 775 /mnt/export/storage/navidrome/music
      - chown -R 1000:1000 /mnt/export/storage/plex
      - chmod -R 775 /mnt/export/storage/plex/media
      - chown -R 1000:1000 /mnt/export/storage/jellyfin
      - chmod -R 775 /mnt/export/storage/jellyfin/config
      - chown -R gman:gman /mnt/export/storage/obsidian

      # 7. Write hardened export rules to /etc/exports (restricted to K3s nodes with all_squash)
      - echo "/mnt/export/storage/vaultwarden 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - echo "/mnt/export/storage/navidrome/data 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - echo "/mnt/export/storage/navidrome/music 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - echo "/mnt/export/storage/plex/config 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - echo "/mnt/export/storage/plex/media 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - echo "/mnt/export/storage/jellyfin/config 192.168.50.185(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000) 192.168.50.210(rw,sync,no_subtree_check,all_squash,anonuid=1000,anongid=1000)" >> /etc/exports
      - exportfs -rav

      # 8. Enable and start NFS server
      - systemctl enable --now nfs-server
    EOF
  }
}

# ==============================================================================
# 2. THE STORAGE CONTAINER DEPLOYMENT (LXC Container for Minimal Footprint)
# - Pinpoints 1 CPU core and 512MB of RAM (reclaiming ~1GB+ RAM vs VM)
# - Privileged container (unprivileged = false) required for nfs-kernel-server
# ==============================================================================
resource "proxmox_virtual_environment_container" "micro_nas" {
  node_name     = "mothership"
  vm_id         = 250 # Distinct ID isolated away from manager (100) and worker blocks
  description   = "Managed by Terraform - Micro Private NAS (LXC) for Storage"
  tags          = ["lxc", "nas", "storage", "tailscale"]
  start_on_boot = true
  started       = true
  unprivileged  = false # Privileged mode required for kernel NFS server operation

  initialization {
    hostname = "micro-nas"

    ip_config {
      ipv4 {
        address = "192.168.50.250/24"
        gateway = var.default_gateway_ip
      }
    }

    dns {
      servers = ["${var.default_gateway_ip}", "1.1.1.1"]
    }

    user_account {
      keys = [trimspace(file("/home/gman/.ssh/id_ed25519.pub"))]
    }
  }

  cpu {
    cores = 1
  }

  memory {
    dedicated = 512 # 500MB allocated for lightweight Syncthing + NFS
    swap      = 512
  }

  network_interface {
    name   = "eth0"
    bridge = "vmbr0"
  }

  operating_system {
    template_file_id = var.proxmox_lxc_template
    type             = "ubuntu"
  }

  # Root operating system disk
  disk {
    datastore_id = "local-lvm"
    size         = 15
  }

  # Dedicated persistent storage mount point on the external 1TB HDD pool
  mount_point {
    volume = "ext-hdd-storage"
    size   = "900G"
    path   = "/mnt/export/storage"
  }

  features {
    nesting = true
    mount   = ["nfs"]
  }

  lifecycle {
    ignore_changes = [
      initialization,
      operating_system,
      disk,
      mount_point,
    ]
  }
}
