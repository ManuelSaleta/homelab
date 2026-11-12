#!/usr/bin/env bash
# ==============================================================================
# Mothership Homelab: Staged Deployment, Validation & Promotion Pipeline (#36)
# ==============================================================================
# Implements ADR 001 (Issue #35) and Staged CI/CD Promotion (Issue #36):
#   - render-test [app|file] : Transform production manifest(s) into isolated
#                              test-* namespace + *.test.<domain> manifests
#   - validate-static [app]  : Validate YAML syntax, domain substitution, and
#                              isolation invariants for production & test specs
#   - sync-secrets           : Mirror Opaque secrets into test-networking/media
#   - test-up [app]          : Deploy application(s) to isolated test environment
#   - test-verify [app]      : Gate check: rollout readiness + HTTPS Ingress probe
#   - test-down [app]        : Tear down staged test deployment(s)
#   - verify-prod [app]      : Verify production rollout & HTTPS Ingress health
#   - rollback-prod [app]    : Roll back production deployment(s) via rollout undo
#   - promote [app]          : Full pipeline: test-up -> test-verify -> test-down
#                              -> prod deploy -> verify-prod (auto-rollback on fail)
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INFRA_DIR="${ROOT_DIR}/kubernetes/infrastructure"
APPS_DIR="${ROOT_DIR}/kubernetes/applications"
TERRAFORM_DIR="${ROOT_DIR}/terraform/vm_provisioning"

# Resolve DOMAIN_NAME and TEST_DOMAIN_NAME
if [[ -z "${DOMAIN_NAME:-}" ]]; then
  DOMAIN_NAME="$(grep -E '^\s*(domain_name|homelab_domain)\s*=' "${TERRAFORM_DIR}/terraform.tfvars" 2>/dev/null | head -n 1 | cut -d'=' -f2 | tr -d ' "' || true)"
  DOMAIN_NAME="${DOMAIN_NAME:-example.com}"
fi
export DOMAIN_NAME
export HOMELAB_DOMAIN="${HOMELAB_DOMAIN:-${DOMAIN_NAME}}"

if [[ -z "${TEST_DOMAIN_NAME:-}" ]]; then
  TEST_DOMAIN_NAME="$(grep -E '^\s*test_domain_name\s*=' "${TERRAFORM_DIR}/terraform.tfvars" 2>/dev/null | head -n 1 | cut -d'=' -f2 | tr -d ' "' || true)"
  TEST_DOMAIN_NAME="${TEST_DOMAIN_NAME:-test.${DOMAIN_NAME}}"
fi
export TEST_DOMAIN_NAME

TRAEFIK_VIP="${TRAEFIK_VIP:-192.168.1.240}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-180s}"

STAGED_APPS=(
  "pihole"
  "homepage"
  "vaultwarden"
  "karakeep"
  "uptime-kuma"
  "navidrome"
  "plex"
  "jellyfin"
  "media-sync"
)

# Resolve manifest files for a given app name
get_app_manifests() {
  local app="$1"
  case "${app}" in
    pihole)      echo "${APPS_DIR}/pihole/pihole-deployment.yaml" ;;
    homepage)    echo "${APPS_DIR}/homepage/homepage-deployment.yaml" ;;
    vaultwarden) echo "${APPS_DIR}/vaultwarden/vaultwarden-deployment.yaml" ;;
    karakeep)    echo "${APPS_DIR}/karakeep/karakeep-deployment.yaml" "${APPS_DIR}/karakeep/karakeep-meili.yaml" "${APPS_DIR}/karakeep/karakeep-browser.yaml" ;;
    uptime-kuma) echo "${APPS_DIR}/uptime-kuma/uptime-kuma-deployment.yaml" ;;
    navidrome)   echo "${APPS_DIR}/navidrome/navidrome-deployment.yaml" ;;
    plex)        echo "${APPS_DIR}/plex/plex-deployment.yaml" ;;
    jellyfin)    echo "${APPS_DIR}/jellyfin/jellyfin-deployment.yaml" ;;
    media-sync)  echo "${APPS_DIR}/media-sync/media-sync-deployment.yaml" ;;
    all|"")
      local files=()
      for a in "${STAGED_APPS[@]}"; do
        # shellcheck disable=SC2207
        files+=($(get_app_manifests "${a}"))
      done
      echo "${files[@]}"
      ;;
    *)
      if [[ -f "${app}" ]]; then
        echo "${app}"
      else
        echo "ERROR: Unknown application or manifest path '${app}'." >&2
        echo "Supported apps: ${STAGED_APPS[*]} all" >&2
        exit 1
      fi
      ;;
  esac
}

