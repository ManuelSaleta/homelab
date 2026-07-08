# ==============================================================================
# MOTHERSHIP HOMELAB GLOBAL VARIABLE DEFINITIONS
# ==============================================================================

# Kubernetes Directory Paths
INFRA_DIR := kubernetes/infrastructure
APPS_DIR  := kubernetes/applications

# Terraform & Packer Directory Paths
TERRAFORM_DIR := terraform/vm_provisioning
PACKER_DIR    := terraform/vm_provisioning/packer-k3s

# Packer Automation Variables
PACKER_FILE := ubuntu-vm-k3s.pkr.hcl
PACKER_VARS := packer.pkrvars.hcl

# Terraform Specific Resource Targets
WORKER_TARGET  := proxmox_virtual_environment_vm.k3s_worker
CONTROL_TARGET := proxmox_virtual_environment_vm.k3s_control

# Default goal when running 'make' without targets
.DEFAULT_GOAL := help

# ==============================================================================
# GLOBAL PHONY DECLARATIONS
# ==============================================================================
.PHONY: help init-all clean-all
.PHONY: p-init p-validate p-build p-debug p-clean
.PHONY: t-init t-validate t-clean t-plan-infra t-apply-infra t-plan-k3s t-apply-k3s
.PHONY: infra-up infra-down infra-status
.PHONY: apps-up apps-down apps-status pihole-up pihole-down homepage-up homepage-down grafana-up grafana-down
.PHONY: wait-for-cluster deploy-all redeploy-workers redeploy-all destroy-workers destroy-manager destroy-all
.PHONY: install-loki install-alloy install-promstack grafana-pass promstack-install-all promstack-clean

# ==============================================================================
# 📋 HELP MENU SYSTEM
# ==============================================================================
help: ## Show this interactive help menu with descriptions
	@echo "========================================================================"
	@echo "               MOTHERSHIP HOMELAB MASTER AUTO-TUNER                     "
	@echo "========================================================================"
	@echo "Global Operations:"
	@echo "  make init-all         - Initialize both Packer and Terraform toolchains"
	@echo "  make clean-all        - Wipe local build caches and temporary footprints"
	@echo ""
	@echo "Packer (Golden Image Baking):"
	@echo "  make p-init           - Initialize Packer plugins"
	@echo "  make p-validate       - Validate Packer syntax and environment configs"
	@echo "  make p-build          - Bake fresh production VM template on Proxmox"
	@echo "  make p-debug          - Run interactive debug image build"
	@echo "  make p-clean          - Purge temporary Packer caches"
	@echo ""
	@echo "Terraform (Compute Provisioning):"
	@echo "  make t-init           - Initialize OpenTofu/Terraform modules"
	@echo "  make t-validate       - Code format and check syntax architecture"
	@echo "  make t-plan-infra     - Preview compute infrastructure changes only"
	@echo "  make t-apply-infra    - Provision Proxmox VMs (Control, Workers, NAS)"
	@echo "  make t-plan-k3s       - Preview cluster secret/config-map injection"
	@echo "  make t-apply-k3s      - Ship orchestration environment configurations"
	@echo ""
	@echo "Kubernetes Core Infrastructure:"
	@echo "  make infra-up         - Deploy MetalLB, Tailscale, Cloudflare tunnels"
	@echo "  make infra-down       - Remove core network infrastructure workloads"
	@echo "  make infra-status     - Check networking stack resource health"
	@echo ""
	@echo "Kubernetes Shared Applications:"
	@echo "  make apps-up          - Ship entire user-facing cluster software stack"
	@echo "  make apps-down        - Complete cluster software stacks wipe"
	@echo "  make apps-status      - Check status of running application pods"
	@echo "  make pihole-up/down   - Target deployment specifically for Pi-hole"
	@echo "  make homepage-up/down - Target deployment specifically for Homepage"
	@echo "  make grafana-up/down  - Target deployment specifically for Grafana layer"
	@echo ""
	@echo "Cluster Lifecycle Control Matrix:"
	@echo "  make deploy-all       - Orchestrate full stack: compute provisioning up to configs"
	@echo "  make redeploy-workers - Tear down environments, validate, and rebuild"
	@echo "  make redeploy-all     - Ultimate sequence: Nuke, Re-bake Image, Spin up cluster"
	@echo "  make destroy-workers  - Target and dismantle worker pools immediately"
	@echo "  make destroy-manager  - Target and dismantle control-plane node immediately"
	@echo "  make destroy-all      - Safely prompt and nuke entire cluster ecosystem"
	@echo ""
	@echo "Cluster Monitoring & Observability Stack:"
	@echo "  make promstack-install-all - Staggered full deploy: Operator -> Loki -> Alloy"
	@echo "  make install-promstack     - Spin up Kube-Prometheus-Stack core infrastructure"
	@echo "  make install-loki          - Deploy Grafana Loki log aggregation engine"
	@echo "  make install-alloy         - Deploy Grafana Alloy telemetry collection agent"
	@echo "  make promstack-clean       - Complete teardown of Loki, Alloy, and Prom-Stack"
	@echo "  make grafana-pass          - Fetch and decode the active Grafana admin password"
	@echo "========================================================================"
	@echo ""
	@echo "Parsed Inline Macro Tasks:"
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'

