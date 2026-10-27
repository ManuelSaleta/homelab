# ==============================================================================
# Makefile for Homelab Container Provisioning (Media Acquisition Stack)
# ==============================================================================
COMPOSE_FILE := docker-compose.yaml

.PHONY: help config up down logs ps clean-staging

help:
	@echo "Homelab Media Acquisition Stack Targets:"
	@echo "  make config   - Validate and render the merged Compose configuration"
	@echo "  make up       - Start the nordlynx, prowlarr, qbittorrent, and vpn-media-sync stack"
	@echo "  make down     - Stop and remove the containers"
	@echo "  make logs     - Tail logs across all containers in the stack"
	@echo "  make ps       - List running containers and tunnel status"

config:
	docker compose -f $(COMPOSE_FILE) config

up:
	docker compose -f $(COMPOSE_FILE) up -d --build

down:
	docker compose -f $(COMPOSE_FILE) down

logs:
	docker compose -f $(COMPOSE_FILE) logs -f

ps:
	docker compose -f $(COMPOSE_FILE) ps
