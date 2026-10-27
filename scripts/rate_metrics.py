"""Read-only Codex metrics. Timing/deduplication follow Agent Speed METRICS 3/4/6.

No prompts, response text, tool arguments, credentials or full paths enter snapshots.
"""
from __future__ import annotations

import collections
import contextlib
import datetime as dt
import json
import math
import os
import pathlib
import sqlite3
import threading
import time
from dataclasses import dataclass, field


WINDOW_MS = 3000
TOKEN_CAP = 10**12
MAX_LINE = 8 * 1024 * 1024


def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return 0
    try:
        return min(TOKEN_CAP, max(0, int(value))) if math.isfinite(value) else 0
    except (ValueError, OverflowError):
        return 0


def timestamp(value):
    try:
        return int(dt.datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000)
    except (TypeError, ValueError, AttributeError, OverflowError):
        return None


def usage(value):
    value = value if isinstance(value, dict) else {}
    return {key: number(value.get(key)) for key in (
        "input_tokens", "cached_input_tokens", "cache_write_input_tokens",
        "output_tokens", "reasoning_output_tokens", "total_tokens")}


@contextlib.contextmanager
def read_only_db(path):
    connection = sqlite3.connect(pathlib.Path(path).as_uri() + "?mode=ro", uri=True, timeout=1)
    connection.execute("PRAGMA query_only=ON")
    connection.row_factory = sqlite3.Row
    try:
        yield connection
    finally:
        connection.close()


@dataclass
class Response:
    thread: str
    model: str | None
    start: int | None
    end: int
    output: int
    input: int

    def interval(self):
        if self.start is None or not 0 <= self.end - self.start <= 900_000:
            return None
        return (min(self.start, self.end - 500), self.end)


@dataclass
class ThreadMetrics:
    id: str
    name: str
    model: str | None = None
    project: str | None = None
    created: int | None = None
    pending: int | None = None
    phase: str = "idle"
    streaming: bool = False
    modern: bool = False
    last_line: int = 0
    total: dict = field(default_factory=dict)
    last_response: dict = field(default_factory=dict)
    previous_legacy: dict | None = None

    def on_input(self, ts):
        if not self.streaming:
            self.pending = ts
            self.phase = "generating"

    def feed(self, event, ledger):
        ts = timestamp(event.get("timestamp"))
        if ts is None:
            return
        kind = event.get("type")
        p = event.get("payload")
        if not isinstance(p, dict):
            return
        if kind == "session_meta":
            if self.created is None:
                self.created = ts
            return
        if self.created is not None and ts < self.created - 2000:
            return
        self.last_line = max(self.last_line, ts)
        if kind == "turn_context":
            self.model = p.get("model") or self.model
            return
        if kind == "token_usage_record":
            self.modern = True
            if isinstance(p.get("thread_token_usage"), dict):
                self.total = usage(p["thread_token_usage"])
            key = p.get("response_id") or f"modern:{p.get('thread_id', self.id)}:{ts}"
            self.emit(str(key), usage(p.get("usage")), ts, ledger)
            return
        subkind = p.get("type")
        if kind == "event_msg":
            if subkind == "token_count":
                info = p.get("info") or {}
                if isinstance(info, dict) and isinstance(info.get("total_token_usage"), dict):
                    self.total = usage(info["total_token_usage"])
                    if not self.modern:
                        current = usage(info["total_token_usage"])
                        last = usage(info.get("last_token_usage"))
                        if self.previous_legacy is None:
                            delta = last if isinstance(info.get("last_token_usage"), dict) else current
                        else:
                            raw = {k: current[k] - self.previous_legacy[k] for k in current}
                            if raw["total_tokens"] == 0:
                                return
                            delta = last if any(v < 0 for v in raw.values()) else raw
                        self.previous_legacy = current
                        self.emit(f"legacy:{self.id}:{current['total_tokens']}", delta, ts, ledger)
                    elif self.streaming:
                        self.phase, self.pending, self.streaming = "tools", ts, False
            elif subkind in ("task_started", "user_message"):
                self.on_input(ts)
            elif subkind in ("task_complete", "turn_aborted"):
                self.phase, self.pending, self.streaming = "idle", None, False
            return
        if kind == "response_item":
            if subkind == "reasoning" or (isinstance(subkind, str) and subkind.endswith("_call")) or (subkind == "message" and p.get("role") == "assistant"):
                self.streaming = True
                self.phase = "generating"
                self.pending = self.pending if self.pending is not None else ts
            elif (isinstance(subkind, str) and subkind.endswith("_output")) or (subkind == "message" and p.get("role") in ("user", "developer", "system")):
                self.on_input(ts)

    def emit(self, key, tokens, ts, ledger):
        if key in ledger.seen:
            return
        ledger.seen[key] = True
        while len(ledger.seen) > 65_536:
            ledger.seen.popitem(last=False)
        self.last_response = tokens
        ledger.responses.append(Response(self.id, self.model, self.pending, ts,
                                         tokens["output_tokens"], tokens["input_tokens"]))
        self.phase, self.pending, self.streaming = "tools", ts, False


