from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import sqlite3
import unittest


repo = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("reader", repo / "scripts" / "cpa-session-quota-reader.py")
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)

NOW = 1_800_000_000_000
A = "00000000-0000-4000-8000-000000000001"
B = "00000000-0000-4000-8000-000000000002"


def snapshot(short=10, week=25, scope="codex", reset_short=NOW + 3600000):
    def window(minutes, used, at):
        return {"window_minutes": minutes, "used_percent": used, "reset_at_ms": at}

    return json.dumps({"quota": {
        "active_limit": scope,
        "primary": window(300 if short is not None else 0, short, reset_short),
        "secondary": window(10080, week, NOW + 86400000),
    }})


class SessionQuotaTest(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.addCleanup(self.db.close)
        self.db.execute("""
            CREATE TABLE usage_events (
                id INTEGER PRIMARY KEY, timestamp_ms INTEGER, request_id TEXT, session_id TEXT,
                provider TEXT, auth_type TEXT, auth_index TEXT, auth_account_id_snapshot TEXT,
                response_metadata_json TEXT, failed INTEGER, generate INTEGER, path TEXT, raw_json TEXT
            )
        """)

    def event(self, sid=A, seconds_ago=30, request="test-request", auth="auth-a", account="actor-a",
              provider="codex", auth_type="oauth", failed=False, generate=True, path="/v1/responses", data=None, origin=None):
        self.db.execute("""
            INSERT INTO usage_events VALUES (NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, (NOW - seconds_ago * 1000, request, sid, provider, auth_type, auth, account,
              data, failed, generate, path, json.dumps({"base_url": origin})))

    def get(self, sid=A):
        return reader.read_session(self.db, sid, NOW)

    def test_exact_session_and_attempt_success(self):
        self.event(failed=True, auth="auth-failed", account="failed-actor", data=snapshot(99, 99))
        self.event(auth="auth-b", account="actor-b", data=snapshot(14, 28))
        self.event(sid=B, seconds_ago=1, request="other", data=snapshot(55, 60))
        self.event(seconds_ago=0, request="compact", path="/v1/responses/compact", data=snapshot(33, 45))
        a, b = self.get(), self.get(B)
        self.assertEqual([w["remaining"] for w in a["windows"]], [86, 72])
        self.assertEqual([w["remaining"] for w in b["windows"]], [45, 40])
        self.assertNotEqual(a["accountAlias"], b["accountAlias"])
        self.assertEqual(set(a), {"status", "provider", "accountAlias", "matchedAt", "observedAt", "windows"})

    def test_newer_quota_uses_exact_account_credential_and_bucket(self):
        self.event(seconds_ago=50, data=snapshot())
        self.event(sid=B, request="refresh", seconds_ago=10, data=snapshot(20, 30))
        self.event(sid=B, request="other-credential", seconds_ago=5, auth="auth-b", data=snapshot(70, 70))
        self.event(sid=B, request="other-account", seconds_ago=4, account="actor-b", data=snapshot(80, 80))
        self.event(sid=B, request="other-bucket", seconds_ago=3, data=snapshot(90, 90, "another-bucket"))
        result = self.get()
        self.assertEqual([w["remaining"] for w in result["windows"]], [80, 70])
        self.assertEqual(result["observedAt"], NOW - 10000)

    def test_recent_other_provider_prevents_reusing_codex(self):
        self.event(data=snapshot())
        self.event(request="not-oauth", seconds_ago=5, auth_type="apikey")
        self.assertEqual(self.get(), {"status": "unsupported"})

    def test_no_guessing_for_missing_and_ambiguous_sessions(self):
        self.event(data=snapshot())
        self.event(auth="auth-b", account="actor-b", data=snapshot())
        self.event(sid=B, seconds_ago=8, failed=True, data=snapshot())
        self.assertEqual(self.get(), {"status": "ambiguous"})
        self.assertEqual(self.get(B), {"status": "no-match"})

    def test_expired_or_missing_period_does_not_use_old_values(self):
        self.event(seconds_ago=59, request="old", data=snapshot())
        self.event(seconds_ago=6, data=snapshot(short=None))
        self.assertEqual([w["minutes"] for w in self.get()["windows"]], [10080])
        self.event(seconds_ago=5, request="expired", data=snapshot(week=True, reset_short=NOW - 1))
        self.assertEqual(self.get()["windows"], [])

    def test_remaining_zero_is_a_valid_reading(self):
        self.event(data=snapshot(short=100, week=100))
        self.assertEqual([w["remaining"] for w in self.get()["windows"]], [0, 0])

    def test_paw_auth_index_gates_the_weekly_dollar_read(self):
        auth = "0123456789abcdef"
        self.event(auth_type="apikey", auth=auth, account="", origin=reader.PAW_ORIGIN + "/v1")
        match = self.get()
        self.assertEqual(match, {"status": "paw", "auth": auth, "matchedAt": NOW - 30000})
        calls = []
        body = {"isValid": True, "subscription": {
            "weekly_limit_usd": 200, "weekly_usage_usd": 82.34,
            "weekly_window_start": datetime.fromtimestamp(NOW / 1000 - 3600, timezone.utc).isoformat()}}

        def cpam(path, payload=None):
            calls.append((path, payload))
            if path == "codex-api-key":
                return {path: [{"auth-index": auth, "base-url": reader.PAW_ORIGIN + "/v1"}]}
            return {"status_code": 200, "body": json.dumps(body)}

        result = reader.read_paw(auth, match["matchedAt"], cpam, lambda: NOW)
        self.assertEqual(calls[1][1]["authIndex"], auth)
        self.assertEqual(calls[1][1]["method"], "GET")
        self.assertEqual(result["windows"][0]["remainingUsd"], 117.66)
        self.assertNotIn("auth", result)

        calls.clear()
        result = reader.read_paw("0123456789abcdef"[::-1], match["matchedAt"], cpam, lambda: NOW)
        self.assertEqual(result["windows"], [])
        self.assertEqual([call[0] for call in calls], ["codex-api-key"])
        self.assertIsNone(reader.paw_week(body, NOW + reader.WEEK_MS))


if __name__ == "__main__":
    unittest.main()
