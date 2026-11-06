import fs from 'node:fs/promises';
import { existsSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { IpcRoute } from './ipc-route.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

function parseArgs(argv) {
  const defaultRuntime = path.resolve(here, '..');
  const defaultState = process.env.LOCALAPPDATA
    ? path.join(process.env.LOCALAPPDATA, 'CodexUsageMonitor', 'companion')
    : path.join(defaultRuntime, 'state');
  const opts = {
    stateDir: process.env.CODEX_USAGE_COMPANION_STATE || defaultState,
    runtimeDir: process.env.CODEX_USAGE_RUNTIME_DIR || defaultRuntime,
    parentPid: Number(process.env.CODEX_USAGE_PARENT_PID || 0),
    maxSeconds: 0,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a === '--state-dir' && argv[i + 1]) opts.stateDir = path.resolve(argv[++i]);
    else if (a === '--runtime-dir' && argv[i + 1]) opts.runtimeDir = path.resolve(argv[++i]);
    else if (a === '--parent-pid' && argv[i + 1]) opts.parentPid = Number(argv[++i]);
    else if (a === '--max-seconds' && argv[i + 1]) opts.maxSeconds = Number(argv[++i]);
  }
  return opts;
}

function resolveRunnableCodexCli() {
  const localAppData = process.env.LOCALAPPDATA || '';
  const isWindowsApps = (p) => /\\WindowsApps\\/i.test(String(p || ''));
  const candidates = [
    process.env.CODEX_USAGE_CODEX_PATH,
    localAppData ? path.join(localAppData, 'OpenAI', 'Codex', 'bin', 'codex.exe') : null,
    localAppData ? path.join(localAppData, 'Programs', 'OpenAI Codex CLI', 'codex.exe') : null,
  ].filter((p) => p && !isWindowsApps(p));
  for (const candidate of candidates) {
    try { if (existsSync(candidate) && statSync(candidate).isFile()) return candidate; } catch {}
  }
  if (localAppData) {
    const mirrorDir = path.join(localAppData, 'CodexUsageMonitor', 'runtime', 'codex-cli');
    try {
      const mirrored = readdirSync(mirrorDir)
        .filter((f) => /^codex-.*\.exe$/i.test(f))
        .map((f) => path.join(mirrorDir, f))
        .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
      if (mirrored.length) return mirrored[0];
    } catch {}
  }
  return 'codex';
}

function isPidAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return true;
  try { process.kill(pid, 0); return true; } catch (err) { return err?.code === 'EPERM'; }
}

const DEFAULT_SELECTIONS = {
  session: ['outputSpeed', 'currentTaskTokens', 'cacheHitRate'],
  official: ['primaryRemaining', 'secondaryRemaining'],
  'model-quota': ['remaining'],
};

const COMPACT_LABELS = {
  outputSpeed: '',
  currentTaskTokens: '会话',
  lastTurnTokens: '上次',
  cacheHitRate: '总缓存',
  lastTurnCacheHitRate: '上次缓存',
  contextCompactions: '压缩',
  executionTime: '耗时',
  primaryRemaining: '5时',
  secondaryRemaining: '7天',
  primaryReset: '重置',
  secondaryReset: '周重置',
  todayTokens: '今日',
  last7DaysTokens: '近7天',
  lifetimeTokens: '累计',
};

function formatChineseTokens(raw) {
  const text = String(raw ?? '--').trim();
  if (/[万亿KMB%]/.test(text)) return text;
  const num = Number(text.replace(/,/g, ''));
  if (!Number.isFinite(num) || num < 0) return text || '--';
  if (num >= 100000000) return `${(num / 100000000).toFixed(2)}亿`;
  if (num >= 10000) return `${Math.round(num / 10000)}万`;
  return String(Math.round(num));
}

function formatDurationMetric(metric, now = Date.now()) {
  if (metric?.durationMs == null || !Number.isFinite(Number(metric.durationMs))) return String(metric?.value ?? '--');
  const sampledAt = Date.parse(metric.sampledAt);
  const elapsed = metric.running && Number.isFinite(sampledAt) ? Math.max(0, now - sampledAt) : 0;
  const seconds = Math.floor((Number(metric.durationMs) + Math.min(elapsed, 60000)) / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds / 60) % 60;
  const rest = seconds % 60;
  const pad = (v) => String(v).padStart(2, '0');
  const body = hours ? `${hours}时${pad(minutes)}分${pad(rest)}秒`
    : minutes ? `${minutes}分${pad(rest)}秒` : `${rest}秒`;
  return `${metric.estimated || metric.running ? '≈' : ''}${body}${metric.incomplete || elapsed > 60000 ? '+' : ''}`;
}