class Ledger:
    def __init__(self):
        self.seen = collections.OrderedDict()
        self.responses = collections.deque(maxlen=6000)

    def speed(self, states, now, selected=None):
        valid = [r for r in self.responses if r.interval() is not None and r.end <= now
                 and (selected is None or r.thread == selected)]
        measured = estimated = 0.0
        last = None
        for response in valid:
            start, end = response.interval()
            overlap = max(0, min(now, end) - max(now - WINDOW_MS, start))
            measured += response.output * overlap / (end - start)
            if last is None or response.end > last.end:
                last = response
        waiting = False
        for state in states:
            if selected is not None and state.id != selected:
                continue
            if state.phase != "generating" or state.pending is None or now - state.last_line > 300_000:
                continue
            own = [r for r in valid if r.thread == state.id and r.end >= now - 600_000][-3:]
            recent = own or [r for r in valid if r.model == state.model and r.end >= now - 1_800_000][-10:]
            recent = recent or [r for r in valid if r.end >= now - 1_800_000][-10:]
            duration = sum(r.interval()[1] - r.interval()[0] for r in recent)
            if not duration:
                waiting = True
                continue
            rate = sum(r.output for r in recent) / (duration / 1000)
            own_end = max((r.end for r in valid if r.thread == state.id), default=0)
            overlap = max(0, now - max(now - WINDOW_MS, state.pending, own_end))
            estimated += rate * overlap / 1000
        rate = (measured + estimated) / (WINDOW_MS / 1000)
        return {"tokPerSecond": None if waiting and rate == 0 else round(rate, 1),
                "estimated": estimated > 0 or waiting,
                "lastCompletedTokPerSecond": round(last.output / ((last.interval()[1] - last.interval()[0]) / 1000), 1) if last else None,
                "lastCompletedAt": last.end if last else None}


class Cursor:
    def __init__(self, path, state, ledger):
        self.path = path
        self.state = state
        self.offset = 0
        self.buffer = b""
        with path.open("rb") as stream:
            first = stream.readline(MAX_LINE)
            self.parse(first, ledger)
            size = stream.seek(0, 2)
            stream.seek(max(0, size - 2 * 1024 * 1024))
            if stream.tell():
                stream.readline()
            block = stream.read()
            # Seeding starts mid-session: prefer modern usage before processing
            # legacy notifications from the same modern-format tail.
            self.state.modern = b'"token_usage_record"' in block
            lines = block.split(b"\n")
            for line in lines[:-1]:
                self.parse(line, ledger)
            self.buffer = lines[-1]
            self.offset = stream.tell()

    def parse(self, line, ledger):
        if len(line) > MAX_LINE:
            return
        try:
            event = json.loads(line)
            if isinstance(event, dict):
                self.state.feed(event, ledger)
        except (ValueError, UnicodeError):
            pass

    def poll(self, ledger):
        size = self.path.stat().st_size
        if size < self.offset:
            raise OSError("Transcript was replaced; reseed required")
        if size == self.offset:
            return
        with self.path.open("rb") as stream:
            stream.seek(self.offset)
            block = stream.read(2 * 1024 * 1024)
        self.offset += len(block)
        parts = (self.buffer + block).split(b"\n")
        for line in parts[:-1]:
            self.parse(line, ledger)
        self.buffer = parts[-1]
        if len(self.buffer) > MAX_LINE:
            self.buffer = b""