# ==============================================================================
# ⚡ SUPER PIPELINES (CROSS-DOMAIN PIPELINES)
# ==============================================================================

init-all: p-init t-init ## Total Workspace Initialization: Initialize both Packer and Terraform plugins
	@echo "✨ Whole workspace toolchains fully initialized."

clean-all: p-clean t-clean ## Total Workspace Purge: Wipe local Packer caches and temporary Terraform files
	@echo "🧹 All local build and cache matrices have been swept completely clean."

deploy-all: t-apply-infra wait-for-cluster t-apply-k3s ## Full Stack Deploy: Build VMs, wait for health check, push configs
	@echo "🚀 Full environment provisioned and ready for operations!"

redeploy-workers: ## Teardown infrastructure, validate syntax, and deploy fresh via phased pipeline
	@echo "🚨 Kicking off a full infrastructure regeneration cycle..."
	-$(MAKE) destroy-all
	$(MAKE) t-validate
	$(MAKE) deploy-all
	@echo "🎯 Fresh deployment layer successfully executed."

redeploy-all: ## Complete Lifecycle Reset: Nuke environment, re-bake Packer template, and spin up cluster
	@echo "🚀 Initiating full stack reset (Teardown -> Re-bake Image -> Deploy)..."
	-$(MAKE) destroy-all
	$(MAKE) p-build
	$(MAKE) t-init
	$(MAKE) deploy-all
	@echo "✨ Whole setup regenerated from bare-metal template up to cluster."

# ==============================================================================
# 🛠️ PACKER TARGETS (GOLDEN IMAGE BAKING)
# ==============================================================================

p-init: ## Initialize Packer plugins and dependencies
	@echo "=> Initializing Packer toolchain..."
	cd $(PACKER_DIR) && packer init $(PACKER_FILE)

p-validate: p-init ## Check the Packer syntax and configuration variables
	@echo "=> Validating Packer configuration..."
	cd $(PACKER_DIR) && packer validate -var-file="$(PACKER_VARS)" $(PACKER_FILE)

p-build: p-validate ## Standard automated VM template build on Proxmox
	@echo "=> Starting production Packer build..."
	cd $(PACKER_DIR) && packer build -var-file="$(PACKER_VARS)" $(PACKER_FILE)

p-debug: p-validate ## Interactive debug build (pauses on steps, keeps SSH open)
	@echo "=> Starting debug Packer build..."
	cd $(PACKER_DIR) && PACKER_LOG=1 packer build -debug -var-file="$(PACKER_VARS)" $(PACKER_FILE)

p-clean: ## Remove packer cache artifacts
	@echo "=> Cleaning packer cache..."
	rm -rf $(PACKER_DIR)/packer_cache/

# ==============================================================================
# 📦 TERRAFORM TARGETS (COMPUTE PROVISIONING)
# ==============================================================================

t-init: ## Initialize the terraform working directory and download providers
	@echo "=> Initializing OpenTofu/Terraform modules and providers..."
	terraform -C $(TERRAFORM_DIR) init

