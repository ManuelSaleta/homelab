# ==============================================================================
# vm-provisioning/reverse-proxy.tf
# ==============================================================================
# Provisions an unprivileged LXC container running native Traefik. 
# Configuration templates are externalized into the configs/ directory to enforce
# clear static analysis and separation of concerns.
# ==============================================================================

resource "proxmox_virtual_environment_container" "reverse_proxy" {
  node_name    = "mothership"
  vm_id        = 999
  unprivileged = true

  initialization {
    hostname = "reverse-proxy"

    # Network Topology Configuration
    ip_config {
      ipv4 {
        address = "192.168.50.240/24"
        gateway = "192.168.50.1"
      }
    }

    # Access & Authentication (Deploys key to root for initial setup)
    user_account {
      keys = [
        trimspace(file("~/.ssh/id_ed25519.pub"))
      ]
    }
  }

  cpu { cores = 1 }

  memory {
    dedicated = 512
    swap      = 256
  }

  disk {
    datastore_id = "local-lvm"
    size         = 10
  }

  operating_system {
    type             = "ubuntu"
    template_file_id = "local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst"
  }


  # File Provisioner: Ship the externalized configurations to the node tmp space
  provisioner "file" {
    content     = file("${path.module}/configs/traefik.yml")
    destination = "/tmp/traefik.yml"
  }

  provisioner "file" {
    content     = file("${path.module}/configs/traefik.service")
    destination = "/tmp/traefik.service"
  }

  # SSH Connection Strategy (Uses root to execute the initial setup script)
  connection {
    type        = "ssh"
    user        = "root"
    private_key = file("~/.ssh/id_ed25519")
    host        = "192.168.50.240"
  }

  # Software Bootstrapping
  provisioner "remote-exec" {
    inline = [
      "sleep 10",

      # 1. Dynamically create the fleet standard user
      "id -u gman &>/dev/null || useradd -m -s /bin/bash gman",
      "echo 'gman ALL=(ALL) NOPASSWD:ALL' | tee /etc/sudoers.d/gman",

      # 2. Migrate SSH keys from root to gman's home directory
      "mkdir -p /home/gman/.ssh",
      "cp /root/.ssh/authorized_keys /home/gman/.ssh/",
      "chown -R gman:gman /home/gman/.ssh",
      "chmod 700 /home/gman/.ssh",
      "chmod 600 /home/gman/.ssh/authorized_keys",

      # 3. Proceed with system core package installation
      "apt-get update",
      "apt-get install -y curl ca-certificates",

      # 4. Download and Install standalone Traefik binary
      "curl -sL https://github.com/traefik/traefik/releases/download/v3.0.1/traefik_v3.0.1_linux_amd64.tar.gz | tar xz",
      "mv traefik /usr/local/bin/",
      "chmod +x /usr/local/bin/traefik",

      # 5. Build Traefik's system environment parameters
      "mkdir -p /etc/traefik/dynamic",
      "mkdir -p /var/log/traefik",
      "mv /tmp/traefik.yml /etc/traefik/traefik.yml",
      "mv /tmp/traefik.service /etc/systemd/system/traefik.service",
      "touch /etc/traefik/acme.json",
      "chmod 600 /etc/traefik/acme.json",

      # 6. Start the native edge routing engine
      "systemctl daemon-reload",
      "systemctl enable --now traefik"
    ]
  }
}