# 🛸 Mothership Homelab: AI Workspace & Architectural Guidelines

This file serves as the system instruction context for AI collaborators working within the `Mothership` IaC homelab repository. It outlines project conventions, state boundaries, safety constraints, and deployment patterns.

---

## Repository Philosophy & Core Principles

1. **Zero-Intervention Pipeline**: IaC manifests must favor automated provisioners (Packer -> Terraform -> K3s) over manual runtime steps.
2. **Cluster & Storage Decoupling**: Compute is transient; persistent data is static. Persistent data lives **outside** the K3s lifecycle on dedicated infrastructure (`micro-nas` at `192.168.50.250`).
3. **NFS Over Local Storage**: Stateful workloads in K3s must consume remote NFS exports from `micro-nas` rather than `hostPath` or `local` persistent volumes to ensure cross-node mobility.
4. **Declarative State Safety**: Never generate code or instructions that mutate cluster resources without explicit namespace isolation (`media`, `networking`, `monitoring`).

---

## Directory Anatomy & Context Mapping

```text
.
├── terraform/
│   └── vm_provisioning/
│       ├── packer-k3s/     # Immutable Ubuntu 24.04 golden image templates (ID 777)
│       └── *.tf            # Proxmox compute resources (Control, Workers, micro-nas)
├── kubernetes/
│   ├── infrastructure/     # Core L2/L3 networking (MetalLB, Traefik, Ingress-Nginx)
│   └── applications/       # Workload manifests grouped by namespace
│       ├── media/          # Navidrome, Plex, etc.
│       ├── networking/     # Pi-hole, Cloudflared, Homepage, Karakeep
│       └── monitoring/     # Prometheus, Grafana, Loki, Alloy
└── Makefile                # Centralized orchestration targets
```

---

## Security & Immutable

- ConstraintsPrivate Key & Variable Isolation: Never hardcode or commit .tfvars files, private API tokens, or SSH keys.
- PersistentVolume Immutability: K8s PersistentVolume sources (e.g., changing hostPath to nfs) cannot be patched in-place. Always instruct deletion (`kubectl delete pv/pvc`) followed by re-creation.
- Storage Pathing Standards:
  - NAS Export Target: `/mnt/export/storage/<application-name>` on `192.168.50.250`.
  - Application Patterns: `/mnt/export/storage/vaultwarden`, `/mnt/export/storage/navidrome/data`, `/mnt/export/storage/navidrome/music`, `/mnt/export/storage/obsidian`.

## Kubernetes Manifest Standards

---

When generating or editing Kubernetes manifests in this repository, strictly adhere to these patterns:

- NFS PV / PVC Blueprint: `YAML`

```yml
    apiVersion: v1
    kind: PersistentVolume
    metadata:
      name: <app>-data-pv
    spec:
      capacity:
        storage: <size>
      accessModes:
        - ReadOnlyMany # or ReadWriteMany / ReadWriteOnce based on app requirements
      persistentVolumeReclaimPolicy: Retain
      nfs:
        server: 192.168.50.250
        path: /mnt/<target-export-folder>
    ---
    apiVersion: v1
    kind: PersistentVolumeClaim
    metadata:
      name: <app>-data-pvc
      namespace: <namespace>
    spec:
      accessModes:
        - ReadOnlyMany
      resources:
        requests:
          storage: <size>
      volumeName: <app>-data-pv
```

### Ingress & Homepage Integration Annotations:

- Always include gethomepage.dev annotations on Ingress rules to ensure automatic service discovery on the cluster dashboard: `YAML`

```yml
metadata:
  annotations:
    gethomepage.dev/enabled: "true"
    gethomepage.dev/name: "<AppName>"
    gethomepage.dev/group: "<Category>"
    gethomepage.dev/icon: "<icon-name>"
    gethomepage.dev/href: "https://<app>[.freesalty.com/](https://.freesalty.com/)"
    traefik.ingress.kubernetes.io/router.entrypoints: web, websecure
```

## Quick Diagnostics & Common Operations:

- Refer to the `Makefile` for common operations.
