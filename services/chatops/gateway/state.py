"""TTL state management and Slack Block Kit builders for media-sync & alert triage."""

from __future__ import annotations

import json
import os
import re
import time
import uuid
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable

from services.chatops.gateway.security import InputValidationError

DEFAULT_BATCH_TTL_SECONDS = 86400  # 24 hours


@dataclass(frozen=True)
class CandidateItem:
    """Represents a single trending movie or TV candidate discovered by media-sync."""

    index: int
    title: str
    year: int | None = None
    media_type: str = "Movie"
    imdb_id: str | None = None
    info_hash: str | None = None
    poster_url: str | None = None
    season: int | None = None

    def display_label(self) -> str:
        """Format candidate for human-readable Slack output."""
        if self.media_type.upper() == "TV" and self.season is not None:
            suffix = f"(Season {self.season}) [TV]"
        elif self.year is not None:
            suffix = f"({self.year}) [{self.media_type}]"
        else:
            suffix = f"[{self.media_type}]"
        return f"[{self.index}] {self.title} {suffix}"

    def to_dict(self) -> dict[str, Any]:
        """Serialize candidate to a dictionary."""
        return asdict(self)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> CandidateItem:
        """Deserialize candidate from a dictionary."""
        return cls(
            index=int(data["index"]),
            title=str(data["title"]),
            year=int(data["year"]) if data.get("year") is not None else None,
            media_type=str(data.get("media_type", "Movie")),
            imdb_id=data.get("imdb_id"),
            info_hash=data.get("info_hash"),
            poster_url=data.get("poster_url"),
            season=int(data["season"]) if data.get("season") is not None else None,
        )


@dataclass
class CandidateBatch:
    """Represents a numbered batch of media-sync candidates with TTL expiration."""

    batch_id: str
    created_at: float
    expires_at: float
    items: list[CandidateItem] = field(default_factory=list)
    channel_id: str | None = None
    message_ts: str | None = None

    def is_expired(self, now: float | None = None) -> bool:
        """Return True if the batch TTL has elapsed."""
        current = time.time() if now is None else now
        return current >= self.expires_at

    def to_dict(self) -> dict[str, Any]:
        """Serialize batch to a dictionary."""
        return {
            "batch_id": self.batch_id,
            "created_at": self.created_at,
            "expires_at": self.expires_at,
            "channel_id": self.channel_id,
            "message_ts": self.message_ts,
            "items": [item.to_dict() for item in self.items],
        }

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> CandidateBatch:
        """Deserialize batch from a dictionary."""
        return cls(
            batch_id=str(data["batch_id"]),
            created_at=float(data["created_at"]),
            expires_at=float(data["expires_at"]),
            channel_id=data.get("channel_id"),
            message_ts=data.get("message_ts"),
            items=[CandidateItem.from_dict(raw) for raw in data.get("items", [])],
        )


