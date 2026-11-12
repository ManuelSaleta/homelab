"""Slack Socket Mode ChatOps Gateway Daemon.

Provides:
  - Zero-Trust outbound WebSocket ingress via Slack Socket Mode (`xapp-...` / `xoxb-...`)
  - Strict `USER_WHITELIST` authorization and per-user rate limiting
  - `/download <numbers>` slash command & interactive Block Kit approval for `media-sync`
  - `/agy` and `@Antigravity` natural-language ChatOps bridge
  - Automated Grafana `#homelab-alerts` interception, diagnosis, and interactive remediation
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import re
from typing import Any, Iterable

from services.chatops.gateway.agent_bridge import AntigravityBridge
from services.chatops.gateway.security import (
    AuthorizationError,
    AuthorizationGuard,
    InputValidationError,
    RateLimiter,
    RateLimitExceededError,
)
from services.chatops.gateway.state import (
    CandidateBatch,
    CandidateItem,
    CandidateStateStore,
    build_alert_triage_blocks,
    build_candidate_blocks,
    parse_download_command,
)
from services.chatops.mcp.server import (
    k8s_get_pod_diagnostics,
    k8s_restart_deployment,
    set_state_store,
    trigger_media_sync_download,
)

logger = logging.getLogger("homelab.chatops.gateway")

ALERT_NAMESPACE_RE = re.compile(r"namespace\s*[=:]\s*([a-z0-9-]+)", re.IGNORECASE)
ALERT_DEPLOYMENT_RE = re.compile(r"deployment\s*[=:]\s*([a-z0-9.-]+)", re.IGNORECASE)
ALERT_POD_RE = re.compile(r"pod\s*[=:]\s*([a-z0-9.-]+)", re.IGNORECASE)


class ChatOpsGateway:
    """Core Slack ChatOps dispatcher decoupling business logic from transport."""

    def __init__(
        self,
        *,
        auth_guard: AuthorizationGuard | None = None,
        rate_limiter: RateLimiter | None = None,
        state_store: CandidateStateStore | None = None,
        agent_bridge: AntigravityBridge | None = None,
        alerts_channel_id: str | None = None,
    ) -> None:
        self.auth_guard = auth_guard or AuthorizationGuard()
        self.rate_limiter = rate_limiter or RateLimiter()
        self.state_store = state_store or CandidateStateStore()
        set_state_store(self.state_store)
        self.agent_bridge = agent_bridge or AntigravityBridge()
        self.alerts_channel_id = (
            alerts_channel_id
            if alerts_channel_id is not None
            else os.environ.get("SLACK_ALERTS_CHANNEL", "")
        ).strip()

    def _authorize_and_limit(self, user_id: str | None) -> str:
        verified_user = self.auth_guard.require_authorized(user_id)
        self.rate_limiter.check(verified_user)
        return verified_user

    def format_candidate_notification(
        self,
        candidates: Iterable[CandidateItem | dict[str, Any]],
        *,
        batch_id: str | None = None,
        channel_id: str | None = None,
    ) -> tuple[CandidateBatch, list[dict[str, Any]]]:
        """Store a new candidate batch in the 24h TTL store and return its Slack blocks."""
        batch = self.state_store.create_batch(
            candidates,
            batch_id=batch_id,
            channel_id=channel_id,
        )
        blocks = build_candidate_blocks(batch)
        return batch, blocks

    async def handle_download_command(
        self,
        *,
        user_id: str | None,
        text: str,
        batch_id: str | None = None,
        webhook_url: str | None = None,
        webhook_secret: str | None = None,
    ) -> dict[str, Any]:
        """Validate `/download 1 3 6` command, resolve TTL batch, and trigger media-sync."""
        caller = self._authorize_and_limit(user_id)
        indices = parse_download_command(text)
        result = await asyncio.to_thread(
            trigger_media_sync_download,
            indices,
            batch_id=batch_id,
            requested_by=f"slack:{caller}",
            webhook_url=webhook_url,
            webhook_secret=webhook_secret,
        )
        approved_titles = [
            f"[{item['index']}] {item['title']}" for item in result.get("approved", [])
        ]
        skipped_titles = [
            f"[{item['index']}] {item['title']} (already in Plex)"
            for item in result.get("skipped_existing", [])
        ]
        summary_parts: list[str] = []
        if approved_titles:
            summary_parts.append(
                f"✅ Dispatched {len(approved_titles)} item(s) to `media-sync`: "
                + ", ".join(approved_titles)
            )
        if skipped_titles:
            summary_parts.append(
                "⏭️ Skipped pre-existing library item(s): " + ", ".join(skipped_titles)
            )
        result["summary_text"] = "\n".join(summary_parts) or result.get(
            "message", "No items dispatched."
        )
        return result

    async def handle_agy_command(
        self,
        *,
        user_id: str | None,
        prompt: str,
        channel_id: str | None = None,
        thread_ts: str | None = None,
    ) -> str:
        """Authorize user and forward prompt to Antigravity CLI bridge."""
        caller = self._authorize_and_limit(user_id)
        return await self.agent_bridge.ask(
            prompt,
            context={
                "slack_user": caller,
                "channel_id": channel_id or "direct",
                "thread_ts": thread_ts or "root",
            },
        )

    async def handle_alert_remediation_action(
        self,
        *,
        user_id: str | None,
        action_id: str,
        value_json: str,
    ) -> dict[str, Any]:
        """Handle interactive Slack Block Kit remediation buttons on Grafana alert threads."""
        caller = self._authorize_and_limit(user_id)
        try:
            payload = json.loads(value_json or "{}")
        except ValueError as exc:
            raise InputValidationError(f"Malformed action payload JSON: {exc}") from exc

        namespace = str(payload.get("namespace", "media"))
        deployment = str(payload.get("deployment", ""))
        pod = str(payload.get("pod", ""))

        if action_id == "remediate_restart_deployment":
            outcome = await asyncio.to_thread(
                k8s_restart_deployment,
                namespace,
                deployment,
            )
            status_emoji = "✅" if outcome.get("restarted") else "❌"
            return {
                "action": action_id,
                "user_id": caller,
                "text": (
                    f"{status_emoji} *Deployment Restart* triggered by <@{caller}> for "
                    f"`{namespace}/{deployment}`:\n```{outcome.get('output', '')}```"
                ),
                "outcome": outcome,
            }

        if action_id == "remediate_view_logs":
            diag = await asyncio.to_thread(
                k8s_get_pod_diagnostics,
                namespace,
                pod,
                100,
            )
            logs_excerpt = (diag.get("logs") or "No logs returned.")[-2500:]
            return {
                "action": action_id,
                "user_id": caller,
                "text": (
                    f"📜 *Logs for `{namespace}/{pod}`* (requested by <@{caller}>):\n"
                    f"```{logs_excerpt}```"
                ),
                "outcome": diag,
            }

        if action_id == "remediate_dismiss":
            return {
                "action": action_id,
                "user_id": caller,
                "text": f"🔕 Alert triage dismissed by <@{caller}>.",
                "outcome": {"dismissed": True},
            }

        raise InputValidationError(f"Unsupported remediation action_id: '{action_id}'")

    @staticmethod
    def extract_alert_targets(alert_text: str) -> dict[str, str]:
        """Extract namespace, deployment, and pod names from a Grafana alert message."""
        ns_match = ALERT_NAMESPACE_RE.search(alert_text or "")
        deploy_match = ALERT_DEPLOYMENT_RE.search(alert_text or "")
        pod_match = ALERT_POD_RE.search(alert_text or "")

        namespace = ns_match.group(1) if ns_match else "media"
        pod = pod_match.group(1) if pod_match else "vpn-media-sync-0"
        deployment = (
            deploy_match.group(1)
            if deploy_match
            else re.sub(r"-[a-z0-9]{5,10}-[a-z0-9]{5}$", "", pod)
        )
        return {
            "namespace": namespace,
            "deployment": deployment,
            "pod": pod,
        }

    async def handle_grafana_alert_message(
        self,
        *,
        alert_text: str,
        channel_id: str | None = None,
    ) -> dict[str, Any]:
        """Intercept a firing Grafana alert in `#homelab-alerts` and build triage blocks."""
        targets = self.extract_alert_targets(alert_text)
        diagnosis = await self.agent_bridge.triage_alert(
            alert_text=alert_text,
            namespace=targets["namespace"],
            deployment=targets["deployment"],
            pod=targets["pod"],
        )
        blocks = build_alert_triage_blocks(
            namespace=targets["namespace"],
            deployment=targets["deployment"],
            pod=targets["pod"],
            diagnosis=diagnosis,
        )
        return {
            "channel_id": channel_id or self.alerts_channel_id,
            "targets": targets,
            "diagnosis": diagnosis,
            "blocks": blocks,
        }


def create_bolt_app(gateway: ChatOpsGateway | None = None) -> Any:
    """Create and wire a Slack Bolt `AsyncApp` instance using Socket Mode."""
    try:
        from slack_bolt.async_app import AsyncApp  # type: ignore[import-not-found]
    except ImportError as exc:  # pragma: no cover
        raise RuntimeError(
            "slack-bolt is required to start the live Socket Mode daemon. "
            "Install via `pip install slack-bolt aiohttp`."
        ) from exc

    gw = gateway or ChatOpsGateway()
    bot_token = os.environ.get("SLACK_BOT_TOKEN", "").strip()
    app = AsyncApp(token=bot_token)

    @app.command("/download")
    async def on_download_command(ack: Any, body: dict[str, Any], respond: Any) -> None:
        await ack()
        user_id = body.get("user_id")
        text = body.get("text", "")
        try:
            result = await gw.handle_download_command(user_id=user_id, text=text)
            await respond(text=result["summary_text"], response_type="in_channel")
        except (AuthorizationError, RateLimitExceededError, InputValidationError) as exc:
            logger.warning("Rejected /download from user %s: %s", user_id, exc)
            await respond(text=f"⛔ {exc}", response_type="ephemeral")
        except Exception as exc:
            logger.exception("Error processing /download command")
            await respond(text=f"⚠️ Failed to trigger media-sync: {exc}", response_type="ephemeral")

    @app.command("/agy")
    async def on_agy_command(ack: Any, body: dict[str, Any], respond: Any) -> None:
        await ack()
        user_id = body.get("user_id")
        prompt = body.get("text", "")
        channel_id = body.get("channel_id")
        try:
            reply = await gw.handle_agy_command(
                user_id=user_id,
                prompt=prompt,
                channel_id=channel_id,
            )
            await respond(text=reply, response_type="in_channel")
        except (AuthorizationError, RateLimitExceededError, InputValidationError) as exc:
            logger.warning("Rejected /agy from user %s: %s", user_id, exc)
            await respond(text=f"⛔ {exc}", response_type="ephemeral")

    @app.event("app_mention")
    async def on_app_mention(event: dict[str, Any], say: Any) -> None:
        user_id = event.get("user")
        raw_text = event.get("text", "")
        prompt = re.sub(r"^<@[A-Z0-9]+>\s*", "", raw_text).strip()
        thread_ts = event.get("thread_ts") or event.get("ts")
        channel_id = event.get("channel")
        try:
            reply = await gw.handle_agy_command(
                user_id=user_id,
                prompt=prompt,
                channel_id=channel_id,
                thread_ts=thread_ts,
            )
            await say(text=reply, thread_ts=thread_ts)
        except (AuthorizationError, RateLimitExceededError, InputValidationError) as exc:
            logger.warning("Rejected @Antigravity mention from user %s: %s", user_id, exc)
            await say(text=f"⛔ {exc}", thread_ts=thread_ts)

    @app.action(re.compile(r"^media_sync_download_item_\d+$"))
    async def on_candidate_button(ack: Any, body: dict[str, Any], respond: Any) -> None:
        await ack()
        user_id = (body.get("user") or {}).get("id")
        actions = body.get("actions") or [{}]
        raw_value = actions[0].get("value", "{}")
        try:
            parsed = json.loads(raw_value)
            indices = [int(i) for i in parsed.get("indices", [])]
            batch_id = parsed.get("batch_id")
            text_arg = " ".join(str(i) for i in indices)
            result = await gw.handle_download_command(
                user_id=user_id,
                text=text_arg,
                batch_id=batch_id,
            )
            await respond(
                text=result["summary_text"],
                replace_original=False,
                response_type="in_channel",
            )
        except (AuthorizationError, RateLimitExceededError, InputValidationError) as exc:
            await respond(
                text=f"⛔ {exc}",
                replace_original=False,
                response_type="ephemeral",
            )

    @app.action(re.compile(r"^remediate_(restart_deployment|view_logs|dismiss)$"))
    async def on_remediation_button(ack: Any, body: dict[str, Any], respond: Any) -> None:
        await ack()
        user_id = (body.get("user") or {}).get("id")
        action = (body.get("actions") or [{}])[0]
        action_id = action.get("action_id", "")
        value_json = action.get("value", "{}")
        try:
            result = await gw.handle_alert_remediation_action(
                user_id=user_id,
                action_id=action_id,
                value_json=value_json,
            )
            await respond(
                text=result["text"],
                replace_original=False,
                response_type="in_channel",
            )
        except (AuthorizationError, RateLimitExceededError, InputValidationError) as exc:
            await respond(
                text=f"⛔ {exc}",
                replace_original=False,
                response_type="ephemeral",
            )

    @app.event("message")
    async def on_channel_message(event: dict[str, Any], say: Any) -> None:
        channel_id = event.get("channel", "")
        if not gw.alerts_channel_id or channel_id != gw.alerts_channel_id:
            return
        # Only intercept firing alert bot messages, not our own replies
        if event.get("thread_ts"):
            return
        text = event.get("text", "")
        if not text:
            return
        triage = await gw.handle_grafana_alert_message(
            alert_text=text,
            channel_id=channel_id,
        )
        await say(
            text=triage["diagnosis"],
            blocks=triage["blocks"],
            thread_ts=event.get("ts"),
        )

    return app


async def run_socket_mode() -> None:
    """Start the outbound Slack Socket Mode WebSocket listener."""
    from slack_bolt.adapter.socket_mode.async_handler import (  # type: ignore[import-not-found]
        AsyncSocketModeHandler,
    )

    app_token = os.environ.get("SLACK_APP_TOKEN", "").strip()
    if not app_token.startswith("xapp-"):
        raise RuntimeError(
            "SLACK_APP_TOKEN (starting with 'xapp-') is required for Slack Socket Mode."
        )
    app = create_bolt_app()
    handler = AsyncSocketModeHandler(app, app_token)
    await handler.start_async()


def main() -> None:
    """CLI entrypoint for `chatops-gateway` service."""
    logging.basicConfig(
        level=os.environ.get("LOG_LEVEL", "INFO"),
        format="%(asctime)s %(levelname)s [%(name)s] %(message)s",
    )
    asyncio.run(run_socket_mode())


if __name__ == "__main__":
    main()