t-validate: ## Validate the underlying syntax formatting syntax architecture
	@echo "=> Formatting and validating configuration code..."
	terraform -C $(TERRAFORM_DIR) fmt
	terraform -C $(TERRAFORM_DIR) validate

t-plan-infra: ## Plan the compute/infrastructure resources only
	@echo "=> Planning infrastructure only..."
	terraform -C $(TERRAFORM_DIR) plan \
		-target=proxmox_virtual_environment_vm.k3s_control \
		-target=proxmox_virtual_environment_vm.k3s_worker \
		-target=proxmox_virtual_environment_vm.micro_nas

t-apply-infra: ## Apply infrastructure changes with auto-approval
	@echo "=> Applying infrastructure only..."
	terraform -C $(TERRAFORM_DIR) apply --auto-approve \
		-target=proxmox_virtual_environment_vm.k3s_control \
		-target=proxmox_virtual_environment_vm.k3s_worker \
		-target=proxmox_virtual_environment_vm.micro_nas

t-plan-k3s: ## Plan the Kubernetes control layer resources only
	@echo "=> Planning Kubernetes configuration layer..."
	terraform -C $(TERRAFORM_DIR) plan \
		-target=kubernetes_secret_v1.cloudflare_tunnel_secret \
		-target=kubernetes_secret_v1.pihole_secret \
		-target=kubernetes_secret_v1.proxmox_secret \
		-target=kubernetes_secret_v1.grafana-secret \
		-target=kubernetes_secret_v1.tailscale_secret \
		-target=kubernetes_config_map_v1.homepage_config

t-apply-k3s: ## Apply Kubernetes configurations with auto-approval
	@echo "=> Applying Kubernetes configuration layer..."
	terraform -C $(TERRAFORM_DIR) apply --auto-approve \
		-target=kubernetes_secret_v1.cloudflare_tunnel_secret \
		-target=kubernetes_secret_v1.pihole_secret \
		-target=kubernetes_secret_v1.proxmox_secret \
		-target=kubernetes_secret_v1.grafana-secret \
		-target=kubernetes_secret_v1.tailscale_secret \
		-target=kubernetes_config_map_v1.homepage_config

wait-for-cluster: ## Block execution until the K3s cluster API endpoints respond green
	@echo "=> Waiting for K3s API to come online..."
	@until curl -k -s https://192.168.50.185:6443/readyz; do sleep 5; done
	@echo "=> Cluster is ready for configuration!"

t-clean: ## Clear out transient terraform cache footprints and local log locks
	@echo "=> Clearing local terraform execution cache..."
	rm -rf $(TERRAFORM_DIR)/.terraform/providers/
	rm -f $(TERRAFORM_DIR)/.terraform.lock.hcl

# ==============================================================================
# ☢️ LIFECYCLE DESTRUCTION CONTROLS
# ==============================================================================

destroy-workers: ## Target and destroy only the worker nodes pool instantly
	@echo "⚠️  Targeting worker node destruction..."
	terraform -C $(TERRAFORM_DIR) destroy -target="$(WORKER_TARGET)" --auto-approve

destroy-manager: ## Target and destroy only the control plane manager node instantly
	@echo "⚠️  Targeting control plane manager node destruction..."
	terraform -C $(TERRAFORM_DIR) destroy -target="$(CONTROL_TARGET)" --auto-approve

destroy-all: ## Completely tear down the entire cluster infrastructure layout (With Safety Prompt)
	@echo -n "☢️:.WARNING!.:☢️: You are about to completely nuke the entire cluster layout. Proceed? [y/N]: " && \
	read ans && [ $${ans:-N} = y ] || [ $${ans:-N} = Y ] || [ $${ans:-N} = yes ] || [ $${ans:-N} = YES ] || \
	(echo "❌ Destruction aborted." && exit 1)
	terraform -C $(TERRAFORM_DIR) destroy --auto-approve

# ==============================================================================
# 🌐 KUBERNETES INFRASTRUCTURE CORE
# ==============================================================================