class CandidateStateStore:
    """In-memory and optional JSON-backed store for media-sync candidate batches."""

    def __init__(
        self,
        ttl_seconds: float | None = None,
        storage_path: str | Path | None = None,
    ) -> None:
        env_ttl = os.environ.get("CANDIDATE_BATCH_TTL_SECONDS")
        self.ttl_seconds = (
            float(ttl_seconds)
            if ttl_seconds is not None
            else float(env_ttl) if env_ttl else float(DEFAULT_BATCH_TTL_SECONDS)
        )
        env_path = os.environ.get("CANDIDATE_STATE_PATH")
        resolved_path = storage_path if storage_path is not None else env_path
        self.storage_path: Path | None = Path(resolved_path) if resolved_path else None
        self._batches: dict[str, CandidateBatch] = {}
        self._latest_batch_id: str | None = None
        if self.storage_path and self.storage_path.exists():
            self._load()

    def _load(self) -> None:
        if not self.storage_path or not self.storage_path.exists():
            return
        try:
            payload = json.loads(self.storage_path.read_text(encoding="utf-8"))
            self._latest_batch_id = payload.get("latest_batch_id")
            self._batches = {
                bid: CandidateBatch.from_dict(raw)
                for bid, raw in payload.get("batches", {}).items()
            }
        except (OSError, ValueError, KeyError):
            self._batches = {}
            self._latest_batch_id = None

    def _save(self) -> None:
        if not self.storage_path:
            return
        self.storage_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "latest_batch_id": self._latest_batch_id,
            "batches": {bid: batch.to_dict() for bid, batch in self._batches.items()},
        }
        self.storage_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    def prune_expired(self, now: float | None = None) -> int:
        """Remove expired batches and return the number of pruned batches."""
        current = time.time() if now is None else now
        expired_ids = [
            bid for bid, batch in self._batches.items() if batch.is_expired(current)
        ]
        for bid in expired_ids:
            del self._batches[bid]
            if self._latest_batch_id == bid:
                self._latest_batch_id = None
        if expired_ids:
            if self._latest_batch_id is None and self._batches:
                newest = max(self._batches.values(), key=lambda b: b.created_at)
                self._latest_batch_id = newest.batch_id
            self._save()
        return len(expired_ids)

    def create_batch(
        self,
        candidates: Iterable[CandidateItem | dict[str, Any]],
        *,
        batch_id: str | None = None,
        channel_id: str | None = None,
        message_ts: str | None = None,
        now: float | None = None,
    ) -> CandidateBatch:
        """Store a new numbered batch of candidates with a 24h TTL."""
        current = time.time() if now is None else now
        self.prune_expired(current)
        normalized_items: list[CandidateItem] = []
        for idx, entry in enumerate(candidates, start=1):
            if isinstance(entry, CandidateItem):
                item = entry
            else:
                payload = dict(entry)
                payload.setdefault("index", idx)
                item = CandidateItem.from_dict(payload)
            normalized_items.append(item)

        if not normalized_items:
            raise InputValidationError("Cannot create an empty candidate batch.")

        resolved_id = batch_id or f"batch-{uuid.uuid4().hex[:8]}"
        batch = CandidateBatch(
            batch_id=resolved_id,
            created_at=current,
            expires_at=current + self.ttl_seconds,
            items=normalized_items,
            channel_id=channel_id,
            message_ts=message_ts,
        )
        self._batches[resolved_id] = batch
        self._latest_batch_id = resolved_id
        self._save()
        return batch

    def get_batch(
        self,
        batch_id: str | None = None,
        *,
        now: float | None = None,
    ) -> CandidateBatch | None:
        """Retrieve a specific or most recent non-expired candidate batch."""
        current = time.time() if now is None else now
        self.prune_expired(current)
        target_id = batch_id or self._latest_batch_id
        if not target_id:
            return None
        batch = self._batches.get(target_id)
        if batch is None or batch.is_expired(current):
            return None
        return batch

    def resolve_selection(
        self,
        indices: Iterable[int],
        *,
        batch_id: str | None = None,
        now: float | None = None,
    ) -> tuple[CandidateBatch, list[CandidateItem]]:
        """Resolve 1-based candidate index selections against an active batch."""
        batch = self.get_batch(batch_id, now=now)
        if batch is None:
            raise InputValidationError(
                "No active media-sync candidate batch found (or batch expired after 24h)."
            )

        by_index = {item.index: item for item in batch.items}
        selected: list[CandidateItem] = []
        seen: set[int] = set()
        for raw_idx in indices:
            idx = int(raw_idx)
            if idx in seen:
                continue
            if idx not in by_index:
                valid_range = sorted(by_index.keys())
                raise InputValidationError(
                    f"Candidate index {idx} is out of range for batch '{batch.batch_id}'. "
                    f"Valid indices: {valid_range}"
                )
            seen.add(idx)
            selected.append(by_index[idx])

        if not selected:
            raise InputValidationError("At least one candidate index must be selected.")
        return batch, selected