# Transform one or more manifests into isolated test-namespace + test-domain YAML
render_test_manifests() {
  local target="${1:-all}"
  # shellcheck disable=SC2207
  local files=($(get_app_manifests "${target}"))

  for file in "${files[@]}"; do
    DOMAIN_NAME="${TEST_DOMAIN_NAME}" \
    HOMELAB_DOMAIN="${TEST_DOMAIN_NAME}" \
    TEST_DOMAIN_NAME="${TEST_DOMAIN_NAME}" \
    envsubst '${DOMAIN_NAME} ${HOMELAB_DOMAIN} ${TEST_DOMAIN_NAME}' < "${file}"
    echo -e "\n---"
  done | python3 -c '
import sys, yaml

docs = list(yaml.safe_load_all(sys.stdin))
out_docs = []

NS_MAP = {
    "networking": "test-networking",
    "media": "test-media",
}

for doc in docs:
    if not doc or not isinstance(doc, dict):
        continue
    kind = doc.get("kind", "")
    # Drop cluster-scoped PVs, PVCs, Namespaces, and ClusterRoles in test overlays
    if kind in ("Namespace", "PersistentVolume", "PersistentVolumeClaim", "ClusterRole", "ClusterRoleBinding"):
        continue

    meta = doc.setdefault("metadata", {})
    ns = meta.get("namespace", "networking")
    meta["namespace"] = NS_MAP.get(ns, ns if ns.startswith("test-") else f"test-{ns}")

    labels = meta.setdefault("labels", {})
    labels["environment"] = "test"

    if kind == "Service":
        spec = doc.get("spec", {})
        if spec.get("type") == "LoadBalancer":
            spec["type"] = "ClusterIP"
        anns = meta.get("annotations", {})
        anns.pop("metallb.universe.tf/loadBalancerIPs", None)

    elif kind == "Ingress":
        anns = meta.setdefault("annotations", {})
        anns["gethomepage.dev/enabled"] = "false"
        for k, v in list(anns.items()):
            if isinstance(v, str):
                anns[k] = (
                    v.replace(".networking.svc.cluster.local", ".test-networking.svc.cluster.local")
                     .replace(".media.svc.cluster.local", ".test-media.svc.cluster.local")
                )

    elif kind in ("Deployment", "StatefulSet", "DaemonSet"):
        pod_spec = doc.get("spec", {}).get("template", {}).get("spec", {})
        # Replace persistentVolumeClaim and non-device hostPath mounts with isolated emptyDir
        volumes = pod_spec.get("volumes", [])
        for vol in volumes:
            if "persistentVolumeClaim" in vol:
                del vol["persistentVolumeClaim"]
                vol["emptyDir"] = {"sizeLimit": "1Gi"}
            elif "hostPath" in vol:
                hp = vol["hostPath"]
                if hp.get("type") != "CharDevice":
                    del vol["hostPath"]
                    vol["emptyDir"] = {"sizeLimit": "1Gi"}

        # Ensure container resource requests/limits fit within test ResourceQuota
        for container in pod_spec.get("containers", []):
            res = container.setdefault("resources", {})
            reqs = res.setdefault("requests", {})
            lims = res.setdefault("limits", {})
            reqs["cpu"] = "25m"
            reqs["memory"] = "64Mi"
            lims["cpu"] = "250m"
            lims["memory"] = "256Mi"

    out_docs.append(doc)

yaml.safe_dump_all(out_docs, sys.stdout, sort_keys=False)
'
}

