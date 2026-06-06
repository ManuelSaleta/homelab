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

  # 🛠️ FIX: Explicitly define the interface map and attach it to your virtual bridge
  network_interface {
    name   = "eth0"
    bridge = "vmbr0" # <-- This connects the LXC to your actual home network switch!
  }

  # 🛠️ FIX: Enable nesting for Systemd 255 compatibility
  features {
    nesting = true
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

  # 1. Shared SSH Connection Strategy
  # Placed here so all downstream provisioners automatically inherit these credentials.
  connection {
    type        = "ssh"
    user        = "root"
    private_key = file("~/.ssh/id_ed25519")
    host        = "192.168.50.240"
    timeout     = "3m" # ⏳ Instructs Terraform to patiently retry connection for up to 3 mins
  }

  # 2. Pre-Flight Connection Boot-Strapper
  # This forces Terraform to wait until the OS initializes its interfaces 
  # and the SSH daemon is fully listening before any files are copied.
  provisioner "remote-exec" {
    inline = [
      "echo 'LXC Container network interface detected online.'",
      "until systemctl is-active --quiet ssh || [ -f /var/run/sshd.pid ]; do echo 'Waiting for OpenSSH server to initialize...'; sleep 3; done"
    ]
  }

  # 3. File Provisioners: Ship the configurations safely now that SSH is responsive
  provisioner "file" {
    content     = file("${path.module}/configs/traefik.yml")
    destination = "/tmp/traefik.yml"
  }

  provisioner "file" {
    content     = file("${path.module}/configs/traefik.service")
    destination = "/tmp/traefik.service"
  }

  # 4. Software Bootstrapping
  provisioner "remote-exec" {
    inline = [
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
      "apt-get install -y curl ca-certificates openssh-server",

      # 4. Download and Install standalone Traefik binary
      "curl -sL https://github.com/traefik/traefik/releases/download/v3.0.1/traefik_v3.0.1_linux_amd64.tar.gz | tar xz",
      "mv traefik /usr/local/bin/",
      "chmod +x /usr/local/bin/traefik",

      # 5. Build Traefik's system environment parameters
      "mkdir -p /etc/traefik/dynamic",
      "mkdir -p /var/log/traefik",
      "mv /tmp/traefik.yml /etc/traefik/traefik.yml",
      "mv /tmp/traefik.service /etc/systemd/system/traefik.service",

      # 🔐 FIX: Dynamically download the public cluster CA cert directly from the K3s control node API over the network
      "curl -k https://192.168.50.185:6443/cacert > /etc/traefik/k3s-ca.crt",

      "touch /etc/traefik/acme.json",
      "chmod 600 /etc/traefik/acme.json",

      # 6. Start the native edge routing engine
      "systemctl daemon-reload",
      "systemctl enable --now traefik"
    ]
  }
}