"""Authorization, rate limiting, input sanitization, and HMAC signing utilities."""

from __future__ import annotations

import hashlib
import hmac
import os
import re
import time
from collections import defaultdict, deque
from typing import Iterable

K8S_IDENTIFIER_RE = re.compile(r"^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$")

DEFAULT_ALLOWED_NAMESPACES: frozenset[str] = frozenset(
    {
        "default",
        "infrastructure",
        "kube-system",
        "media",
        "media-test",
        "monitoring",
        "utilities",
        "utilities-test",
    }
)

DEFAULT_ALLOWED_NODE_IPS: frozenset[str] = frozenset(
    {
        "192.168.1.180",  # mothership (Proxmox VE hypervisor)
        "192.168.1.210",  # k3s-svr-1 (Control Plane + Worker)
        "192.168.1.211",  # k3s-worker-1 (Media & Torrent Node)
        "192.168.1.50",   # micro-nas (Storage & Utility Node)
    }
)


class AuthorizationError(PermissionError):
    """Raised when an unauthorized Slack user attempts to invoke a command."""


class RateLimitExceededError(RuntimeError):
    """Raised when a user exceeds the allowed command rate."""


class InputValidationError(ValueError):
    """Raised when command or tool parameters fail sanitization checks."""


class AuthorizationGuard:
    """Enforces fail-closed Slack User ID whitelist validation."""

    def __init__(self, whitelist: Iterable[str] | str | None = None) -> None:
        raw = whitelist if whitelist is not None else os.environ.get("USER_WHITELIST", "")
        if isinstance(raw, str):
            parsed = [item.strip() for item in raw.split(",") if item.strip()]
        else:
            parsed = [str(item).strip() for item in raw if str(item).strip()]
        self._allowed_users: frozenset[str] = frozenset(parsed)

    @property
    def allowed_users(self) -> frozenset[str]:
        """Return the immutable set of whitelisted Slack user IDs."""
        return self._allowed_users

    def is_authorized(self, user_id: str | None) -> bool:
        """Return True only if the whitelist is non-empty and contains user_id."""
        if not user_id or not self._allowed_users:
            return False
        return user_id.strip() in self._allowed_users

    def require_authorized(self, user_id: str | None) -> str:
        """Validate user_id or raise AuthorizationError."""
        if not self.is_authorized(user_id):
            raise AuthorizationError(
                f"Unauthorized Slack user '{user_id or '<empty>'}'. "
                "User ID is not present in USER_WHITELIST."
            )
        return user_id.strip()  # type: ignore[return-value]


class RateLimiter:
    """Sliding-window per-user rate limiter."""

    def __init__(self, max_requests: int = 10, window_seconds: float = 60.0) -> None:
        if max_requests <= 0:
            raise ValueError("max_requests must be positive")
        if window_seconds <= 0:
            raise ValueError("window_seconds must be positive")
        self.max_requests = max_requests
        self.window_seconds = window_seconds
        self._events: dict[str, deque[float]] = defaultdict(deque)

    def check(self, user_id: str, now: float | None = None) -> None:
        """Record a request for user_id or raise RateLimitExceededError."""
        timestamp = time.monotonic() if now is None else now
        cutoff = timestamp - self.window_seconds
        bucket = self._events[user_id]
        while bucket and bucket[0] <= cutoff:
            bucket.popleft()
        if len(bucket) >= self.max_requests:
            raise RateLimitExceededError(
                f"Rate limit exceeded for user '{user_id}' "
                f"({self.max_requests} requests per {int(self.window_seconds)}s)."
            )
        bucket.append(timestamp)


def validate_k8s_identifier(value: str, field_name: str = "identifier") -> str:
    """Validate that a Kubernetes resource name matches RFC 1123."""
    cleaned = (value or "").strip()
    if not cleaned or not K8S_IDENTIFIER_RE.match(cleaned):
        raise InputValidationError(
            f"Invalid Kubernetes {field_name}: '{value}'. "
            "Must contain only lowercase alphanumeric characters, '-' or '.'."
        )
    return cleaned


def validate_namespace(
    namespace: str,
    allowed_namespaces: Iterable[str] | None = None,
) -> str:
    """Validate a Kubernetes namespace against RFC 1123 and the homelab allowlist."""
    cleaned = validate_k8s_identifier(namespace, field_name="namespace")
    allowlist = (
        frozenset(allowed_namespaces)
        if allowed_namespaces is not None
        else DEFAULT_ALLOWED_NAMESPACES
    )
    if cleaned not in allowlist:
        raise InputValidationError(
            f"Namespace '{cleaned}' is not in the allowed homelab namespace list: "
            f"{sorted(allowlist)}"
        )
    return cleaned


def validate_node_ip(
    node_ip: str,
    allowed_ips: Iterable[str] | None = None,
) -> str:
    """Validate that a target SSH node IP belongs to the homelab node allowlist."""
    cleaned = (node_ip or "").strip()
    env_ips = os.environ.get("ALLOWED_SSH_NODE_IPS", "")
    if allowed_ips is not None:
        allowlist = frozenset(allowed_ips)
    elif env_ips.strip():
        allowlist = frozenset(ip.strip() for ip in env_ips.split(",") if ip.strip())
    else:
        allowlist = DEFAULT_ALLOWED_NODE_IPS

    if cleaned not in allowlist:
        raise InputValidationError(
            f"Node IP '{cleaned}' is not in the allowed homelab SSH node list: "
            f"{sorted(allowlist)}"
        )
    return cleaned


def compute_webhook_signature(secret: str, payload: bytes) -> str:
    """Compute an HMAC-SHA256 signature header value for inter-service webhook requests."""
    if not secret:
        raise InputValidationError("WEBHOOK_SECRET_TOKEN must not be empty.")
    digest = hmac.new(secret.encode("utf-8"), payload, hashlib.sha256).hexdigest()
    return f"sha256={digest}"


def verify_webhook_signature(secret: str, payload: bytes, signature_header: str) -> bool:
    """Constant-time verification of an HMAC-SHA256 signature header."""
    if not secret or not signature_header:
        return False
    expected = compute_webhook_signature(secret, payload)
    return hmac.compare_digest(expected, signature_header.strip())
