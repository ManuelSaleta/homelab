## 🛠️ Kubernetes Command-Line Shortcuts

This repository utilizes standard `kubectl` workflows for cluster operations. Drop these configurations directly into your local shell configuration file (e.g., `~/.zshrc` or `~/.bashrc`) to streamline daily administration tasks.

### Core Auto-Completion

Ensure your interactive shell auto-completes resources dynamically by sourcing the native engine:

```bash
# Inject native completion architecture
source <(kubectl completion zsh)

### Context & Cluster Topology:

# Cluster Routing Switchers
alias kcontext-MOTHERSHIP="kubectl config use-context default"
alias kcurr-context="kubectl config get-contexts"

# Global System Diagnostics
alias kinfo="kubectl cluster-info"
alias kver="kubectl version --client"
alias knodes="kubectl get nodes -o wide"
alias khealth="kubectl get componentstatuses"

# Resource Lookups
alias kall-net="kubectl get all -n networking"
alias kpods="kubectl get pods -o wide"
alias kdeployments="kubectl get deployment"
alias ksvc="kubectl get svc --all-namespaces"
alias kingress="kubectl get ingress --all-namespaces"

# Ingress Controller Rules
alias kwhitelist="kubectl get configmap -n ingress-nginx-internal ingress-nginx-controller -o jsonpath='{.data.whitelist-source-range}'"


# Cluster Node Endpoint Extractors
alias kips="kubectl get nodes -o jsonpath='{.items[*].status.addresses[?(@.type == \"InternalIP\")].address}'"
alias kips-external="kubectl get nodes -o jsonpath='{.items[*].status.addresses[?(@.type == \"ExternalIP\")].address}'"

# Real-Time Stream Tailing
alias klogs="kubectl logs -f --tail=100"
alias klogs-net="kubectl logs -f --tail=100 -n networking"

# Interactive Pod Shell Drop-In
alias kexec="kubectl exec -it"

# Deployment Rollout Revisions Trace
alias krev-pihole="kubectl rollout history deployment/pihole-dns-server -n networking"
alias krev-tunnel="kubectl rollout history deployment/cloudflared-tunnel -n networking"

# Local Proxy Management
alias kproxy="kubectl proxy"
alias kkill="pkill -9 -f 'kubectl proxy'"

# Modern On-Demand Token Generator (Valid for 1 hour)
alias ktoken="kubectl -n kubernetes-dashboard create token admin-user"

```

## Cluster Verification Quick Reference

```bash
    kubectl get svc -n kube-system
```

### Manually Setting Up CloudFlare Tunnel Token

TODO: Automate this process

```bash
kubectl create secret generic cloudflare-tunnel-secret \
  -n networking \
  --from-literal=token="YOUR_RAW_CLOUDFLARE_TUNNEL_TOKEN_STRING"
```

# Verify Split-Horizon Ingress Route Resolution:

```bash
    curl -I -H "Host: pihole.freesalty.com" [http://192.168.50.240/admin/](http://192.168.50.240/admin/)
```

---

## Worker Nodes K3 Authorization

Open file location with:

```bash
sudo nano /etc/systemd/system/k3s-agent.service.env
```

Find the Control K3S_TOKEN, and K3S_URL here. If the IP of K3S_URL does not point back to the internal IP of the Control Node.
The workers will fail to communicate.

### How to Check Worker Nodes:

```bash
# Check the status on the worker node
sudo systemctl status k3s-agent

# If it is failed, or if it says active but metrics are broken, restart it:
sudo systemctl restart k3s-agent
```

---

### Setting up a load balancer

Since k3s is bare-metal; we will need a load balancer...
From your workstation do:

```bash
# Create the namespace and deploy the components
kubectl apply -f https://raw.githubusercontent.com/metallb/metallb/v0.14.5/config/manifests/metallb-native.yaml
```

Output should look like:

```text
namespace/metallb-system created
customresourcedefinition.apiextensions.k8s.io/bfdprofiles.metallb.io created
customresourcedefinition.apiextensions.k8s.io/bgpadvertisements.metallb.io created
customresourcedefinition.apiextensions.k8s.io/bgppeers.metallb.io created
customresourcedefinition.apiextensions.k8s.io/communities.metallb.io created
customresourcedefinition.apiextensions.k8s.io/ipaddresspools.metallb.io created
customresourcedefinition.apiextensions.k8s.io/l2advertisements.metallb.io created
customresourcedefinition.apiextensions.k8s.io/servicel2statuses.metallb.io created
serviceaccount/controller created
serviceaccount/speaker created
role.rbac.authorization.k8s.io/controller created
role.rbac.authorization.k8s.io/pod-lister created
clusterrole.rbac.authorization.k8s.io/metallb-system:controller created
clusterrole.rbac.authorization.k8s.io/metallb-system:speaker created
rolebinding.rbac.authorization.k8s.io/controller created
rolebinding.rbac.authorization.k8s.io/pod-lister created
clusterrolebinding.rbac.authorization.k8s.io/metallb-system:controller created
clusterrolebinding.rbac.authorization.k8s.io/metallb-system:speaker created
configmap/metallb-excludel2 created
secret/metallb-webhook-cert created
service/metallb-webhook-service created
deployment.apps/controller created
daemonset.apps/speaker created
validatingwebhookconfiguration.admissionregistration.k8s.io/metallb-webhook-configuration created
```

## Find the metallb (double LL) config

[config](./load-balancer/metallb-config.yaml)

From the directoy apply it to the cluster:

```bash
kubectl apply -f metallb-config.yaml
```

---
