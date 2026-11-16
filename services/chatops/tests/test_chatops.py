"""Unit tests for Homelab ChatOps security, TTL state store, MCP tools, and gateway."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from services.chatops.gateway.agent_bridge import AntigravityBridge
from services.chatops.gateway.app import ChatOpsGateway
from services.chatops.gateway.security import (
    AuthorizationError,
    AuthorizationGuard,
    InputValidationError,
    RateLimiter,
    RateLimitExceededError,
    compute_webhook_signature,
    validate_k8s_identifier,
    validate_namespace,
    validate_node_ip,
    verify_webhook_signature,
)
from services.chatops.gateway.state import (
    CandidateItem,
    CandidateStateStore,
    build_alert_triage_blocks,
    build_candidate_blocks,
    parse_download_command,
)
from services.chatops.mcp.server import (
    k8s_get_pod_diagnostics,
    k8s_restart_deployment,
    list_media_sync_candidates,
    query_plex_library,
    set_state_store,
    ssh_node_health_check,
    trigger_media_sync_download,
)


class SecurityTests(unittest.TestCase):
    """Verify fail-closed USER_WHITELIST, rate limiting, and input sanitization."""

    def test_whitelist_fail_closed_when_empty(self) -> None:
        guard = AuthorizationGuard("")
        self.assertFalse(guard.is_authorized("U12345"))
        with self.assertRaises(AuthorizationError):
            guard.require_authorized("U12345")

    def test_whitelist_allows_configured_users(self) -> None:
        guard = AuthorizationGuard("U111, U222 ")
        self.assertTrue(guard.is_authorized("U111"))
        self.assertTrue(guard.is_authorized("U222"))
        self.assertFalse(guard.is_authorized("U999"))
        self.assertEqual(guard.require_authorized("U111"), "U111")

    def test_rate_limiter_enforces_sliding_window(self) -> None:
        limiter = RateLimiter(max_requests=2, window_seconds=10.0)
        limiter.check("U111", now=100.0)
        limiter.check("U111", now=105.0)
        with self.assertRaises(RateLimitExceededError):
            limiter.check("U111", now=109.0)
        # Window slides past first event at 111.0
        limiter.check("U111", now=111.0)

    def test_k8s_and_node_validators(self) -> None:
        self.assertEqual(validate_namespace("media"), "media")
        self.assertEqual(validate_k8s_identifier("vpn-media-sync"), "vpn-media-sync")
        self.assertEqual(validate_node_ip("192.168.1.211"), "192.168.1.211")

        with self.assertRaises(InputValidationError):
            validate_namespace("evil-namespace")
        with self.assertRaises(InputValidationError):
            validate_k8s_identifier("pod; rm -rf /")
        with self.assertRaises(InputValidationError):
            validate_node_ip("10.0.0.99")

    def test_webhook_hmac_signature(self) -> None:
        secret = "test-shared-secret"
        payload = b'{"batch_id":"batch-1","items":[]}'
        sig = compute_webhook_signature(secret, payload)
        self.assertTrue(sig.startswith("sha256="))
        self.assertTrue(verify_webhook_signature(secret, payload, sig))
        self.assertFalse(verify_webhook_signature("wrong-secret", payload, sig))


class StateAndCommandTests(unittest.TestCase):
    """Verify 24-hour TTL candidate batch state store and `/download` parser."""

    def test_parse_download_command(self) -> None:
        self.assertEqual(parse_download_command("/download 1 3 6 10"), [1, 3, 6, 10])
        self.assertEqual(parse_download_command("1, 3, 3, 6"), [1, 3, 6])
        with self.assertRaises(InputValidationError):
            parse_download_command("/download")
        with self.assertRaises(InputValidationError):
            parse_download_command("/download 1 abc")

    def test_candidate_store_ttl_and_persistence(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            state_file = Path(tmpdir) / "state.json"
            store = CandidateStateStore(ttl_seconds=86400, storage_path=state_file)
            batch = store.create_batch(
                [
                    CandidateItem(index=1, title="Severance", season=2, media_type="TV"),
                    CandidateItem(index=2, title="Evil Dead Burn", year=2026, media_type="Movie"),
                    CandidateItem(index=3, title="Gladiator II", year=2024, media_type="Movie"),
                ],
                batch_id="batch-test",
                now=1000.0,
            )
            self.assertEqual(batch.batch_id, "batch-test")
            self.assertTrue(state_file.exists())

            # Reload from disk and resolve indices 1 and 3
            reloaded = CandidateStateStore(ttl_seconds=86400, storage_path=state_file)
            _, selected = reloaded.resolve_selection([1, 3], now=2000.0)
            self.assertEqual([item.title for item in selected], ["Severance", "Gladiator II"])

            # Verify expiration after 24 hours (1000 + 86401)
            self.assertIsNone(reloaded.get_batch("batch-test", now=87401.0))
            with self.assertRaises(InputValidationError):
                reloaded.resolve_selection([1], batch_id="batch-test", now=87401.0)

    def test_block_kit_builders(self) -> None:
        store = CandidateStateStore(ttl_seconds=86400)
        batch = store.create_batch(
            [
                CandidateItem(index=1, title="Severance", season=2, media_type="TV"),
                CandidateItem(index=2, title="Evil Dead Burn", year=2026, media_type="Movie"),
            ],
            batch_id="batch-ui",
        )
        blocks = build_candidate_blocks(batch)
        self.assertEqual(blocks[0]["type"], "section")
        self.assertIn("[1] Severance (Season 2) [TV]", blocks[0]["text"]["text"])

        alert_blocks = build_alert_triage_blocks(
            namespace="media",
            deployment="vpn-media-sync",
            pod="vpn-media-sync-abc",
            diagnosis="WireGuard peer handshake timeout.",
        )
        action_ids = [el["action_id"] for el in alert_blocks[1]["elements"]]
        self.assertEqual(
            action_ids,
            [
                "remediate_restart_deployment",
                "remediate_view_logs",
                "remediate_dismiss",
            ],
        )


class McpAndGatewayTests(unittest.IsolatedAsyncioTestCase):
    """Verify MCP tools and ChatOpsGateway workflows."""

    def setUp(self) -> None:
        self.store = CandidateStateStore(ttl_seconds=86400)
        self.store.create_batch(
            [
                CandidateItem(index=1, title="Severance", season=2, media_type="TV"),
                CandidateItem(index=2, title="Existing Movie", year=2024, media_type="Movie"),
                CandidateItem(index=3, title="Evil Dead Burn", year=2026, media_type="Movie"),
            ],
            batch_id="batch-live",
        )
        set_state_store(self.store)

    def test_query_plex_library_local_inventory_fallback(self) -> None:
        with tempfile.TemporaryDirectory() as tmpdir:
            inv_file = Path(tmpdir) / "existing-movies.json"
            inv_file.write_text(
                json.dumps([{"title": "Existing Movie", "year": 2024, "type": "movie"}]),
                encoding="utf-8",
            )
            found = query_plex_library(
                "Existing Movie",
                2024,
                plex_url="",
                plex_token="",
                existing_inventory_path=str(inv_file),
            )
            self.assertTrue(found["exists"])

            missing = query_plex_library(
                "Evil Dead Burn",
                2026,
                plex_url="",
                plex_token="",
                existing_inventory_path=str(inv_file),
            )
            self.assertFalse(missing["exists"])

    @patch("services.chatops.mcp.server.urllib.request.urlopen")
    @patch("services.chatops.mcp.server.query_plex_library")
    async def test_gateway_download_command_and_plex_dedup(
        self,
        mock_plex: MagicMock,
        mock_urlopen: MagicMock,
    ) -> None:
        def fake_plex(title: str, year: int | None = None) -> dict[str, object]:
            return {"exists": title == "Existing Movie"}

        mock_plex.side_effect = fake_plex

        mock_resp = MagicMock()
        mock_resp.status = 202
        mock_resp.read.return_value = b'{"status":"queued"}'
        mock_resp.__enter__.return_value = mock_resp
        mock_urlopen.return_value = mock_resp

        gw = ChatOpsGateway(
            auth_guard=AuthorizationGuard("U_MANNY"),
            state_store=self.store,
        )

        # Unauthorized user is blocked
        with self.assertRaises(AuthorizationError):
            await gw.handle_download_command(
                user_id="U_INTRUDER",
                text="/download 1 2 3",
                webhook_url="http://localhost:8080/trigger",
                webhook_secret="top-secret",
            )

        # Authorized user dispatches 1 & 3 and skips 2 (already in Plex)
        result = await gw.handle_download_command(
            user_id="U_MANNY",
            text="/download 1 2 3",
            webhook_url="http://localhost:8080/trigger",
            webhook_secret="top-secret",
        )
        self.assertTrue(result["dispatched"])
        self.assertEqual(len(result["approved"]), 2)
        self.assertEqual(len(result["skipped_existing"]), 1)
        self.assertIn("Dispatched 2 item(s)", result["summary_text"])
        self.assertIn("Skipped pre-existing", result["summary_text"])

        active = list_media_sync_candidates("batch-live")
        self.assertTrue(active["active"])

    @patch("services.chatops.mcp.server.subprocess.run")
    async def test_k8s_and_ssh_tools_and_alert_remediation(
        self,
        mock_run: MagicMock,
    ) -> None:
        mock_proc = MagicMock()
        mock_proc.returncode = 0
        mock_proc.stdout = "deployment.apps/vpn-media-sync restarted"
        mock_proc.stderr = ""
        mock_run.return_value = mock_proc

        restart_res = k8s_restart_deployment("media", "vpn-media-sync")
        self.assertTrue(restart_res["restarted"])

        diag_res = k8s_get_pod_diagnostics("media", "vpn-media-sync-0", tail_lines=20)
        self.assertTrue(diag_res["describe_ok"])

        ssh_res = ssh_node_health_check("192.168.1.211")
        self.assertTrue(ssh_res["healthy"])

        bridge = AntigravityBridge()
        with patch.object(bridge, "ask", return_value="Root cause: WireGuard timeout"):
            gw = ChatOpsGateway(
                auth_guard=AuthorizationGuard("U_MANNY"),
                state_store=self.store,
                agent_bridge=bridge,
                alerts_channel_id="C_ALERTS",
            )
            triage = await gw.handle_grafana_alert_message(
                alert_text="Firing: namespace=media deployment=vpn-media-sync pod=vpn-media-sync-0",
            )
            self.assertEqual(triage["targets"]["namespace"], "media")
            self.assertEqual(triage["targets"]["deployment"], "vpn-media-sync")

            action_res = await gw.handle_alert_remediation_action(
                user_id="U_MANNY",
                action_id="remediate_restart_deployment",
                value_json=json.dumps(triage["targets"]),
            )
            self.assertIn("Deployment Restart", action_res["text"])


if __name__ == "__main__":
    unittest.main()