# Validate static rendering for both production and test environments
validate_static() {
  local target="${1:-all}"
  echo "🔍 [Gate 1/3] Running static manifest & isolation validation (target: ${target})..."
  echo "   Production Domain : ${DOMAIN_NAME}"
  echo "   Test Sub-Zone     : ${TEST_DOMAIN_NAME}"

  # 1. Validate infrastructure manifests (including traefik-dns-config.yaml and test-namespaces.yaml)
  for infra_file in "${INFRA_DIR}/metallb-config.yaml" "${INFRA_DIR}/traefik-dns-config.yaml" "${INFRA_DIR}/cloudflared-config.yaml" "${INFRA_DIR}/test-namespaces.yaml"; do
    DOMAIN_NAME="${DOMAIN_NAME}" HOMELAB_DOMAIN="${DOMAIN_NAME}" TEST_DOMAIN_NAME="${TEST_DOMAIN_NAME}" \
      envsubst '${DOMAIN_NAME} ${HOMELAB_DOMAIN} ${TEST_DOMAIN_NAME}' < "${infra_file}" | \
      python3 -c 'import sys, yaml; list(yaml.safe_load_all(sys.stdin))'
  done

  # 2. Validate production rendering
  # shellcheck disable=SC2207
  local files=($(get_app_manifests "${target}"))
  for file in "${files[@]}"; do
    DOMAIN_NAME="${DOMAIN_NAME}" HOMELAB_DOMAIN="${DOMAIN_NAME}" TEST_DOMAIN_NAME="${TEST_DOMAIN_NAME}" \
      envsubst '${DOMAIN_NAME} ${HOMELAB_DOMAIN} ${TEST_DOMAIN_NAME}' < "${file}" | \
      python3 -c '
import sys, yaml
raw = sys.stdin.read()
if "${DOMAIN_NAME}" in raw or "${HOMELAB_DOMAIN}" in raw:
    raise SystemExit("Unresolved domain variable found in rendered production YAML")
list(yaml.safe_load_all(raw))
'
  done

  # 3. Validate test rendering & strict isolation invariants
  render_test_manifests "${target}" | python3 -c '
import sys, yaml

raw = sys.stdin.read()
docs = [d for d in yaml.safe_load_all(raw) if d]
if not docs:
    raise SystemExit("ERROR: Test manifest rendering produced 0 documents.")

test_domain = sys.argv[1]
prod_domain = sys.argv[2]

for doc in docs:
    kind = doc.get("kind")
    meta = doc.get("metadata", {})
    name = meta.get("name", "<unnamed>")
    ns = meta.get("namespace", "")

    if kind in ("PersistentVolume", "PersistentVolumeClaim"):
        raise SystemExit(f"ISOLATION VIOLATION: {kind}/{name} present in test manifest")

    if not ns.startswith("test-"):
        raise SystemExit(f"ISOLATION VIOLATION: {kind}/{name} targets non-test namespace \"{ns}\"")

    if kind == "Service" and doc.get("spec", {}).get("type") == "LoadBalancer":
        raise SystemExit(f"ISOLATION VIOLATION: Service/{name} uses LoadBalancer in test namespace")

    if kind == "Ingress":
        for rule in doc.get("spec", {}).get("rules", []):
            host = rule.get("host", "")
            if not host.endswith(test_domain):
                raise SystemExit(
                    f"ISOLATION VIOLATION: Ingress/{name} host \"{host}\" does not end with test sub-zone \"{test_domain}\""
                )

    if kind in ("Deployment", "StatefulSet", "DaemonSet"):
        vols = doc.get("spec", {}).get("template", {}).get("spec", {}).get("volumes", [])
        for v in vols:
            if "persistentVolumeClaim" in v:
                raise SystemExit(f"ISOLATION VIOLATION: {kind}/{name} still mounts a PersistentVolumeClaim in test mode")

print(f"✅ Validated {len(docs)} isolated test Kubernetes resources for *.{test_domain} (prod: {prod_domain}).")
' "${TEST_DOMAIN_NAME}" "${DOMAIN_NAME}"
}

# Copy Opaque secrets & ConfigMaps needed by workloads into test namespaces
sync_test_secrets() {
  echo "🔐 Mirroring secrets & configmaps into test-networking and test-media..."
  kubectl apply -f "${INFRA_DIR}/test-namespaces.yaml" >/dev/null

  for pair in "networking:test-networking" "media:test-media"; do
    local src_ns="${pair%%:*}"
    local dst_ns="${pair##*:}"

    if kubectl get namespace "${src_ns}" >/dev/null 2>&1; then
      kubectl get secrets -n "${src_ns}" --field-selector type=Opaque -o json | \
        jq --arg ns "${dst_ns}" '
          .items[] |
          del(.metadata.uid, .metadata.resourceVersion, .metadata.creationTimestamp, .metadata.annotations, .metadata.managedFields) |
          .metadata.namespace = $ns
        ' | kubectl apply -f - >/dev/null 2>&1 || true

      kubectl get configmaps -n "${src_ns}" -o json | \
        jq --arg ns "${dst_ns}" '
          .items[] |
          select(.metadata.name != "kube-root-ca.crt") |
          del(.metadata.uid, .metadata.resourceVersion, .metadata.creationTimestamp, .metadata.annotations, .metadata.managedFields) |
          .metadata.namespace = $ns
        ' | kubectl apply -f - >/dev/null 2>&1 || true
    fi
  done
}

