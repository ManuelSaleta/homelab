# ==============================================================================
# Helper Makefile for Terraform VM Provisioning. For Homelab
# ==============================================================================

.PHONY: plan apply destroy-workers destroy-manager destroy-all clean validate cycle help



# Target VMs matching your configuration definitions
WORKER_TARGET  := proxmox_virtual_environment_vm.k3s_worker
CONTROL_TARGET := proxmox_virtual_environment_vm.k3s_control

init: ## Initialize the terraform working directory and download providers
	terraform init
	
plan: ## Run a standard speculative configuration execution plan
	terraform plan

apply: ## Apply the configuration infrastructure changes with auto-approval
	terraform apply --auto-approve

destroy-workers: ## Target and destroy only the worker nodes pool instantly
	terraform destroy -target="$(WORKER_TARGET)" --auto-approve

destroy-manager: ## Target and destroy only the control plane manager node instantly
	terraform destroy -target="$(CONTROL_TARGET)" --auto-approve

destroy-all: ## Completely tear down the entire cluster infrastructure layout (With Safety Prompt)
	@@echo -n "☢️:.WARNING!.:☢️: You are about to completely nuke the entire cluster layout.\
	 Proceed? [y/N]: " && read ans && [ $${ans:-N} = y ] || [ $${ans:-N} = Y ] || [ $${ans:-N} = yes ] || [ $${ans:-N} = YES ] || (echo "❌ Destruction aborted." && exit 1)
	terraform destroy --auto-approve

clean: ## Clear out transient terraform cache footprints and local log locks
	rm -rf .terraform/providers/
	rm -f .terraform.lock.hcl
	@echo "🧹 Local execution caching matrices cleared."

validate: ## Validate the underlying syntax formatting syntax architecture
	terraform fmt
	terraform validate

# ==============================================================================
# THE ULTIMATE "NUKE AND REBOOT" MATRIX PIPELINE
# ==============================================================================
redeploy-workers: ## Completely teardown, wipe, validate syntax, and deploy fresh in one execution run
	@echo "🚨 Kicking off a full infrastructure regeneration cycle..."
	-$(MAKE) destroy-all
	$(MAKE) validate
	$(MAKE) apply
	@echo "🎯 Fresh deployment layer successfully executed."

help: ## Show this help menu with descriptions
	@echo "Available commands:"
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'