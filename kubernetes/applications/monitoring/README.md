# Monitoring Stack

A collection of Helm configurations for deploying a full observability suite (Prometheus, Grafana, Loki, Alloy).

## Prerequisites

- Helm installed
- Access to the `monitoring` namespace

## Deployment

Run the following command to deploy the entire stack in the correct order:

```bash
make all
```

### Danger: This removes all resources in the monitoring namespace

```bash
kubectl delete all --all -n monitoring
```

### Why this structure works

By using `helm upgrade --install`, you solve the "no release found" vs. "release already exists" conflict because Helm handles both states gracefully.
