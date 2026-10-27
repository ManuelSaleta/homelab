# ==============================================================================
# 1. THE USER DATA SNIPPETS (Streamlined for Golden VM Template )
# ==============================================================================
resource "proxmox_virtual_environment_file" "k3s_worker_cloud_config" {
  count        = var.worker_count
  content_type = "snippets"
  datastore_id = "local"
  node_name    = "mothership"

  source_raw {

    file_name = "k3s-worker-0${count.index + 1}-cloud-config.yaml"

    data = <<-EOF
    #cloud-config
    hostname: "k3s-worker-0${count.index + 1}"

    write_files:
      - path: /etc/rancher/k3s/config.yaml
        permissions: "0600"
        owner: "root:root"
        content: |
          server: "https://${var.k3s_manager_ip}:6443"
          token: "${var.k3s_share_token}"
          node-name: "k3s-worker-0${count.index + 1}"
          node-label:
            - "node-role.kubernetes.io/worker=true"

    runcmd:
      # 1. Set the individual node name with restricted permissions
      - install -m 0600 /dev/null /etc/systemd/system/k3s-agent.service.env
      - echo 'K3S_NODE_NAME="k3s-worker-0${count.index + 1}"' > /etc/systemd/system/k3s-agent.service.env
      
      # 2. WAIT for the control plane to be reachable before trying to join
      - until curl -k -s https://${var.k3s_manager_ip}:6443/readyz; do echo "Waiting for control plane..."; sleep 5; done
      
      # 3. Join the cluster using local baked installer (reads /etc/rancher/k3s/config.yaml without leaking token to logs)
      - if [ -x /home/gman/install.sh ]; then INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_EXEC="agent" /home/gman/install.sh; else curl -sfL https://get.k3s.io | INSTALL_K3S_SKIP_DOWNLOAD=true INSTALL_K3S_EXEC="agent" sh -; fi
      
      # 4. Reload and restart
      - systemctl daemon-reload
      - systemctl restart k3s-agent
    EOF
  }
}

# ==============================================================================
# 2. THE VM DEPLOYMENT (Cloned securely from Template 777)
# ==============================================================================
resource "proxmox_virtual_environment_vm" "k3s_worker" {
  count       = var.worker_count
  name        = "k3s-worker-0${count.index + 1}"
  description = "Managed by Terraform - K3s Worker Node via Golden Template"
  tags        = ["k3s", "kubernetes", "worker"]
  node_name   = "mothership"
  vm_id       = 210 + count.index # Arbitrary starting point for worker VM IDs; matched to the static IP assignment in cloud-init
  on_boot     = true

  # Enables the QEMU Guest Agent to communicate IP metadata cleanly
  agent {
    enabled = true
  }

  # Hardware Layout Blocks
  cpu {
    cores = 2
    type  = "x86-64-v2-AES"
  }

  memory {
    dedicated = 10240 # 10 GB RAM for consolidated application workloads (Plex, Jellyfin, Karakeep, Observability)
  }

  network_device {
    bridge = "vmbr0"
    model  = "virtio"
  }

  # Target the freshly generated golden Packer template
  clone {
    vm_id   = var.proxmox_template_vm_id # Point this directly to the brand new template ID
    full    = true                       # Allocates standalone storage profiles on local-lvm
    retries = 3
  }

  # Cloned VMs should boot directly straight off their primary OS storage block
  boot_order = ["scsi0"]

  # Storage configuration mapping to track template overrides
  disk {
    datastore_id = "local-lvm"
    interface    = "scsi0"
    size         = 30 # Feel free to increase this if workers need more than the template default
    discard      = "on"
  }

  # Cloud-Init Overrides Layer
  initialization {
    datastore_id = "local-lvm" # Location where the cloud-init drive ISO spins up

    # Map the specific user-data snippets configuration mapped above
    user_data_file_id = proxmox_virtual_environment_file.k3s_worker_cloud_config[count.index].id

    # Forces cloud-init to pass the exact parameters instead of the "ubuntu" fallback
    user_account {
      username = var.ssh_username
      keys     = [trimspace(file(pathexpand(var.ssh_public_key_path)))]
    }

    # Set static IPs for worker nodes 192.168.1.X starting at .210
    ip_config {
      ipv4 {
        address = "192.168.1.${210 + count.index}/24"
        gateway = var.default_gateway_ip
      }
    }

    dns {
      servers = ["${var.default_gateway_ip}", "1.1.1.1"]
    }
  }

  lifecycle {
    ignore_changes = [
      initialization,
    ]
  }
}