# Deploy staged workloads to test-* namespaces
test_up() {
  local target="${1:-all}"
  validate_static "${target}"
  echo "🚀 Deploying '${target}' to isolated test environment (*.${TEST_DOMAIN_NAME})..."
  sync_test_secrets
  render_test_manifests "${target}" | kubectl apply -f -
  echo "✅ Staged deployment applied to test namespaces."
}

# Verify rollout & HTTPS ingress health in given namespaces
verify_environment() {
  local mode="$1" # "test" or "prod"
  local target="${2:-all}"

  local namespaces=()
  if [[ "${mode}" == "test" ]]; then
    namespaces=("test-networking" "test-media")
  else
    namespaces=("networking" "media")
  fi

  echo "🩺 [Gate 2/3] Verifying pod rollout readiness (${mode}, timeout: ${ROLLOUT_TIMEOUT})..."
  local checked_deployments=0

  if [[ "${target}" != "all" ]]; then
    # Filter to deployments defined in the target manifest(s)
    local dep_names
    dep_names="$(render_test_manifests "${target}" | python3 -c '
import sys, yaml
for d in yaml.safe_load_all(sys.stdin):
    if d and d.get("kind") == "Deployment":
        print(d["metadata"]["name"])
')"
    for dep in ${dep_names}; do
      for ns in "${namespaces[@]}"; do
        if kubectl get deployment "${dep}" -n "${ns}" >/dev/null 2>&1; then
          echo "   -> Waiting for deployment/${dep} in namespace '${ns}'..."
          kubectl rollout status "deployment/${dep}" -n "${ns}" --timeout="${ROLLOUT_TIMEOUT}"
          checked_deployments=$((checked_deployments + 1))
        fi
      done
    done
  else
    for ns in "${namespaces[@]}"; do
      if kubectl get namespace "${ns}" >/dev/null 2>&1; then
        local deps
        deps="$(kubectl get deployments -n "${ns}" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null || true)"
        for dep in ${deps}; do
          echo "   -> Waiting for deployment/${dep} in namespace '${ns}'..."
          kubectl rollout status "deployment/${dep}" -n "${ns}" --timeout="${ROLLOUT_TIMEOUT}"
          checked_deployments=$((checked_deployments + 1))
        done
      fi
    done
  fi

  echo "🌐 [Gate 3/3] Probing Ingress HTTPS endpoints (${mode})..."
  local hosts=()
  if [[ "${target}" != "all" ]]; then
    local rendered_hosts
    if [[ "${mode}" == "test" ]]; then
      rendered_hosts="$(render_test_manifests "${target}" | python3 -c '
import sys, yaml
for d in yaml.safe_load_all(sys.stdin):
    if d and d.get("kind") == "Ingress":
        for r in d.get("spec", {}).get("rules", []):
            if r.get("host"): print(r["host"])
')"
    else
      # shellcheck disable=SC2207
      local files=($(get_app_manifests "${target}"))
      rendered_hosts="$(for f in "${files[@]}"; do DOMAIN_NAME="${DOMAIN_NAME}" HOMELAB_DOMAIN="${DOMAIN_NAME}" envsubst '${DOMAIN_NAME} ${HOMELAB_DOMAIN}' < "${f}"; echo -e "\n---"; done | python3 -c '
import sys, yaml
for d in yaml.safe_load_all(sys.stdin):
    if d and d.get("kind") == "Ingress":
        for r in d.get("spec", {}).get("rules", []):
            if r.get("host"): print(r["host"])
')"
    fi
    # shellcheck disable=SC2206
    hosts=(${rendered_hosts})
  else
    for ns in "${namespaces[@]}"; do
      if kubectl get namespace "${ns}" >/dev/null 2>&1; then
        local ns_hosts
        ns_hosts="$(kubectl get ingress -n "${ns}" -o jsonpath='{.items[*].spec.rules[*].host}' 2>/dev/null || true)"
        # shellcheck disable=SC2206
        hosts+=(${ns_hosts})
      fi
    done
  fi

  for host in "${hosts[@]}"; do
    [[ -z "${host}" ]] && continue
    local status_code
    status_code="$(curl -k -s -o /dev/null -w "%{http_code}" --max-time 10 --resolve "${host}:443:${TRAEFIK_VIP}" "https://${host}/" || echo "000")"
    echo "   -> https://${host}/ (via ${TRAEFIK_VIP}) : HTTP ${status_code}"
    if [[ "${status_code}" == "000" || "${status_code}" -ge 500 ]]; then
      echo "❌ Ingress health probe failed for https://${host}/ (HTTP ${status_code})." >&2
      return 1
    fi
  done

  echo "✅ All ${mode} rollout & Ingress health checks passed (${checked_deployments} deployments verified)."
}

