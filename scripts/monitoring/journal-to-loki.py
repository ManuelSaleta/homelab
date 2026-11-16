#!/usr/bin/env python3
"""Lightweight systemd journal -> Loki forwarder for external hosts (mothership, micro-nas).

Filters to Warning+ (PRIORITY <= 4) plus SSH/sudo/su/login auth events to respect
the 16 GB homelab RAM guardrail, and batches pushes to Loki's HTTP API.
"""

import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

LOKI_URL = os.environ.get("LOKI_URL", "http://192.168.1.210:31310/loki/api/v1/push")
NODE_NAME = os.environ.get("NODE_NAME", socket.gethostname())
AUTH_COMMS = {"sshd", "sudo", "su", "login"}
BATCH_MAX = 20
FLUSH_INTERVAL_SEC = 5.0


def push_batch(streams_map):
    if not streams_map:
        return
    payload = {
        "streams": [
            {"stream": dict(labels), "values": entries}
            for labels, entries in streams_map.items()
        ]
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        LOKI_URL,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            resp.read()
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        print(f"journal-to-loki push error: {exc}", file=sys.stderr, flush=True)


def main():
    cmd = ["journalctl", "-f", "-n", "25", "-o", "json", "--no-pager"]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, text=True, bufsize=1)
    assert proc.stdout is not None

    streams_map = {}
    count = 0
    last_flush = time.monotonic()

    for line in proc.stdout:
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue

        prio_raw = str(entry.get("PRIORITY", "6"))
        comm = str(entry.get("_COMM", "") or "unknown")
        try:
            prio = int(prio_raw)
        except ValueError:
            prio = 6

        if prio > 4 and comm not in AUTH_COMMS:
            continue

        unit = str(entry.get("_SYSTEMD_UNIT", "") or entry.get("SYSLOG_IDENTIFIER", "") or comm)
        msg = entry.get("MESSAGE", "")
        if isinstance(msg, list):
            msg = bytes(msg).decode("utf-8", errors="replace")
        else:
            msg = str(msg)

        ts_us = entry.get("__REALTIME_TIMESTAMP")
        if ts_us:
            ts_ns = str(int(ts_us) * 1000)
        else:
            ts_ns = str(time.time_ns())

        labels = (
            ("job", "systemd-journal"),
            ("node", NODE_NAME),
            ("unit", unit),
            ("comm", comm),
            ("priority", prio_raw),
        )
        streams_map.setdefault(labels, []).append([ts_ns, msg])
        count += 1

        now = time.monotonic()
        if count >= BATCH_MAX or (now - last_flush) >= FLUSH_INTERVAL_SEC:
            push_batch(streams_map)
            streams_map.clear()
            count = 0
            last_flush = now

    push_batch(streams_map)


if __name__ == "__main__":
    main()
