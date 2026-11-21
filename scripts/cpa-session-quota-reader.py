#!/usr/bin/env python3
"""Return quota for the OAuth account that served one exact Codex session."""

from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import sys
import time
import urllib.request


STACK = Path(__file__).resolve().parent.parent
DB = STACK / "data" / "cpam" / "usage.sqlite"
SESSION_ID = re.compile(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", re.ASCII)
LOOKBACK_MS = 72 * 60 * 60 * 1000
WINDOWS = {300, 10080}
RESPONSE = "failed = 0 AND generate = 1 AND path IN ('/responses', '/v1/responses')"
PAW_ORIGIN = "https://sub2api-paw.ling.plus"
PAW_BASES = {PAW_ORIGIN, PAW_ORIGIN + "/v1"}
WEEK_MS = 7 * 24 * 60 * 60 * 1000


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kwargs):
        return None


def cpam_read(path, payload=None):
    key = (STACK / "secrets" / "cpam-admin.key").read_text(encoding="utf-8").strip()
    request = urllib.request.Request("http://127.0.0.1:18318/v0/management/" + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=5) as reply:
        return json.loads(reply.read(2 * 1024 * 1024))


def paw_week(body, now):
    if not isinstance(body, dict) or body.get("isValid") is not True:
        return None
    subscription = body.get("subscription")
    if not isinstance(subscription, dict):
        return None
    limit, used = subscription.get("weekly_limit_usd"), subscription.get("weekly_usage_usd")
    start = subscription.get("weekly_window_start")
    if not (type(limit) in (int, float) and 0 < limit <= 1e9
            and type(used) in (int, float) and 0 <= used <= 1e9 and isinstance(start, str)):
        return None
    try:
        window_start = datetime.fromisoformat(start.replace("Z", "+00:00"))
        if window_start.tzinfo is None:
            return None
        start_ms = int(window_start.timestamp() * 1000)
    except (ValueError, OverflowError, OSError):
        return None
    if not now - WEEK_MS < start_ms <= now + 120000:
        return None
    return {"minutes": 10080, "remainingUsd": round(max(0, limit - used), 2), "resetsAt": start_ms + WEEK_MS}


