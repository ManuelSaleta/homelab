# K3s Worker CPU Model (`pclmulqdq`) & `k3s-agent` Token Guard

## 1. Worker CPU Model (`Skylake-Client-noTSX-IBRS`)

### Context
QEMU's `x86-64-v2-AES` virtual CPU type exposes `aes` (AES-NI) and `sse4_2`, but omits `pclmulqdq` (`pclmul`), which is excluded from the standardized `x86-64-v2`/`v3`/`v4` psABI tiers. Binaries compiled with `pclmul` enabled (such as the Antigravity CLI `agy` inside `jarvis-sharp`) fail immediately on worker nodes with:

```text
FATAL ERROR: This binary was compiled with pclmul enabled, but this feature is not available on this processor (go/sigill-fail-fast).
```

### Configuration
In `terraform/vm_provisioning/workers.tf`, worker VMs use `Skylake-Client-noTSX-IBRS` (matching `mothership`'s 13th Gen Intel Core i7-13620H, which supports `pclmulqdq`, `aes`, `avx2`, and `IBRS` while having no TSX or AVX-512):

```hcl
cpu {
  cores = 2
  type  = "Skylake-Client-noTSX-IBRS"
}
```

> **Note:** Changing a VM's CPU type in Proxmox requires a **cold Shutdown/Stop + Start** (not an in-guest reboot) for QEMU to relaunch with the updated CPU flags.

---

## 2. `k3s-agent` Token Guard (`ExecStartPre`)

### Context
When a Proxmox VM's hardware config changes (such as updating the CPU type), `cloud-init` can re-run `write_files` on the next boot. Previously, `workers.tf` wrote directly to `/etc/rancher/k3s/config.yaml`, which meant an empty `k3s_share_token` in a regenerated cloud-init snippet would overwrite the active token with `token: ""` and leave `k3s-agent` stuck in `activating (start)` with:

```text
Failed to validate connection to cluster at https://192.168.1.185:6443: token must not be empty
```

### How the Guard Works
1. **Terraform Validation (`variables.tf`)**: `k3s_share_token` enforces a non-empty value at plan/apply time.
2. **Staged Cloud-Init Output (`workers.tf`)**: `cloud-init` writes to `/etc/rancher/k3s/config.yaml.cloudinit` rather than directly overwriting `/etc/rancher/k3s/config.yaml`.
3. **Self-Healing `ExecStartPre` (`/usr/local/bin/k3s-token-guard.sh`)**: Runs automatically before `k3s-agent` starts via `/etc/systemd/system/k3s-agent.service.d/10-token-guard.conf`:
   - If `/etc/rancher/k3s/config.yaml.cloudinit` contains a non-empty `token`, it promotes it to `/etc/rancher/k3s/config.yaml` and updates `/etc/rancher/k3s/config.yaml.bak`.
   - If `/etc/rancher/k3s/config.yaml` is missing or has an empty `token: ""`, it restores `/etc/rancher/k3s/config.yaml` from `/etc/rancher/k3s/config.yaml.bak`.
   - If `/etc/rancher/k3s/config.yaml` has a valid non-empty token, it keeps `/etc/rancher/k3s/config.yaml.bak` synced (`0600`).
