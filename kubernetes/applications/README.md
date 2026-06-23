# Homelab Core Infrastructure Deployment Guide

This repository contains the production-ready Kubernetes manifest for deploying a unified, ad-blocking Pi-hole DNS engine inside a K3s homelab cluster. The deployment leverages MetalLB for dedicated IP allocation and local node storage for persistence.

# PIHOLE DEPLOYMENT

---

## 🏗️ Architecture & Network Layout

The configuration establishes a single, unified service instance that co-locates core DNS resolution and web administration onto one dedicated local IP address.

- **Dedicated Network Address:** `192.168.50.242`
- **DNS Resolution Interface:** Ports `53/UDP` and `53/TCP`
- **Web Administration Panel:** Port `80/TCP` (Accessible via `http://192.168.50.242/admin`)
- **Default Dashboard Password:** `AdminHomelabPass123`

---

## 💾 Storage & Scheduling Dependencies

### 1. Automated Scheduling

The manifest does **not** hardcode a specific `nodeName`. The Kubernetes Scheduler automatically evaluates cluster resource utilization and binds the pod to the optimal worker node.

### 2. HostPath Storage Warning

Because this configuration utilizes a local `hostPath` volume mapped to `/var/data/pihole/config`, **the physical configuration data resides on the machine where the pod runs.** \* If the pod is scheduled on a worker node lacking the historical configuration files, it will initialize a clean, out-of-the-box database.

- For absolute cross-node mobility, transition this storage configuration to a distributed network storage engine (e.g., Longhorn or an NFS-backed PersistentVolume) in the future.

---

## 🚀 Redeployment Instructions

Follow these precise steps from the administrative workstation to cleanly purge the old configurations, refresh the MetalLB state engine, and spin up the updated schema.

### Step 1: Clean Up Legacy Kubernetes Resources

Ensure that all old, standalone service blocks, deprecated selectors, and lingering namespaces are fully cleared from the cluster API to prevent orchestration collisions.

`````bash
# Delete the old standalone service endpoints
kubectl delete svc pihole-dns pihole-web pihole-service -n networking --ignore-not-found

# Delete the old deployment instance
kubectl delete deployment pihole pihole-dns-server -n networking --ignore-not-found
```

### Step 2: Flush Persistent Host Directories (Optional)

If you need a completely clean structural slate without inherited database logs or configuration file locks from older v5 structures, execute this cleanup directly on the primary worker machine:
Bash

# Run this on the target worker node to clear out old legacy config blocks

````bash
    sudo rm -rf /var/data/pihole/config/_.conf /var/data/pihole/config/_.toml
```

### Step 3: Apply the Manifest Configuration

Deploy the manifest file directly from the local control station:

````bash
    kubectl apply -f pihole-deployment.yaml
```

## Verification checklist

---

1. Check Network Assignment

Verify that the service has successfully shed its <pending> state and claims the target IP address assigned by MetalLB:

````bash
kubectl get svc -n networking -w
````

```text
Expected Output Snippet:
Plaintext

NAME                          TYPE           CLUSTER-IP     EXTERNAL-IP      PORT(S)                                 AGE
pihole-loadbalancer-service   LoadBalancer   10.43.x.x      192.168.50.242   53:32301/UDP,53:32301/TCP,80:30442/TCP  10s
```

2. Verify Live DNS Pipeline

Execute a directed lookup query against the newly provisioned load balancer IP to confirm the Pi-hole container is processing and answering network queries from the workstation:
Verification Checklist:

Status must return NOERROR.

    The ANSWER SECTION must contain valid address responses.

    The query round-trip time should resolve quickly (under 15ms locally).

````bash
   $ dig @192.168.50.242 google.com
````

3. Container Administration Execution (Password Updates)

To execute manual configuration modifications—such as manually setting or resetting the administrative web panel access password—tunnel directly into the application space across the network interface:

````bash
kubectl exec -it -n networking deployment/pihole-dns-server -- pihole setpassword
```
`````
