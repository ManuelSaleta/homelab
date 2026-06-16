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
    TS_AUTHKEY = var.tailscale_auth_key
  }
}

# 🔐 4. CLOUDFLARE TUNNEL EDGE INGRESS SECRET
resource "kubernetes_secret_v1" "cloudflare_tunnel_secret" {
  metadata {
    name      = "cloudflare-tunnel-secret"
    namespace = "networking"
  }

  type = "Opaque"

  data = {
    # 🎯 Pulls your cloudflared tunnel credentials JSON or token from tfvars
    "credentials.json" = var.cloudflare_tunnel_token
  }
}

# 2. Core Infrastructure Secrets (e.g., Pi-hole Admin Access)
resource "kubernetes_secret_v1" "pihole_secret" {
  metadata {
    name      = "pihole-secret"
    namespace = "networking" # Matches your Homepage dashboard deployment namespace
  }

  type = "Opaque"

  data = {
    # 🎯 Pulls your "AdminHomelabPass123" securely out of plain-text YAML
    PIHOLE_PASSWORD = var.pihole_admin_password
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