const opts = parseArgs(process.argv.slice(2));
await fs.mkdir(opts.stateDir, { recursive: true });

const scriptsUrl = (name) => pathToFileURL(path.join(opts.runtimeDir, 'scripts', name)).href;
const { CombinedUsageClient } = await import(scriptsUrl('usage-client.mjs'));
const { LiveRateClient, withLiveRate } = await import(scriptsUrl('live-rate.mjs'));
const { ModelQuotaClient } = await import(scriptsUrl('model-quota.mjs'));
const { createUiSettingsStore, normalizeUiSettings } = await import(scriptsUrl('ui-settings.mjs'));

const settingsStore = await createUiSettingsStore();
let settings = settingsStore.current || normalizeUiSettings({
  schemaVersion: 2,
  metrics: DEFAULT_SELECTIONS,
  metricOrder: [
    'session:outputSpeed',
    'session:currentTaskTokens',
    'session:cacheHitRate',
    'model-quota:remaining',
    'official:primaryRemaining',
    'official:secondaryRemaining',
  ],
  unifiedMetricsVersion: 2,
  minimalMode: true,
  showApiColumns: true,
  showResetForecast: true,
});

let stopped = false;
let routeStatus = { connected: false, threadId: null, activeThreadId: null, routeCount: 0 };
let baseUsage = null;
let anchor = null;
let dirty = true;
let lastHandledCmdSeq = 0;
const warmCachePath = path.join(opts.stateDir, 'warm-cache.json');
let warmCache = { at: 0, officialRows: [], quotaByKey: {} };
try {
  const parsed = JSON.parse((await fs.readFile(warmCachePath, 'utf8')).replace(/^\uFEFF/, ''));
  if (parsed && Date.now() - Number(parsed.at || 0) < 15 * 60 * 1000) warmCache = parsed;
} catch {}

const codexCli = resolveRunnableCodexCli();
const rate = new LiveRateClient();
const quota = new ModelQuotaClient({ onUpdate: () => { dirty = true; } });
await quota.initialize();

const usageClient = new CombinedUsageClient({
  command: codexCli,
  refreshMs: settings.refreshEvery30Seconds ? 30000 : 60000,
  onUpdate: (u) => { baseUsage = u; dirty = true; },
});

function effectiveThreadId() {
  return routeStatus.threadId || baseUsage?.currentThreadId || null;
}

const route = new IpcRoute((status) => {
  const prev = routeStatus.threadId;
  routeStatus = status;
  if (status.threadId !== prev) {
    usageClient.setVisibleThreadIds(status.threadId ? [status.threadId] : []);
    dirty = true;
  }
});

function selectedIdsForSource(sourceId, availableIds) {
  const hasExplicit = Object.prototype.hasOwnProperty.call(settings.metrics || {}, sourceId);
  const raw = hasExplicit ? (settings.metrics[sourceId] || []) : (DEFAULT_SELECTIONS[sourceId] || []);
  return raw.filter((id) => availableIds.has(id));
}

