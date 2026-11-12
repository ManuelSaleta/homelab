"""Homelab Remediator & Media-Sync MCP Server (FastMCP).

Exposes safe, structured tools to Antigravity CLI (`agy`):
  - query_plex_library(title, year)           [Issue #42]
  - list_media_sync_candidates(batch_id)      [Issue #39]
  - trigger_media_sync_download(indices, ...) [Issue #39]
  - k8s_get_pod_diagnostics(namespace, pod)
  - k8s_restart_deployment(namespace, deployment)
  - ssh_node_health_check(node_ip)
"""

from __future__ import annotations

import json
import os
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Callable

from services.chatops.gateway.security import (
    InputValidationError,
    compute_webhook_signature,
    validate_k8s_identifier,
    validate_namespace,
    validate_node_ip,
)
from services.chatops.gateway.state import CandidateStateStore

try:
    from mcp.server.fastmcp import FastMCP  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover - fallback when mcp package is not installed

    class FastMCP:  # type: ignore[no-redef]
        """Minimal FastMCP-compatible shim for stdlib-only test environments."""

        def __init__(self, name: str) -> None:
            self.name = name
            self.tools: dict[str, Callable[..., Any]] = {}

        def tool(self, name: str | None = None) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
            def decorator(func: Callable[..., Any]) -> Callable[..., Any]:
                self.tools[name or func.__name__] = func
                return func

            return decorator

        def run(self, transport: str = "stdio") -> None:
            raise RuntimeError(
                f"FastMCP runtime requires the 'mcp' package (transport={transport}). "
                "Install via `pip install 'mcp[cli]'`."
            )


mcp = FastMCP("homelab-remediator")

_DEFAULT_STATE_STORE: CandidateStateStore | None = None


def get_state_store() -> CandidateStateStore:
    """Return or initialize the shared CandidateStateStore instance."""
    global _DEFAULT_STATE_STORE
    if _DEFAULT_STATE_STORE is None:
        _DEFAULT_STATE_STORE = CandidateStateStore()
    return _DEFAULT_STATE_STORE


def set_state_store(store: CandidateStateStore) -> None:
    """Override the module-level CandidateStateStore (used in tests/gateway)."""
    global _DEFAULT_STATE_STORE
    _DEFAULT_STATE_STORE = store


def _normalize_title(title: str) -> str:
    return "".join(ch.lower() for ch in (title or "") if ch.isalnum() or ch.isspace()).strip()


