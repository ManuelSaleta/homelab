# ==============================================================================
# Makefile for Homelab Container Provisioning (Media Acquisition Stack)
# ==============================================================================
COMPOSE_FILE := docker-compose.yaml
COMPOSE_CMD  ?= $(shell docker info >/dev/null 2>&1 && echo "docker compose" || echo "podman compose")

# Route rootless Podman to user socket if Docker daemon is not directly accessible
export DOCKER_HOST ?= unix://$(or $(XDG_RUNTIME_DIR),/run/user/$(shell id -u))/podman/podman.sock

.PHONY: help config up down logs ps clean-staging

help:
	@echo "Homelab Media Acquisition Stack Targets (Engine: $(COMPOSE_CMD)):"
	@echo "  make config   - Validate and render the merged Compose configuration"
	@echo "  make up       - Start the nordlynx, prowlarr, qbittorrent, and vpn-media-sync stack"
	@echo "  make down     - Stop and remove the containers"
	@echo "  make logs     - Tail logs across all containers in the stack"
	@echo "  make ps       - List running containers and tunnel status"

config:
	$(COMPOSE_CMD) -f $(COMPOSE_FILE) config

up:
	$(COMPOSE_CMD) -f $(COMPOSE_FILE) up -d --build

down:
	$(COMPOSE_CMD) -f $(COMPOSE_FILE) down

logs:
	$(COMPOSE_CMD) -f $(COMPOSE_FILE) logs -f

ps:
	$(COMPOSE_CMD) -f $(COMPOSE_FILE) ps