function formatResetShort(tsSeconds) {
  const n = Number(tsSeconds);
  if (!Number.isFinite(n) || n <= 0) return null;
  const d = new Date(n * 1000);
  if (!Number.isFinite(d.getTime())) return null;
  const pad = (v) => String(v).padStart(2, '0');
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}重置`;
}

function formatActivityTimestamp(ms) {
  const d = new Date(Number(ms));
  if (!Number.isFinite(d.getTime())) return '--';
  const pad = (v) => String(v).padStart(2, '0');
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const OFFICIAL_LABEL_FIX = {
  lifetimeTokens: '累计 Token',
  todayTokens: '今日 Token',
  last7DaysTokens: '近7天 Token',
  primaryRemaining: '5小时剩余',
  secondaryRemaining: '7天剩余',
  primaryReset: '重置时间',
};

function normalizeSourceMetrics(sourceId, source, now) {
  const list = Array.isArray(source?.metrics) ? source.metrics : [];
  const out = [];
  for (const m of list) {
    if (!m || typeof m.id !== 'string' || m.id === 'autoResume') continue;
    let value = String(m.value ?? '--');
    if (m.id === 'outputSpeed') {
      value = /^\d+(?:\.\d+)?\s*tok\/s$/.test(value) ? value : '0.0 tok/s';
    } else if (m.id === 'executionTime') {
      value = formatDurationMetric(m, now);
    } else if (m.id.endsWith('Tokens')) {
      value = formatChineseTokens(value);
    }
    const normalizedLabel = (sourceId === 'official' && OFFICIAL_LABEL_FIX[m.id]) ? OFFICIAL_LABEL_FIX[m.id] : String(m.label || m.id);
    const compact = COMPACT_LABELS[m.id] ?? normalizedLabel;
    const minimalText = m.id === 'outputSpeed' ? value : value.replace(/\s+/g, ' ').trim();
    const normalText = m.id === 'outputSpeed' || !compact ? minimalText : `${compact} ${minimalText}`;
    const selectable = sourceId !== 'model-quota' || m.id === 'remaining' || (settings.metrics?.['model-quota'] || []).includes(m.id);
    const resetHint = sourceId === 'official' && (m.id === 'primaryRemaining' || m.id === 'secondaryRemaining')
      ? formatResetShort(m.resetsAt)
      : null;
    out.push({
      id: m.id,
      key: `${sourceId}:${m.id}`,
      label: normalizedLabel,
      value,
      resetHint,
      minimalText,
      normalText,
      detail: String(m.detail || `${normalizedLabel}：${value}`).slice(0, 360),
      selectable,
    });
  }
  return out;
}

async function handleCommands() {
  const cmdPath = path.join(opts.stateDir, 'command.json');
  let cmd = null;
  try {
    const raw = await fs.readFile(cmdPath, 'utf8');
    cmd = JSON.parse(raw.replace(/^\uFEFF/, ''));
  } catch {
    return;
  }
  try { await fs.unlink(cmdPath); } catch {}
  if (!cmd || typeof cmd !== 'object') return;
  const seq = Number(cmd.seq || 0);
  if (seq && seq === lastHandledCmdSeq) return;
  if (seq) lastHandledCmdSeq = seq;

  if (cmd.type === 'refreshQuota') {
    const model = anchor && Date.now() - Number(anchor.at || 0) < 15000 ? anchor.model : null;
    if (model) quota.refresh(model, true).catch(() => {});
    usageClient.official?.refresh?.().catch(() => {});
    dirty = true;
    return;
  }

  if (cmd.type === 'toggleMetric' && typeof cmd.sourceId === 'string' && typeof cmd.metricId === 'string') {
    const next = structuredClone(settings);
    next.metrics ||= {};
    const existing = Object.prototype.hasOwnProperty.call(next.metrics, cmd.sourceId)
      ? [...(next.metrics[cmd.sourceId] || [])]
      : [...(DEFAULT_SELECTIONS[cmd.sourceId] || [])];
    const key = `${cmd.sourceId}:${cmd.metricId}`;
    const idx = existing.indexOf(cmd.metricId);
    if (idx >= 0) {
      existing.splice(idx, 1);
      next.metricOrder = (next.metricOrder || []).filter((k) => k !== key);
    } else {
      existing.push(cmd.metricId);
      next.metricOrder = [...(next.metricOrder || []).filter((k) => k !== key), key];
    }
    next.metrics[cmd.sourceId] = existing;
    settings = await settingsStore.save(next);
    dirty = true;
    return;
  }

  if (cmd.type === 'setOption' && typeof cmd.key === 'string') {
    const allowed = ['minimalMode', 'refreshEvery30Seconds', 'showResetForecast', 'showApiColumns'];
    if (!allowed.includes(cmd.key)) return;
    const next = structuredClone(settings);
    next[cmd.key] = Boolean(cmd.value);
    settings = await settingsStore.save(next);
    if (cmd.key === 'refreshEvery30Seconds') {
      usageClient.setRefreshInterval(settings.refreshEvery30Seconds ? 30000 : 60000);
    }
    dirty = true;
  }
}

async function writeAtomicJson(targetPath, payload) {
  const temp = `${targetPath}.${process.pid}.tmp`;
  await fs.writeFile(temp, JSON.stringify(payload), 'utf8');
  await fs.rename(temp, targetPath);
}

async function publish() {
  const now = Date.now();
  const threadId = effectiveThreadId();
  let usage = baseUsage
    ? usageClient.usageForThread(baseUsage, threadId, now)
    : { sources: { session: { id: 'session', label: '本会话', status: 'loading', metrics: [] }, official: { id: 'official', label: '官方订阅', status: 'loading', metrics: [] } }, nextRefreshAt: null };
  usage = withLiveRate(usage, rate.get(threadId), threadId, now);

  const activeModel = anchor && now - Number(anchor.at || 0) < 15000 ? anchor.model : null;
  const modelQuotaSource = quota.get(activeModel);
  usage.sources['model-quota'] = modelQuotaSource;

  const sessionRows = normalizeSourceMetrics('session', usage.sources.session, now);
  if (!sessionRows.some((r) => r.id === 'outputSpeed')) {
    sessionRows.unshift({
      id: 'outputSpeed',
      key: 'session:outputSpeed',
      label: '输出速率',
      value: '0.0 tok/s',
      minimalText: '0.0 tok/s',
      normalText: '0.0 tok/s',
      detail: '当前没有模型输出，或正在等待首次采样。',
      selectable: true,
    });
  }
  const officialRows = normalizeSourceMetrics('official', usage.sources.official, now);
  const forecastRows = normalizeSourceMetrics('reset-forecast', usage.sources['reset-forecast'], now);
  const quotaRows = normalizeSourceMetrics('model-quota', modelQuotaSource, now);

  const quotaBucketKey = activeModel
    ? (activeModel.startsWith('gemini-4-argon-') ? 'ag:argon'
      : activeModel.startsWith('gemini-') ? 'ag:gemini'
      : /^(?:claude-|gpt-oss-)/.test(activeModel) ? 'ag:third-party'
      : activeModel.startsWith('deepseek-') ? 'wb:cn'
      : activeModel.startsWith('gpt-') ? 'paw' : null)
    : null;

  let effectiveOfficialRows = officialRows;
  if (usage.sources.official?.status === 'ready' && officialRows.length) {
    warmCache.officialRows = officialRows;
    warmCache.at = now;
  } else if (usage.sources.official?.status === 'loading' && warmCache.officialRows?.length) {
    effectiveOfficialRows = warmCache.officialRows.map((r) => OFFICIAL_LABEL_FIX[r.id] ? { ...r, label: OFFICIAL_LABEL_FIX[r.id] } : r);
  }

  let effectiveQuotaRows = quotaRows;
  if (quotaBucketKey && modelQuotaSource.status === 'ready' && quotaRows.length) {
    warmCache.quotaByKey ||= {};
    warmCache.quotaByKey[quotaBucketKey] = quotaRows;
    warmCache.at = now;
  } else if (quotaBucketKey && modelQuotaSource.status === 'loading' && warmCache.quotaByKey?.[quotaBucketKey]?.length) {
    effectiveQuotaRows = warmCache.quotaByKey[quotaBucketKey];
  }

  const forecastSrc = usage.sources['reset-forecast'];
  const resetMethodVal = forecastSrc?.resetMethod === 'banked' ? '发放重置卡'
    : forecastSrc?.resetMethod === 'forced' ? '直接重置' : '暂未明确';
  const rawAct = forecastSrc?.latestActivity;
  const latestActivity = rawAct && rawAct.text && rawAct.sourceUrl ? {
    text: String(rawAct.text).replace(/\s+/g, ' ').trim().slice(0, 220),
    url: /^https:\/\/(?:www\.)?x\.com\/thsottiaux\/status\/\d+$/i.test(String(rawAct.sourceUrl)) ? String(rawAct.sourceUrl) : null,
    publishedAt: formatActivityTimestamp(rawAct.createdAt),
  } : null;

  const sourceDefs = [
    { id: 'session', title: '本会话', status: usage.sources.session?.status || 'ready', error: usage.sources.session?.error || null, rows: sessionRows, visible: true, canRefresh: false },
    { id: 'official', title: '官方订阅', status: usage.sources.official?.status || 'loading', error: usage.sources.official?.error || null, rows: effectiveOfficialRows, visible: true, canRefresh: false },
    { id: 'reset-forecast', title: '重置概率预测（仅供参考）', status: forecastSrc?.status || 'unavailable', error: forecastSrc?.error || null, rows: forecastRows, resetMethodText: `预告方式：${resetMethodVal}`, latestActivity, visible: Boolean(settings.showResetForecast) && forecastRows.length > 0, canRefresh: false },
    { id: 'model-quota', title: modelQuotaSource.label || '个人额度', status: modelQuotaSource.status || 'loading', error: modelQuotaSource.error || null, rows: effectiveQuotaRows, visible: settings.showApiColumns !== false, canRefresh: true },
  ];

  const selectedMap = new Map();
  for (const col of sourceDefs) {
    const avail = new Set(col.rows.map((r) => r.id));
    const chosenIds = new Set(selectedIdsForSource(col.id, avail));
    for (const row of col.rows) {
      row.checked = chosenIds.has(row.id);
      if (row.checked) selectedMap.set(row.key, row);
    }
  }

  const orderedSelected = [];
  for (const key of settings.metricOrder || []) {
    if (selectedMap.has(key)) {
      orderedSelected.push(selectedMap.get(key));
      selectedMap.delete(key);
    }
  }
  for (const row of selectedMap.values()) orderedSelected.push(row);

  const stripMetrics = orderedSelected.map((r) => ({
    key: r.key,
    label: r.label,
    value: settings.minimalMode ? r.minimalText : r.normalText,
    rawValue: r.value,
    detail: r.detail,
  }));

  const payload = {
    at: now,
    pid: process.pid,
    threadId,
    model: activeModel,
    route: routeStatus,
    quotaStatus: modelQuotaSource.status,
    officialStatus: usage.sources.official?.status || 'loading',
    nextRefreshAt: usage.nextRefreshAt || null,
    settings: {
      minimalMode: Boolean(settings.minimalMode),
      refreshEvery30Seconds: Boolean(settings.refreshEvery30Seconds),
      showResetForecast: Boolean(settings.showResetForecast),
      showApiColumns: settings.showApiColumns !== false,
    },
    metrics: stripMetrics,
    columns: sourceDefs.filter((c) => c.visible),
    details: sourceDefs.flatMap((c) => c.rows),
  };
  await writeAtomicJson(path.join(opts.stateDir, 'metrics.json'), payload);
  if (warmCache.at === now) {
    writeAtomicJson(warmCachePath, warmCache).catch(() => {});
  }
}

route.start();
rate.start();
rate.startWatching(() => {
  const id = effectiveThreadId();
  return id ? [id] : [];
}, () => { dirty = true; });
usageClient.start().catch(() => {});

let lastPublish = 0;
let publishing = false;
let lastParentCheck = 0;
let lastAnchorModel = null;

const tick = setInterval(async () => {
  if (stopped) return;
  const now = Date.now();
  if (opts.parentPid > 0 && now - lastParentCheck >= 2500) {
    lastParentCheck = now;
    if (!isPidAlive(opts.parentPid)) { await stop(); return; }
  }
  try { await fs.access(path.join(opts.stateDir, 'stop')); await stop(); return; } catch {}
  await handleCommands();
  try {
    anchor = JSON.parse((await fs.readFile(path.join(opts.stateDir, 'anchor.json'), 'utf8')).replace(/^\uFEFF/, ''));
  } catch {
    anchor = null;
  }
  const freshAnchor = anchor && now - Number(anchor.at || 0) < 10000 && anchor.located;
  if (freshAnchor && anchor.model) {
    if (anchor.model !== lastAnchorModel) {
      lastAnchorModel = anchor.model;
      dirty = true;
    }
    quota.refresh(anchor.model).catch(() => {});
  }
  if (!publishing && (dirty || now - lastPublish >= 1000)) {
    dirty = false;
    lastPublish = now;
    publishing = true;
    try { await publish(); } catch {} finally { publishing = false; }
  }
}, 150);

async function stop() {
  if (stopped) return;
  stopped = true;
  clearInterval(tick);
  route.stop();
  quota.stop();
  await Promise.allSettled([rate.stop(), usageClient.stop(), settingsStore.flush()]);
  process.exit(0);
}

process.on('SIGINT', stop);
process.on('SIGTERM', stop);
if (opts.maxSeconds > 0) setTimeout(stop, opts.maxSeconds * 1000).unref();
console.log(JSON.stringify({ ready: true, pid: process.pid, mode: 'native-companion-no-cdp', stateDir: opts.stateDir, codexCli }));