def read_paw(auth, matched_at, cpam=cpam_read, clock=lambda: time.time_ns() // 1_000_000):
    windows = []
    try:
        entries = cpam("codex-api-key").get("codex-api-key", [])
        matches = [entry for entry in entries if isinstance(entry, dict)
                   and entry.get("auth-index") == auth
                   and str(entry.get("base-url", "")).rstrip("/") in PAW_BASES]
        if len(matches) == 1:
            result = cpam("api-call", {"authIndex": auth, "method": "GET",
                "url": PAW_ORIGIN + "/v1/usage",
                "header": {"Authorization": "Bearer $TOKEN$", "Accept": "application/json"}})
            if result.get("status_code", result.get("statusCode")) == 200:
                body = result.get("body")
                window = paw_week(json.loads(body) if isinstance(body, str) else body, clock())
                if window:
                    windows = [window]
    except (OSError, ValueError, TypeError, AttributeError):
        pass
    return {"status": "matched", "provider": "paw",
            "accountAlias": hashlib.sha256(("paw-quota:" + auth).encode()).hexdigest()[:8].upper(),
            "matchedAt": matched_at, "observedAt": clock(), "windows": windows}


def parse_quota(raw):
    try:
        data = json.loads(raw or "{}")
    except (TypeError, ValueError):
        return {}
    quota = data.get("quota") if isinstance(data, dict) else None
    return quota if isinstance(quota, dict) else {}


def quota_windows(quota, now):
    windows = {}
    for name in ("primary", "secondary"):
        window = quota.get(name)
        if not isinstance(window, dict):
            continue
        minutes = window.get("window_minutes")
        used = window.get("used_percent")
        reset = window.get("reset_at_ms")
        if type(minutes) is int and minutes in WINDOWS:
            valid = (type(used) in (int, float) and 0 <= used <= 100
                     and type(reset) is int and now < reset <= now + minutes * 60000 + 120000)
            # Conflicting duplicate windows cannot be attributed to a specific period.
            windows[minutes] = None if minutes in windows or not valid else {
                "minutes": minutes,
                "remaining": 100 - used,
                "resetsAt": reset,
            }
    return [window for _, window in sorted(windows.items()) if window is not None]


def read_session(db, session_id, now):
    db.row_factory = sqlite3.Row
    # Read the latest successful generation even if it used another provider.
    event = db.execute(f"""
        SELECT id, timestamp_ms, request_id, provider, auth_type,
               auth_index, auth_account_id_snapshot, response_metadata_json
        FROM usage_events
        WHERE session_id = ? AND timestamp_ms BETWEEN ? AND ? AND {RESPONSE}
        ORDER BY timestamp_ms DESC, id DESC
        LIMIT 1
    """, (session_id, now - LOOKBACK_MS, now)).fetchone()
    if event is None:
        return {"status": "no-match"}
    if event["provider"] != "codex":
        return {"status": "unsupported"}

    account = event["auth_account_id_snapshot"]
    auth = event["auth_index"]
    request = event["request_id"]
    if not auth or not request:
        return {"status": "no-match"}
    routes = db.execute(f"""
        SELECT DISTINCT provider, auth_type, auth_index, auth_account_id_snapshot
        FROM usage_events
        WHERE request_id = ? AND session_id = ? AND {RESPONSE}
        LIMIT 2
    """, (request, session_id)).fetchall()
    if len(routes) != 1:
        return {"status": "ambiguous"}

    if event["auth_type"] == "apikey" and re.fullmatch(r"[a-f0-9]{16}", auth, re.ASCII):
        try:
            origin = db.execute("SELECT json_extract(raw_json, '$.base_url') FROM usage_events WHERE id = ?",
                                (event["id"],)).fetchone()[0]
        except sqlite3.OperationalError:
            origin = None
        if isinstance(origin, str) and origin.rstrip("/") in PAW_BASES:
            return {"status": "paw", "auth": auth, "matchedAt": event["timestamp_ms"]}
    if event["auth_type"] != "oauth":
        return {"status": "unsupported"}
    if not account:
        return {"status": "no-match"}

    quota = parse_quota(event["response_metadata_json"])
    observed = event["timestamp_ms"]
    scope = quota.get("active_limit")
    if isinstance(scope, str) and scope:
        # A more recent request may have updated this exact credential and quota bucket.
        # Key by both identities so shared workspace IDs and credential replacements cannot cross.
        samples = db.execute(f"""
            SELECT timestamp_ms, response_metadata_json
            FROM usage_events
            WHERE auth_index = ? AND auth_account_id_snapshot = ?
              AND provider = 'codex' AND auth_type = 'oauth'
              AND timestamp_ms BETWEEN ? AND ? AND {RESPONSE}
              AND response_metadata_json IS NOT NULL
            ORDER BY timestamp_ms DESC, id DESC
            LIMIT 32
        """, (auth, account, observed, now))
        for sample in samples:
            candidate = parse_quota(sample["response_metadata_json"])
            if candidate.get("active_limit") == scope and any(
                    isinstance(candidate.get(key), dict) for key in ("primary", "secondary")):
                observed = sample["timestamp_ms"]
                quota = candidate
                break

    return {
        "status": "matched",
        "provider": "codex",
        "accountAlias": hashlib.sha256(("codex-quota:" + account).encode()).hexdigest()[:8].upper(),
        "matchedAt": event["timestamp_ms"],
        "observedAt": observed,
        "windows": quota_windows(quota, now),
    }


def main():
    try:
        request = json.loads(sys.stdin.read(1024))
        session_id = request.get("sessionId") if isinstance(request, dict) else None
        if not isinstance(session_id, str) or not SESSION_ID.fullmatch(session_id):
            raise ValueError("Invalid request")
        with sqlite3.connect(DB.as_uri() + "?mode=ro", uri=True, timeout=2) as db:
            db.execute("PRAGMA query_only = ON")
            db.execute("BEGIN")
            payload = read_session(db, session_id, time.time_ns() // 1_000_000)
        if payload["status"] == "paw":
            payload = read_paw(payload["auth"], payload["matchedAt"])
    except (OSError, ValueError, sqlite3.Error):
        print('{"version":1,"status":"unavailable"}')
        return 1
    print(json.dumps({"version": 1, **payload}, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
