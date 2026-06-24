# ==============================================================================
# HOMELAB IAC AUTOMATION MASTER HUB (PACKER + TERRAFORM)
# ==============================================================================

# Target VM mappings matching configuration definitions
WORKER_TARGET  := proxmox_virtual_environment_vm.k3s_worker
CONTROL_TARGET := proxmox_virtual_environment_vm.k3s_control

# Packer sub-component variables
PACKER_DIR     := packer-k3s
PACKER_FILE    := ubuntu-vm-k3s.pkr.hcl
PACKER_VARS    := packer.pkrvars.hcl

# Core Phonies
.PHONY: help init-all clean-all

# Packer Phonies
.PHONY: p-init p-validate p-build p-debug p-clean

# Terraform Phonies
.PHONY: t-init t-plan-infra t-apply-infra t-plan-k3s t-apply-k3s wait-for-cluster deploy-all t-validate t-clean

# Lifecycle Phonies
.PHONY: destroy-workers destroy-manager destroy-all redeploy-workers redeploy-all

# Default target when you just run 'make'
.DEFAULT_GOAL := help

# ==============================================================================
# ⚡ SUPER COMMANDS (ENCOMPASSING PIPELINES)
# ==============================================================================

init-all: p-init t-init ## Total Workspace Initialization: Initialize both Packer and Terraform plugins
	@echo "✨ Whole workspace toolchains fully initialized."

clean-all: p-clean t-clean ## Total Workspace Purge: Wipe local Packer caches and temporary Terraform files
	@echo "🧹 All local build and cache matrices have been swept completely clean."

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

# ==============================================================================
# 📦 TERRAFORM TARGETS (COMPUTE PROVISIONING)
# ==============================================================================

t-init: ## Initialize the terraform working directory and download providers
	@echo "=> Initializing OpenTofu/Terraform modules and providers..."
	terraform init

t-plan-infra: ## Plan the compute/infrastructure resources only
	@echo "=> Planning infrastructure only..."
	terraform plan \
		-target=proxmox_virtual_environment_vm.k3s_control \
		-target=proxmox_virtual_environment_vm.k3s_worker \
		-target=proxmox_virtual_environment_vm.micro_nas

t-apply-infra: ## Apply infrastructure changes with auto-approval
	@echo "=> Applying infrastructure only..."
	terraform apply --auto-approve \
		-target=proxmox_virtual_environment_vm.k3s_control \
		-target=proxmox_virtual_environment_vm.k3s_worker \
		-target=proxmox_virtual_environment_vm.micro_nas

t-plan-k3s: ## Plan the Kubernetes control layer resources only
	@echo "=> Planning Kubernetes configuration layer..."
	terraform plan \
		-target=kubernetes_secret_v1.cloudflare_tunnel_secret \
		-target=kubernetes_secret_v1.pihole_secret \
		-target=kubernetes_secret_v1.proxmox_secret \
		-target=kubernetes_secret_v1.grafana-secret \
		-target=kubernetes_secret_v1.tailscale_secret \
		-target=kubernetes_config_map_v1.homepage_config

t-apply-k3s: ## Apply Kubernetes configurations with auto-approval
	@echo "=> Applying Kubernetes configuration layer..."
	terraform apply --auto-approve \
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

deploy-all: t-apply-infra wait-for-cluster t-apply-k3s ## Full Stack Deploy: Build VMs, wait for health check, push configs

t-validate: ## Validate the underlying syntax formatting syntax architecture
	@echo "=> Formatting and validating configuration code..."
	terraform fmt
	terraform validate

t-clean: ## Clear out transient terraform cache footprints and local log locks
	@echo "=> Clearing local terraform execution cache..."
	rm -rf .terraform/providers/
	rm -f .terraform.lock.hcl

# ==============================================================================
# THE ULTIMATE "NUKE AND REBOOT" MATRIX PIPELINES
# ==============================================================================

redeploy-workers: ## Teardown infrastructure, validate syntax, and deploy fresh via phased pipeline
	@echo "🚨 Kicking off a full infrastructure regeneration cycle..."
	-$(MAKE) destroy-all
	$(MAKE) t-validate
	$(MAKE) deploy-all
	@echo "🎯 Fresh deployment layer successfully executed."

redeploy-all: ## Complete Lifecycle Reset: Nuke environment, re-bake Packer template, and spin up cluster
	@echo "🚀 Initiating full stack reset (Teardown -> Re-bake Image -> Deploy)..."
	-$(MAKE) destroy-all
# 	$(MAKE) p-build
	$(MAKE) t-init
	$(MAKE) deploy-all
	@echo "✨ Whole setup regenerated from bare-metal template up to cluster."

# ==============================================================================
# ☢️ LIFECYCLE DESTRUCTION CONTROLS
# ==============================================================================

destroy-workers: ## Target and destroy only the worker nodes pool instantly
	@echo "⚠️  Targeting worker node destruction..."
	terraform destroy -target="$(WORKER_TARGET)" --auto-approve

destroy-manager: ## Target and destroy only the control plane manager node instantly
	@echo "⚠️  Targeting control plane manager node destruction..."
	terraform destroy -target="$(CONTROL_TARGET)" --auto-approve

destroy-all: ## Completely tear down the entire cluster infrastructure layout (With Safety Prompt)
	@echo -n "☢️:.WARNING!.:☢️: You are about to completely nuke the entire cluster layout. Proceed? [y/N]: " && \
	read ans && [ $${ans:-N} = y ] || [ $${ans:-N} = Y ] || [ $${ans:-N} = yes ] || [ $${ans:-N} = YES ] || \
	(echo "❌ Destruction aborted." && exit 1)
	terraform destroy --auto-approve

# ==============================================================================
# THE ULTIMATE "NUKE AND REBOOT" MATRIX PIPELINES
# ==============================================================================

redeploy-workers: ## Completely teardown, wipe, validate syntax, and deploy fresh in one execution run
	@echo "🚨 Kicking off a full infrastructure regeneration cycle..."
	-$(MAKE) destroy-all
	$(MAKE) t-validate
	$(MAKE) t-apply
	@echo "🎯 Fresh deployment layer successfully executed."

redeploy-all: ## Complete Pipeline: Teardown, rebuild the Packer golden image, and spin up new VMs
	@echo "🚀 Initiating full stack reset (Teardown -> Re-bake Image -> Deploy)..."
	-$(MAKE) destroy-all
	$(MAKE) p-build
	$(MAKE) t-init
	$(MAKE) t-apply
	@echo "✨ Whole setup regenerated from bare-metal template up to cluster."

# ==============================================================================
# HELP MENU NAVIGATION
# ==============================================================================

help: ## Show this help menu with descriptions
	@echo "Available Homelab Infrastructure Commands:"
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'