@mcp.tool()
def query_plex_library(
    title: str,
    year: int | None = None,
    plex_url: str | None = None,
    plex_token: str | None = None,
    existing_inventory_path: str | None = None,
) -> dict[str, Any]:
    """Check whether a movie or TV show already exists in the Plex library (Issue #42).

    Queries the Plex REST API (`/search?query=...`) first, then falls back to the
    local read-only inventory file (`/state/existing-movies.json`) if configured.
    """
    cleaned_title = (title or "").strip()
    if not cleaned_title:
        raise InputValidationError("Movie or TV title must not be empty.")

    target_norm = _normalize_title(cleaned_title)
    resolved_url = (plex_url if plex_url is not None else os.environ.get("PLEX_URL", "")).rstrip("/")
    resolved_token = plex_token if plex_token is not None else os.environ.get("PLEX_TOKEN", "")
    resolved_inventory = (
        existing_inventory_path
        if existing_inventory_path is not None
        else os.environ.get("EXISTING_MOVIES_INDEX_PATH", "/state/existing-movies.json")
    )

    matches: list[dict[str, Any]] = []

    if resolved_url and resolved_token:
        query_qs = urllib.parse.urlencode({"query": cleaned_title})
        endpoint = f"{resolved_url}/search?{query_qs}"
        req = urllib.request.Request(
            endpoint,
            headers={
                "X-Plex-Token": resolved_token,
                "Accept": "application/json, application/xml;q=0.9",
            },
            method="GET",
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                raw_body = resp.read().decode("utf-8", errors="replace")
                content_type = resp.headers.get("Content-Type", "")
                if "json" in content_type or raw_body.lstrip().startswith("{"):
                    data = json.loads(raw_body)
                    metadata_list = (
                        data.get("MediaContainer", {}).get("Metadata", []) or []
                    )
                    for entry in metadata_list:
                        entry_title = str(entry.get("title", ""))
                        entry_year = entry.get("year")
                        if _normalize_title(entry_title) == target_norm and (
                            year is None or entry_year is None or int(entry_year) == int(year)
                        ):
                            matches.append(
                                {
                                    "title": entry_title,
                                    "year": int(entry_year) if entry_year is not None else None,
                                    "type": entry.get("type", "movie"),
                                    "rating_key": entry.get("ratingKey"),
                                    "source": "plex_api",
                                }
                            )
                else:
                    root = ET.fromstring(raw_body)
                    for elem in root.findall(".//Video") + root.findall(".//Directory"):
                        entry_title = elem.attrib.get("title", "")
                        entry_year = elem.attrib.get("year")
                        if _normalize_title(entry_title) == target_norm and (
                            year is None or entry_year is None or int(entry_year) == int(year)
                        ):
                            matches.append(
                                {
                                    "title": entry_title,
                                    "year": int(entry_year) if entry_year else None,
                                    "type": elem.attrib.get("type", "movie"),
                                    "rating_key": elem.attrib.get("ratingKey"),
                                    "source": "plex_api",
                                }
                            )
        except (urllib.error.URLError, TimeoutError, ValueError, ET.ParseError) as exc:
            # Graceful fallback per Issue #42 acceptance criteria
            return {
                "exists": False,
                "title": cleaned_title,
                "year": year,
                "matches": [],
                "warning": f"Plex API unreachable, falling back to local inventory: {exc}",
            }

    if not matches and resolved_inventory:
        inv_path = Path(resolved_inventory)
        if inv_path.exists():
            try:
                items = json.loads(inv_path.read_text(encoding="utf-8"))
                if isinstance(items, dict):
                    items = items.get("movies", [])
                for entry in items:
                    entry_title = str(entry.get("title", ""))
                    entry_year = entry.get("year")
                    if _normalize_title(entry_title) == target_norm and (
                        year is None or entry_year is None or int(entry_year) == int(year)
                    ):
                        matches.append(
                            {
                                "title": entry_title,
                                "year": int(entry_year) if entry_year is not None else None,
                                "type": entry.get("type", "movie"),
                                "source": "local_inventory",
                            }
                        )
            except (OSError, ValueError):
                pass

    return {
        "exists": len(matches) > 0,
        "title": cleaned_title,
        "year": year,
        "matches": matches,
    }


@mcp.tool()
def list_media_sync_candidates(batch_id: str | None = None) -> dict[str, Any]:
    """Return the active 24h TTL media-sync candidate batch."""
    batch = get_state_store().get_batch(batch_id)
    if batch is None:
        return {
            "active": False,
            "batch_id": None,
            "candidates": [],
        }
    return {
        "active": True,
        "batch_id": batch.batch_id,
        "expires_at": batch.expires_at,
        "candidates": [item.to_dict() for item in batch.items],
    }


@mcp.tool()
def trigger_media_sync_download(
    indices: list[int],
    batch_id: str | None = None,
    requested_by: str = "mcp",
    webhook_url: str | None = None,
    webhook_secret: str | None = None,
    skip_existing_in_plex: bool = True,
) -> dict[str, Any]:
    """Dispatch approved candidate indices to the media-sync orchestrator webhook (Issue #39).

    Resolves candidate numbers against the 24h TTL state store, checks Plex deduplication
    if enabled, and posts an HMAC-SHA256 authenticated webhook request.
    """
    store = get_state_store()
    batch, selected_items = store.resolve_selection(indices, batch_id=batch_id)

    approved_items: list[dict[str, Any]] = []
    skipped_existing: list[dict[str, Any]] = []

    for item in selected_items:
        if skip_existing_in_plex:
            dedup = query_plex_library(item.title, item.year)
            if dedup.get("exists"):
                skipped_existing.append(item.to_dict())
                continue
        approved_items.append(item.to_dict())

    resolved_url = (
        webhook_url
        if webhook_url is not None
        else os.environ.get(
            "MEDIA_SYNC_WEBHOOK_URL",
            "http://vpn-media-sync.media.svc.cluster.local:8080/api/v1/sync/trigger",
        )
    )
    resolved_secret = (
        webhook_secret
        if webhook_secret is not None
        else os.environ.get("WEBHOOK_SECRET_TOKEN", "")
    )

    if not resolved_secret:
        raise InputValidationError(
            "WEBHOOK_SECRET_TOKEN is required for inter-service media-sync authentication."
        )

    if not approved_items:
        return {
            "dispatched": False,
            "batch_id": batch.batch_id,
            "approved": [],
            "skipped_existing": skipped_existing,
            "message": "All selected items already exist in Plex library; nothing dispatched.",
        }

    payload_dict = {
        "batch_id": batch.batch_id,
        "requested_by": requested_by,
        "items": approved_items,
    }
    payload_bytes = json.dumps(payload_dict, separators=(",", ":"), sort_keys=True).encode("utf-8")
    signature = compute_webhook_signature(resolved_secret, payload_bytes)

    req = urllib.request.Request(
        resolved_url,
        data=payload_bytes,
        headers={
            "Content-Type": "application/json",
            "X-Webhook-Secret": resolved_secret,
            "X-Signature-SHA256": signature,
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            response_body = resp.read().decode("utf-8", errors="replace")
            status_code = resp.status
    except urllib.error.HTTPError as exc:
        raise RuntimeError(
            f"media-sync webhook returned HTTP {exc.code}: {exc.reason}"
        ) from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(
            f"Failed to reach media-sync webhook at {resolved_url}: {exc.reason}"
        ) from exc

    return {
        "dispatched": True,
        "status_code": status_code,
        "batch_id": batch.batch_id,
        "approved": approved_items,
        "skipped_existing": skipped_existing,
        "webhook_response": response_body,
    }


@mcp.tool()
def k8s_get_pod_diagnostics(
    namespace: str,
    pod: str,
    tail_lines: int = 80,
) -> dict[str, Any]:
    """Query non-destructive pod description and recent logs from K3s."""
    clean_ns = validate_namespace(namespace)
    clean_pod = validate_k8s_identifier(pod, field_name="pod")
    safe_tail = max(1, min(int(tail_lines), 500))

    describe_proc = subprocess.run(
        ["kubectl", "describe", "pod", clean_pod, "-n", clean_ns],
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )
    logs_proc = subprocess.run(
        ["kubectl", "logs", clean_pod, "-n", clean_ns, f"--tail={safe_tail}"],
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )

    return {
        "namespace": clean_ns,
        "pod": clean_pod,
        "describe_ok": describe_proc.returncode == 0,
        "describe": (describe_proc.stdout or describe_proc.stderr).strip(),
        "logs_ok": logs_proc.returncode == 0,
        "logs": (logs_proc.stdout or logs_proc.stderr).strip(),
    }


@mcp.tool()
def k8s_restart_deployment(namespace: str, deployment: str) -> dict[str, Any]:
    """Trigger a safe rolling restart of a Kubernetes deployment on K3s."""
    clean_ns = validate_namespace(namespace)
    clean_deploy = validate_k8s_identifier(deployment, field_name="deployment")

    proc = subprocess.run(
        [
            "kubectl",
            "rollout",
            "restart",
            f"deployment/{clean_deploy}",
            "-n",
            clean_ns,
        ],
        capture_output=True,
        text=True,
        timeout=20,
        check=False,
    )

    return {
        "namespace": clean_ns,
        "deployment": clean_deploy,
        "restarted": proc.returncode == 0,
        "output": (proc.stdout or proc.stderr).strip(),
    }


@mcp.tool()
def ssh_node_health_check(node_ip: str, ssh_user: str | None = None) -> dict[str, Any]:
    """Execute a non-destructive SSH resource health check on an allowed homelab node."""
    clean_ip = validate_node_ip(node_ip)
    user = (ssh_user or os.environ.get("HOMELAB_SSH_USER", "ubuntu")).strip()
    if not user.isalnum() and "_" not in user and "-" not in user:
        raise InputValidationError(f"Invalid SSH username '{user}'.")

    proc = subprocess.run(
        [
            "ssh",
            "-o",
            "BatchMode=yes",
            "-o",
            "ConnectTimeout=5",
            f"{user}@{clean_ip}",
            "uptime && free -m && df -h /",
        ],
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )

    return {
        "node_ip": clean_ip,
        "healthy": proc.returncode == 0,
        "output": (proc.stdout or proc.stderr).strip(),
    }


def main() -> None:
    """Entrypoint for running the Homelab FastMCP server over stdio."""
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
