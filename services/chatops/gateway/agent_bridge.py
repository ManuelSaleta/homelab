"""Bridge between Slack Socket Mode Gateway and Antigravity CLI (`agy`) / SDK."""

from __future__ import annotations

import asyncio
import os
import shutil
from typing import Any

from services.chatops.mcp.server import k8s_get_pod_diagnostics


class AntigravityBridge:
    """Dispatches prompts and alert triage tasks to the Antigravity CLI agent."""

    def __init__(self, agy_binary: str | None = None, timeout_seconds: float = 90.0) -> None:
        self.agy_binary = (
            agy_binary
            or os.environ.get("AGY_BINARY")
            or shutil.which("agy")
            or "agy"
        )
        self.timeout_seconds = timeout_seconds

    async def ask(self, prompt: str, context: dict[str, Any] | None = None) -> str:
        """Send a natural-language prompt to Antigravity SDK or `agy` CLI."""
        cleaned_prompt = (prompt or "").strip()
        if not cleaned_prompt:
            return "Please provide a non-empty prompt for Antigravity."

        full_prompt = cleaned_prompt
        if context:
            ctx_lines = [f"- {k}: {v}" for k, v in sorted(context.items())]
            full_prompt = (
                "Homelab ChatOps Context:\n"
                + "\n".join(ctx_lines)
                + "\n\nUser Request:\n"
                + cleaned_prompt
            )

        # 1. Prefer google-antigravity Python SDK if installed
        try:
            from google_antigravity import Agent  # type: ignore[import-not-found]

            async with Agent() as agent:
                response = await agent.chat(full_prompt)
                return str(response).strip()
        except ImportError:
            pass
        except Exception as exc:  # pragma: no cover - defensive runtime fallback
            return f"Antigravity SDK error: {exc}"

        # 2. Fallback to `agy` CLI subprocess
        try:
            proc = await asyncio.create_subprocess_exec(
                self.agy_binary,
                "--prompt",
                full_prompt,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(
                proc.communicate(), timeout=self.timeout_seconds
            )
            output = (stdout.decode("utf-8", errors="replace") or "").strip()
            if proc.returncode == 0 and output:
                return output
            err_text = (stderr.decode("utf-8", errors="replace") or "").strip()
            return output or err_text or f"agy exited with status {proc.returncode}"
        except FileNotFoundError:
            return (
                "Antigravity CLI (`agy`) binary not found in PATH. "
                "Ensure `agy` is installed on the Fedora workstation host."
            )
        except TimeoutError:
            return f"Antigravity CLI timed out after {int(self.timeout_seconds)}s."

    async def triage_alert(
        self,
        *,
        alert_text: str,
        namespace: str,
        deployment: str,
        pod: str,
    ) -> str:
        """Perform non-destructive K3s diagnostics and generate a root-cause summary."""
        diag_summary = ""
        try:
            diag = await asyncio.to_thread(
                k8s_get_pod_diagnostics,
                namespace,
                pod,
                40,
            )
            logs_excerpt = (diag.get("logs") or "")[-1200:]
            describe_excerpt = (diag.get("describe") or "")[-1200:]
            diag_summary = (
                f"Pod `{namespace}/{pod}` diagnostics:\n"
                f"Describe excerpt:\n```\n{describe_excerpt}\n```\n"
                f"Logs excerpt:\n```\n{logs_excerpt}\n```"
            )
        except Exception as exc:
            diag_summary = f"Could not collect live pod diagnostics for `{namespace}/{pod}`: {exc}"

        prompt = (
            "Analyze the following Grafana homelab alert and Kubernetes pod diagnostics. "
            "Provide a concise root-cause summary and recommended remediation.\n\n"
            f"Alert:\n{alert_text}\n\n{diag_summary}"
        )
        return await self.ask(
            prompt,
            context={
                "namespace": namespace,
                "deployment": deployment,
                "pod": pod,
            },
        )
