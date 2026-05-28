packer {
  required_plugins {
    proxmox = {
      version = "~> 1"
      source  = "github.com/hashicorp/proxmox"
    }
  }
}

# --- DECLARE VARIABLES (No defaults, values come from tfvars) ---
variable "proxmox_endpoint" { type = string }
variable "proxmox_api_token" { type = string }
variable "proxmox_api_token_id" { type = string }
variable "proxmox_api_token_secret" { type = string }
variable "proxmox_template_vm_id" { type = string }
variable "proxmox_node_name" { type = string }
variable "http_bind_address_ip" { type = string }
variable "k3s_share_token" { type = string }

# --- BUILDER CONFIGURATION ---
source "proxmox-iso" "k3s_template" {

  # Proxmox connection details
  proxmox_url              = "${var.proxmox_endpoint}api2/json"
  username                 = var.proxmox_api_token_id
  token                    = var.proxmox_api_token_secret
  node                     = var.proxmox_node_name
  insecure_skip_tls_verify = true

  # Image iso source
  boot_iso {
    type         = "scsi"
    iso_file     = "local:iso/ubuntu-24.04.4-live-server-amd64.iso"
    unmount      = true
    iso_checksum = "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e"
  }


  # VM Template details
  vm_name              = "ubuntu-k3s-template"
  vm_id                = var.proxmox_template_vm_id
  template_description = "Packer built - Ubuntu 24.04 K3s Base Image"
  tags                 = "k3s;Ubuntu;Template"

  # VM OS and Hardware details
  os              = "l26" # Linux 2.6/3.x/4.x/5.x (64-bit)
  cores           = 2     # 2 CPU cores
  memory          = 2048  # 2 GB RAM
  scsi_controller = "virtio-scsi-pci"

  disks {
    disk_size    = "20G"
    format       = "raw"
    storage_pool = "local-lvm"
    type         = "scsi"
  }

  network_adapters {
    model  = "virtio"
    bridge = "vmbr0"
  }

  # HTTP Server for Cloud-Init, serving the autoinstall config and SSH keys
  # Packer will spin up a temporary HTTP server on the host machine to serve these files during the VM build process.
  # Using the same min and max port forces it to use a specific port, which we can reference in the autoinstall config.
  http_port_min     = 8688
  http_port_max     = 8688
  http_directory    = "http"
  http_bind_address = var.http_bind_address_ip # This should be the IP of the machine running Packer, reachable by the Proxmox host and the VM during build.

  boot_command = [
    "<esc><wait3>",
    "c<wait3>",
    "linux /casper/vmlinuz autoinstall <wait>",
    "\"ds=nocloud;s=http://{{ .HTTPIP }}:{{ .HTTPPort }}/\" <wait3>",
    "---<enter><wait3>",
    "initrd /casper/initrd<enter><wait3>",
    "boot<enter>"
  ]

  #TODO: document QEMU Instructs Proxmox to provision a virtual QEMU Guest Agent interface channel
  qemu_agent = true
  communicator = "ssh"
  ssh_username = "gman"
  ssh_timeout  = "20m"

  # Point Packer directly to your local private key file on your Fedora workstation
  ssh_private_key_file = "~/.ssh/id_ed25519"
}

build {
  sources = ["source.proxmox-iso.k3s_template"]

  provisioner "shell" {
    inline = [
      "echo 'Downloading K3s installer...'",
      "curl -sfL https://get.k3s.io -o install.sh",
      "chmod +x install.sh",
      "sudo INSTALL_K3S_SKIP_ENABLE=true INSTALL_K3S_SKIP_START=true ./install.sh"
    ]
  }

provisioner "shell" {
    inline = [
      "echo 'Cleaning up system state...'",
      "sudo cloud-init clean",
      "sudo truncate -s 0 /etc/machine-id",
      "sudo rm -f /var/lib/dbus/machine-id",
      "sudo ln -s /etc/machine-id /var/lib/dbus/machine-id",
      "sudo apt-get clean",
      "rm -f ~/.bash_history"
    ]
  }
}
