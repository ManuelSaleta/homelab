# ==============================================================================
# Variables
# ==============================================================================
PACKER_FILE := ubuntu-vm-k3s.pkr.hcl
VAR_FILE    := packer.pkrvars.hcl

# Default target when you just run 'make'
.DEFAULT_GOAL := help

# ==============================================================================
# Phony Targets (Commands that don't represent actual files)
# ==============================================================================
.PHONY: all init validate build build-debug clean help


# Default target when running just 'make'
all: validate build

## init: Initialize Packer plugins and dependencies
init:
	@echo "=> Initializing Packer..."
	packer init $(PACKER_FILE)

## validate: Check the Packer syntax and configuration variables
validate: init
	@echo "=> Validating Packer configuration..."
	packer validate -var-file="$(VAR_FILE)" $(PACKER_FILE)

## build: Standard automated VM template build
build: validate
	@echo "=> Starting production Packer build..."
	packer build -var-file="$(VAR_FILE)" $(PACKER_FILE)

## build-debug: Interactive debug build (pauses on steps, keeps SSH open on failure)
build-debug: validate
	@echo "=> Starting debug Packer build..."
	PACKER_LOG=1 packer build -debug -var-file="$(VAR_FILE)" $(PACKER_FILE)

## clean: Remove packer cache artifacts
clean:
	@echo "=> Cleaning packer cache..."
	rm -rf packer_cache/

help: ## Show this help menu with descriptions
	@echo "Available commands:"
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'