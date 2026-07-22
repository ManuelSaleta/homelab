# ==============================================================================
# Purpose: Dynamically provision Kubernetes primitives from Terraform Variables
# Prevents raw sensitive secret tokens from being tracked in plain text git YAML files.
# ==============================================================================

# 1. Tailscale Subnet/Mesh Network Secret
resource "kubernetes_secret_v1" "tailscale_secret" {
  metadata {
    name      = "tailscale-secret"
    namespace = "networking"
  }

  type = "Opaque"

  data = {
    TS_AUTH_KEY         = var.tailscale_auth_key
    TS_API_TOKEN        = var.tailscale_api_token
    TS_NAS_DEVICE_ID    = var.tailscale_device_id_nas
    TS_LAPTOP_DEVICE_ID = var.tailscale_device_id_mac
  }
}

# 🔐 1. TRAEFIK DNS-01 CHALLENGE SECRET (kube-system namespace)
resource "kubernetes_secret_v1" "traefik_cloudflare_api_token" {
  metadata {
    name      = "cloudflare-dns-api-token"
    namespace = "kube-system"
  }

  type = "Opaque"

  data = {
    # Traefik expects the literal string 'CF_API_TOKEN' as its environment variable key
    CF_API_TOKEN = var.cloudflare_dns_api_token
  }
}

# 🔐 2. CLOUDFLARE TUNNEL EDGE INGRESS SECRET
resource "kubernetes_secret_v1" "cloudflare_tunnel_secret" {
  metadata {
    name      = "cloudflare-tunnel-secret"
    namespace = "networking"
  }

  type = "Opaque"

  data = {
    # 🎯 Pulls the cloudflared tunnel credentials JSON or token from tfvars
    CF_TUNNEL_TOKEN = var.cloudflare_tunnel_token
    CF_API_TOKEN    = var.cloudflare_api_token
    CF_TUNNEL_ID    = var.cloudflare_tunnel_id
    CF_ACCOUNT_ID   = var.cloudflare_account_id
  }
}

# 2. Core Infrastructure Secrets (e.g., Pi-hole Admin Access)
resource "kubernetes_secret_v1" "pihole_secret" {
  metadata {
    name      = "pihole-secret"
    namespace = "networking" # Matches the Homepage dashboard deployment namespace
  }

  type = "Opaque"

  data = {
    # 🎯 Pulls the "AdminHomelabPass123" securely out of plain-text YAML
    PIHOLE_PASSWORD = var.pihole_admin_password
    PIHOLE_API_KEY  = var.pihole_api_key
  }
}

# 2. Core Infrastructure Secrets (e.g., Proxmox Homepage Widget Access)
resource "kubernetes_secret_v1" "proxmox_secret" {
  metadata {
    name      = "proxmox-secret"
    namespace = "networking" # Matches the Homepage dashboard deployment namespace
  }

  type = "Opaque"

  data = {
    # 🎯 Pulls the "AdminHomelabPass123" securely out of plain-text YAML
    PROXMOX_WIDGET_PASSWORD = var.proxmox_vm_auditor_password
    PROXMOX_URL             = var.proxmox_endpoint
    PROXMOX_NODE_NAME       = var.proxmox_node_name
    PROXMOX_USER            = var.proxmox_api_user
    PROXMOX_TOKEN_ID        = var.proxmox_api_token_id # e.g., "root@pam!homepage"
    PROXMOX_TOKEN_SECRET    = var.proxmox_api_token_secret
  }
}

resource "kubernetes_secret_v1" "grafana-secret" {
  metadata {
    name      = "grafana-secret"
    namespace = "networking" # Enables Homepage Grafana Widget, thus belongs to the networking namespace
  }

  type = "Opaque"

  data = {
    GRAFANA_API_KEY  = var.grafana_api_key
    GRAFANA_USERNAME = var.grafana_username
    GRAFANA_PASSWORD = var.grafana_password
  }
}

# The background image asset
resource "kubernetes_secret_v1" "homepage_background" {
  metadata {
    name      = "homepage-background"
    namespace = "networking"
  }

  binary_data = {
    # Reads the raw image from your local directory and encodes it to base64 automatically
    "homepage_background.webp" = filebase64("${path.module}/../../kubernetes/applications/homepage/config/assets/homepage_background.webp")
  }
}

# # 3. Application State Secrets (Example: Postgres or Immich Backends)
# resource "kubernetes_secret_v1" "database_secret" {
#   metadata {
#     name      = "database-secret"
#     namespace = "apps"
#   }

#   type = "Opaque"

#   data = {
#     username = "postgres"
#     password = var.database_root_password
#   }
# }
