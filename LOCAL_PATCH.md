# Local native-rate patch

Based on upstream v3.2.2, commit 1f32ee598cb56875075968a86e0795873688b5f6.

Adds a per-window tok/s metric, measured from completed Codex usage events and
described as estimated in the tooltip while a response is generating. The main
display contains only a number and tok/s. The read-only
Python collector is derived from the local metrics prototype and follows Agent
Speed v1.4.1 timing and deduplication rules. It does not call a model, access account
credentials, open an HTTP port, change routing or register an MCP plugin.

When the rate metric is selected, align the strip immediately before the native
right-hand controls, use their font/color, and remove the extra panel-style border.
All original Token/quota detail controls remain available by clicking the rate.

Automatic monitor updates remain off initially. When enabled, updates are fetched
only from wushuai0802/codex-usage-monitor-windows. The Store Codex path is resolved afresh by
the original launcher on each cold start. An absent or changed composer hides the
monitor instead of guessing a new placement.

Adds model-aware personal quotas through the existing NAS SSH connection:
GPT → Paw, Gemini → Antigravity Gemini windows, DeepSeek → WorkBuddy CN credits
only. LV is excluded. No provider credential is copied to Windows. Source-specific
units and balance/update timestamps are retained; AG's compact value is the
minimum account/window remainder, not a summed balance. Only quota reads are
issued, never generation, account-refresh or check-in requests.

The adapter uses a fixed NAS reader path, bounded JSON stdin/stdout, query timeout,
60-second source caches and no new network listener. The quota metric is selected
by default while an explicit user opt-out is preserved. Provider switches never
reuse the previous supplier's reading. Tests are in tests/model-quota.mjs and
tests/provider-quota-reader.py. Public builds default to a disabled adapter and
load operator-owned model-quota.local.json only when explicitly configured.
See docs/model-quota.md for setup and data freshness boundaries.

The launcher uses the Windows package activation API directly, including warm
launches, with an activation mutex acquired first. It never routes a same-AUMID
custom shortcut through AppsFolder. This avoids self-invocation loops while
preserving the existing Codex process and monitor.