infra-up:
	@echo "🚀 Deploying Cluster Infrastructure Core Layers..."
	kubectl apply -f $(INFRA_DIR)/metallb-config.yaml
	kubectl apply -f $(INFRA_DIR)/tailscale-config.yaml
	kubectl apply -f $(INFRA_DIR)/cloudflared-tunnel.yaml

infra-down:
	@echo "⚠️ Tearing down Core Infrastructure Layers..."
	kubectl delete -f $(INFRA_DIR)/cloudflared-tunnel.yaml --ignore-not-found
	kubectl delete -f $(INFRA_DIR)/metallb-config.yaml --ignore-not-found
	kubectl delete -f $(INFRA_DIR)/tailscale-config.yaml --ignore-not-found

infra-status:
	@echo "🔍 Checking Infrastructure Workloads..."
	kubectl get pods,svc,endpointslices -n networking

# ==============================================================================
# 🚀 KUBERNETES INDIVIDUAL & GLOBAL APPLICATION GROUPS
# ==============================================================================

pihole-up:
	@echo "🎯 Deploying Pi-hole DNS Engine..."
	kubectl apply -f $(APPS_DIR)/pihole/pihole-deployment.yaml

pihole-down:
	@echo "💥 Removing Pi-hole Deployment..."
	kubectl delete -f $(APPS_DIR)/pihole/pihole-deployment.yaml --ignore-not-found

homepage-up:
	@echo "🏠 Deploying Homepage Dashboard..."
	kubectl apply -f $(APPS_DIR)/homepage/homepage-deployment.yaml

homepage-down:
	@echo "💥 Removing Homepage Dashboard..."
	kubectl delete -f $(APPS_DIR)/homepage/homepage-deployment.yaml --ignore-not-found

grafana-up:
	@echo "📊 Deploying Grafana Exposure Layer..."
	kubectl apply -f $(APPS_DIR)/monitoring/prometheus-stack.yaml

grafana-down:
	@echo "💥 Removing Grafana Exposure Layer..."
	kubectl delete -f $(APPS_DIR)/monitoring/prometheus-stack.yaml --ignore-not-found

apps-up: pihole-up homepage-up grafana-up ## Deploy all applications at once
	@echo "✅ All applications applied successfully."

apps-down: ## Tear down all cluster workloads with a safety step
	@echo "🛑 WARNING: You are about to wipe all apps. Press Ctrl+C to abort, or Enter to continue..."
	@read _
	$(MAKE) pihole-down homepage-down grafana-down

apps-status:
	@echo "🔍 Checking Application Status..."
	kubectl get pods,deployments,ingress -n networking



# ==============================================================================
#  📊 MONITORING
# ==============================================================================

NAMESPACE=monitoring

# The 'all' recipe now staggers the deployment
promstack-install-all: install-promstack
	@echo "--- Waiting for Prometheus stack to be ready ---"
	@kubectl rollout status deployment/promstack-kube-prometheus-operator -n $(NAMESPACE) --timeout=300s
	@$(MAKE) install-loki
	@echo "--- Waiting for Loki to be ready ---"
	@kubectl rollout status statefulset/my-loki -n $(NAMESPACE) --timeout=300s
	@$(MAKE) install-alloy

install-loki: ## Install Loki via Helm with correct path values
	helm upgrade --install my-loki grafana/loki -n $(NAMESPACE) --create-namespace -f $(APPS_DIR)/monitoring/loki-values.yaml

install-alloy: ## Install Grafana Alloy via Helm with correct path values
	helm upgrade --install my-alloy grafana/alloy -n $(NAMESPACE) -f $(APPS_DIR)/monitoring/alloy-values.yaml

install-promstack: ## Install Kube-Prometheus-Stack via Helm with correct path values
	helm upgrade --install promstack prometheus-community/kube-prometheus-stack -n $(NAMESPACE) --create-namespace -f $(APPS_DIR)/monitoring/prometheus-values.yaml

promstack-clean:
	helm uninstall my-loki my-alloy promstack -n $(NAMESPACE)

grafana-pass:
	@echo "Grafana Admin Password: $(shell kubectl get secret --namespace $(NAMESPACE) promstack-grafana -o jsonpath="{.data.admin-password}" | base64 --decode ; echo)"
