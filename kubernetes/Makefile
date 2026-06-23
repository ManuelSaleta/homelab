# Define Paths to match the directory structure
INFRA_DIR := kubernetes/infrastructure
APPS_DIR  := applications

# Default help menu showing available commands
.PHONY: help
help:
	@echo "========================================================================"
	@echo "MOTHERSHIP HOMELAB CLUSTER MANAGEMENT TOOL"
	@echo "========================================================================"
	@echo "Infrastructure Operations:"
	@echo "  make infra-up         - Deploy all core network and tunnel infrastructure"
	@echo "  make infra-down       - Remove core infrastructure workloads"
	@echo "  make infra-status     - Check the health of infrastructure components"
	@echo ""
	@echo "Application Operations:"
	@echo "  make apps-up          - Deploy all user-facing applications"
	@echo "  make apps-down        - Delete all applications"
	@echo "  make apps-status      - Check status of running application pods"
	@echo ""
	@echo "Targeted Application Operations:"
	@echo "  make pihole-up        - Deploy or update Pi-hole"
	@echo "  make pihole-down      - Tear down Pi-hole"
	@echo "  make homepage-up      - Deploy or update Homepage dashboard"
	@echo "  make homepage-down    - Tear down Homepage dashboard"
	@echo "  make grafana-up       - Deploy or update Grafana exposure layer"
	@echo "  make grafana-down     - Tear down Grafana exposure layer"
	@echo "========================================================================"

# ==============================================================================
# INFRASTRUCTURE CORE
# ==============================================================================
.PHONY: infra-up infra-down infra-status

infra-up:
	@echo "🚀 Deploying Cluster Infrastructure..."
	kubectl apply -f $(INFRA_DIR)/metallb-config.yaml
	kubectl apply -f $(INFRA_DIR)/tailscale-config.yaml
	kubectl apply -f $(INFRA_DIR)/cloudflared-tunnel.yaml

infra-down:
	@echo "⚠️ Tearing down Core Infrastructure..."
	kubectl delete -f $(INFRA_DIR)/cloudflared-tunnel.yaml --ignore-not-found
	kubectl delete -f $(INFRA_DIR)/metallb-config.yaml --ignore-not-found
	kubectl delete -f $(INFRA_DIR)/tailscale-config.yaml --ignore-not-found

infra-status:
	@echo "🔍 Checking Infrastructure Workloads..."
		


# ==============================================================================
# INDIVIDUAL APPLICATIONS
# ==============================================================================
.PHONY: pihole-up pihole-down homepage-up homepage-down grafana-up grafana-down

pihole-up:
	@echo "🎯 Deploying Pi-hole DNS Engine..."
	kubectl apply -f $(APPS_DIR)/pihole-deployment.yaml

pihole-down:
	@echo "💥 Removing Pi-hole Deployment..."
	kubectl delete -f $(APPS_DIR)/pihole-deployment.yaml --ignore-not-found

homepage-up:
	@echo "🏠 Deploying Homepage Dashboard..."
	kubectl apply -f $(APPS_DIR)/homepage/homepage-deployment.yaml

homepage-down:
	@echo "💥 Removing Homepage Dashboard..."
	kubectl delete -f $(APPS_DIR)/homepage/homepage-deployment.yaml --ignore-not-found

grafana-up:
	@echo "📈 Deploying Grafana Exposure Configs..."
	kubectl apply -f $(APPS_DIR)/grafana-exposure.yaml

grafana-down:
	@echo "💥 Removing Grafana Exposure Configs..."
	kubectl delete -f $(APPS_DIR)/grafana-exposure.yaml --ignore-not-found


# ==============================================================================
# GLOBAL APPLICATION GROUPS
# ==============================================================================
.PHONY: apps-up apps-down apps-status

apps-up: pihole-up homepage-up grafana-up
	@echo "✅ All applications applied successfully."

apps-down:
	@echo "🛑 WARNING: You are about to wipe all apps. Press Ctrl+C to abort, or Enter to continue..."
	@read _
	@make pihole-down homepage-down grafana-down

apps-status:
	@echo "🔍 Checking Application Status..."
	kubectl get pods,deployments,ingress -n networking