# Tear down test environment workloads
test_down() {
  local target="${1:-all}"
  echo "🧹 Cleaning up staged test resources (target: ${target})..."
  if [[ "${target}" == "all" ]]; then
    render_test_manifests "all" | kubectl delete --ignore-not-found -f - || true
  else
    render_test_manifests "${target}" | kubectl delete --ignore-not-found -f - || true
  fi
  echo "✅ Staged test resources removed."
}

# Roll back production deployments on post-promotion failure
rollback_prod() {
  local target="${1:-all}"
  echo "⏪ Rolling back production deployment(s) for '${target}'..."
  local dep_info
  dep_info="$(render_test_manifests "${target}" | python3 -c '
import sys, yaml
REV_NS = {"test-networking": "networking", "test-media": "media"}
for d in yaml.safe_load_all(sys.stdin):
    if d and d.get("kind") == "Deployment":
        ns = REV_NS.get(d["metadata"]["namespace"], d["metadata"]["namespace"])
        print(f"{ns}:{d[\"metadata\"][\"name\"]}")
')"
  for item in ${dep_info}; do
    local ns="${item%%:*}"
    local dep="${item##*:}"
    if kubectl get deployment "${dep}" -n "${ns}" >/dev/null 2>&1; then
      echo "   -> Executing rollout undo on deployment/${dep} (-n ${ns})..."
      kubectl rollout undo "deployment/${dep}" -n "${ns}"
      kubectl rollout status "deployment/${dep}" -n "${ns}" --timeout="${ROLLOUT_TIMEOUT}" || true
    fi
  done
  echo "✅ Production rollback completed."
}

# Full Staged Promotion Pipeline: Test Deploy -> Test Gate -> Test Cleanup -> Prod Deploy -> Prod Verify (Auto-Rollback)
promote_to_prod() {
  local target="${1:-all}"
  echo "========================================================================"
  echo "🚦 STAGED DEPLOYMENT & PROMOTION PIPELINE (Target: ${target})"
  echo "   Stage 1 : Test Environment (*.${TEST_DOMAIN_NAME})"
  echo "   Stage 2 : Production Environment (*.${DOMAIN_NAME})"
  echo "========================================================================"

  # Stage 1: Deploy and validate in test environment
  test_up "${target}"
  if ! verify_environment "test" "${target}"; then
    echo "❌ Stage 1 (Test Verification Gate) FAILED. Aborting production promotion!" >&2
    test_down "${target}" || true
    exit 1
  fi

  # Free test resources before promoting to production (16 GB RAM budget)
  test_down "${target}"

  # Stage 2: Promote to Production
  echo "🚀 Stage 1 PASSED. Promoting '${target}' to Production (*.${DOMAIN_NAME})..."
  if [[ "${target}" == "all" ]]; then
    make -C "${ROOT_DIR}" apps-up DOMAIN_NAME="${DOMAIN_NAME}"
  else
    make -C "${ROOT_DIR}" "${target}-up" DOMAIN_NAME="${DOMAIN_NAME}"
  fi

  # Stage 3: Post-promotion verification & automatic rollback on failure
  if ! verify_environment "prod" "${target}"; then
    echo "🚨 Production post-deployment verification FAILED! Triggering automatic rollback..." >&2
    rollback_prod "${target}"
    exit 1
  fi

  echo "🎉 Promotion of '${target}' to Production (*.${DOMAIN_NAME}) succeeded!"
}

COMMAND="${1:-help}"
TARGET_ARG="${2:-all}"

case "${COMMAND}" in
  render-test)     render_test_manifests "${TARGET_ARG}" ;;
  validate-static) validate_static "${TARGET_ARG}" ;;
  sync-secrets)    sync_test_secrets ;;
  test-up)         test_up "${TARGET_ARG}" ;;
  test-verify)      validate_static "${TARGET_ARG}" && verify_environment "test" "${TARGET_ARG}" ;;
  test-down)       test_down "${TARGET_ARG}" ;;
  verify-prod)     verify_environment "prod" "${TARGET_ARG}" ;;
  rollback-prod)   rollback_prod "${TARGET_ARG}" ;;
  promote)         promote_to_prod "${TARGET_ARG}" ;;
  *)
    echo "Usage: $0 {render-test|validate-static|sync-secrets|test-up|test-verify|test-down|verify-prod|rollback-prod|promote} [app|all]"
    exit 1
    ;;
esac
