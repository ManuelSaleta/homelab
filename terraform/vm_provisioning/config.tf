resource "kubernetes_config_map_v1" "homepage_config" {
  metadata {
    name      = "homepage-config"
    namespace = "networking"
  }

  data = {
    # We go up one level (..) to 'homelab', then into 'kubernetes/applications/homepage/config/'
    "settings.yaml"   = file("${path.module}/../../kubernetes/applications/homepage/config/settings.yaml")
    "services.yaml"   = file("${path.module}/../../kubernetes/applications/homepage/config/services.yaml")
    "kubernetes.yaml" = file("${path.module}/../../kubernetes/applications/homepage/config/kubernetes.yaml")
    "widgets.yaml"    = file("${path.module}/../../kubernetes/applications/homepage/config/widgets.yaml")
  }
}