class MetricsCollector:
    def __init__(self, codex_home=None):
        self.home = pathlib.Path(codex_home or os.environ.get("CODEX_HOME") or pathlib.Path.home() / ".codex")
        self.ledger = Ledger()
        self.cursors = {}
        self.selected = set()
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.last_scan = 0.0
        self.last_error = None

    def discover(self):
        with read_only_db(self.home / "state_5.sqlite") as db:
            rows = db.execute("SELECT t.id, substr(COALESCE(t.name,t.title,'未命名'),1,100) name, t.model, t.rollout_path, p.name project FROM threads t LEFT JOIN projects p ON p.id=t.project_id WHERE t.archived=0").fetchall()
        candidates = []
        for row in rows:
            path = pathlib.Path(row["rollout_path"])
            try:
                info = path.stat()
                if path.suffix == ".jsonl" and (row["id"] in self.selected or time.time() - info.st_mtime < 3600):
                    candidates.append((info.st_mtime, row, path))
            except OSError:
                continue
        for _, row, path in sorted(candidates, key=lambda item: (item[1]["id"] in self.selected, item[0]), reverse=True)[:16]:
            key = str(path)
            if key not in self.cursors:
                state = ThreadMetrics(row["id"], row["name"], row["model"], row["project"])
                self.cursors[key] = Cursor(path, state, self.ledger)
            else:
                self.cursors[key].state.name = row["name"]
                self.cursors[key].state.project = row["project"]
        for key in list(self.cursors):
            if self.cursors[key].state.id not in self.selected and time.time() * 1000 - self.cursors[key].state.last_line > 3_600_000:
                del self.cursors[key]

    def tick(self):
        with self.lock:
            try:
                if time.monotonic() - self.last_scan > 10:
                    self.discover()
                    self.last_scan = time.monotonic()
                for key, cursor in list(self.cursors.items()):
                    try:
                        cursor.poll(self.ledger)
                    except OSError:
                        del self.cursors[key]
                self.last_error = None
            except (OSError, sqlite3.Error):
                self.last_error = "本机用量暂不可读，保留已有数据"

    def start(self):
        def run():
            while not self.stop.is_set():
                self.tick()
                self.stop.wait(1)
        threading.Thread(target=run, daemon=True, name="metrics-read-only").start()

    def snapshot(self, selected=None):
        now = int(time.time() * 1000)
        with self.lock:
            states = sorted((cursor.state for cursor in self.cursors.values()), key=lambda state: state.last_line, reverse=True)
            distinct = {}
            for state in states:
                distinct.setdefault(state.id, state)
            states = list(distinct.values())
            if selected is None and states:
                selected = states[0].id
            state = next((item for item in states if item.id == selected), None)
            result = {"updatedAt": now, "selectedThreadId": selected,
                      "speed": self.ledger.speed(states, now, selected),
                      "thread": {"name": state.name, "model": state.model, "project": state.project,
                                 "phase": state.phase if now - state.last_line < 300_000 else "idle",
                                 "total": state.total, "lastResponse": state.last_response} if state else None,
                      "threads": [{"id": item.id, "name": item.name, "model": item.model,
                                   "phase": item.phase if now - item.last_line < 300_000 else "idle"} for item in states],
                      "error": self.last_error}
        return result