def parse_download_command(text: str) -> list[int]:
    """Parse `/download 1 3 6 10` or `1, 3, 6` into deduplicated 1-based integers."""
    cleaned = (text or "").strip()
    if cleaned.startswith("/download"):
        cleaned = cleaned[len("/download") :].strip()
    if not cleaned:
        raise InputValidationError(
            "Usage: `/download <numbers>` (e.g. `/download 1 3 6 10`)."
        )

    tokens = [tok for tok in re.split(r"[\s,]+", cleaned) if tok]
    indices: list[int] = []
    seen: set[int] = set()
    for token in tokens:
        if not token.isdigit():
            raise InputValidationError(
                f"Invalid candidate number '{token}'. Provide positive integers such as `1 3 6`."
            )
        value = int(token)
        if value <= 0 or value > 100:
            raise InputValidationError(
                f"Candidate index '{value}' must be between 1 and 100."
            )
        if value not in seen:
            seen.add(value)
            indices.append(value)
    return indices


def build_candidate_blocks(batch: CandidateBatch) -> list[dict[str, Any]]:
    """Build Slack Block Kit blocks for an interactive media-sync candidate batch."""
    lines = [item.display_label() for item in batch.items]
    summary_text = (
        f"🎬 *media-sync found {len(batch.items)} trending candidates* "
        f"(`{batch.batch_id}`):\n"
        + "\n".join(lines)
        + "\n\nReply with `/download <numbers>` (e.g. `/download 1 3 6 10`) "
        "or click a button below to initiate acquisition."
    )

    blocks: list[dict[str, Any]] = [
        {
            "type": "section",
            "text": {
                "type": "mrkdwn",
                "text": summary_text,
            },
        },
        {"type": "divider"},
    ]

    button_elements: list[dict[str, Any]] = []
    for item in batch.items[:5]:
        button_elements.append(
            {
                "type": "button",
                "text": {
                    "type": "plain_text",
                    "text": f"⬇️ [{item.index}] {item.title[:18]}",
                    "emoji": True,
                },
                "action_id": f"media_sync_download_item_{item.index}",
                "value": json.dumps(
                    {"batch_id": batch.batch_id, "indices": [item.index]}
                ),
            }
        )

    if button_elements:
        blocks.append({"type": "actions", "elements": button_elements})

    if len(batch.items) > 5:
        second_row: list[dict[str, Any]] = []
        for item in batch.items[5:10]:
            second_row.append(
                {
                    "type": "button",
                    "text": {
                        "type": "plain_text",
                        "text": f"⬇️ [{item.index}] {item.title[:18]}",
                        "emoji": True,
                    },
                    "action_id": f"media_sync_download_item_{item.index}",
                    "value": json.dumps(
                        {"batch_id": batch.batch_id, "indices": [item.index]}
                    ),
                }
            )
        blocks.append({"type": "actions", "elements": second_row})

    return blocks


def build_alert_triage_blocks(
    *,
    namespace: str,
    deployment: str,
    pod: str,
    diagnosis: str,
) -> list[dict[str, Any]]:
    """Build Slack Block Kit blocks for automated Grafana alert triage and remediation."""
    metadata = json.dumps(
        {
            "namespace": namespace,
            "deployment": deployment,
            "pod": pod,
        }
    )
    return [
        {
            "type": "section",
            "text": {
                "type": "mrkdwn",
                "text": (
                    f"🤖 *Antigravity Alert Triage* (`{namespace}/{deployment}`)\n"
                    f"{diagnosis}"
                ),
            },
        },
        {
            "type": "actions",
            "elements": [
                {
                    "type": "button",
                    "text": {
                        "type": "plain_text",
                        "text": "🔄 Restart Deployment",
                        "emoji": True,
                    },
                    "style": "primary",
                    "action_id": "remediate_restart_deployment",
                    "value": metadata,
                },
                {
                    "type": "button",
                    "text": {
                        "type": "plain_text",
                        "text": "📜 View Full Logs",
                        "emoji": True,
                    },
                    "action_id": "remediate_view_logs",
                    "value": metadata,
                },
                {
                    "type": "button",
                    "text": {
                        "type": "plain_text",
                        "text": "❌ Dismiss",
                        "emoji": True,
                    },
                    "style": "danger",
                    "action_id": "remediate_dismiss",
                    "value": metadata,
                },
            ],
        },
    ]
