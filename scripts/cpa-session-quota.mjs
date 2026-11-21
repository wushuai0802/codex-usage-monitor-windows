import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { validQuotaEndpoint } from './model-quota.mjs';

const SESSION_ID = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const READER_CONFIG = new URL('../config/cpa-session-quota.local.json', import.meta.url);
const PERIODS = [
  { minutes: 300, id: 'primaryRemaining', label: '5小时剩余' },
  { minutes: 10080, id: 'secondaryRemaining', label: '7天剩余' },
];
const UNAVAILABLE = {
  'no-match': 'CPAM 未采集到本会话近期可归属的成功请求。',
  ambiguous: '同一请求有多个成功归属，暂时无法确认账号。',
  unsupported: '最近成功的请求走了一条尚未接入额度的线路。',
  unavailable: '暂时无法读取 CPA 额度。',
  'invalid-config': 'CPA 额度读取器配置无效。',
};

function readOverSsh(config, sessionId, signal) {
  return new Promise((resolve, reject) => {
    const ssh = process.env.CODEX_USAGE_SSH_PATH
      || path.join(process.env.SystemRoot || 'C:/Windows', 'System32/OpenSSH/ssh.exe');
    const remoteCommand = 'sudo -n python3 -B ' + config.remoteReader;
    const child = spawn(ssh, ['-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
      '-o', 'ConnectTimeout=5', config.sshHost, remoteCommand],
    { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], signal });
    let output = '';
    const timer = setTimeout(() => child.kill(), 14000);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => {
      if (output.length <= 4096) output += chunk;
      if (output.length > 4096) child.kill();
    });
    child.stderr.resume();
    child.stdin.on('error', () => {});
    child.on('error', (error) => { clearTimeout(timer); reject(error); });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code !== 0 || output.length > 4096) return reject(new Error('CPA quota read failed'));
      try { resolve(JSON.parse(output)); } catch (error) { reject(error); }
    });
    child.stdin.end(JSON.stringify({ sessionId }));
  });
}

export class CpaSessionQuotaClient {
  constructor({ runner = readOverSsh, onUpdate = () => {}, clock = Date.now } = {}) {
    this.runner = runner;
    this.onUpdate = onUpdate;
    this.clock = clock;
    this.enabled = false;
    this.config = null;
    this.current = null;
  }

  async initialize(configFile = READER_CONFIG) {
    try {
      const config = JSON.parse((await fs.readFile(configFile, 'utf8')).replace(/^\uFEFF/, ''));
      this.enabled = config?.enabled !== false;
      this.config = validQuotaEndpoint(config) ? config : null;
    } catch (error) {
      // An unreadable opt-in file must not silently expose a different account's quota.
      this.enabled = error.code !== 'ENOENT';
    }
  }

  refresh(sessionId, intervalMs, force = false) {
    if (!this.config) return;
    const id = typeof sessionId === 'string' && SESSION_ID.test(sessionId) ? sessionId.toLowerCase() : null;
    if (id !== this.current?.id) {
      this.stop();
      this.current = id ? { id, attemptAt: -Infinity, reply: null, readAt: null } : null;
    }
    const current = this.current;
    const now = this.clock();
    if (!current || current.pending || now - current.attemptAt < (force ? 3000 : intervalMs)) return;
    current.attemptAt = now;
    const controller = new AbortController();
    current.pending = controller;
    const job = Promise.resolve().then(() => this.runner(this.config, id, controller.signal))
      .then((reply) => {
        if (this.current !== current) return;
        if (reply?.version !== 1 || !(reply.status === 'matched' || Object.hasOwn(UNAVAILABLE, reply.status))) {
          throw new Error('CPA quota response invalid');
        }
        if (reply.status === 'matched' && (!['codex', 'paw'].includes(reply.provider)
            || !/^[0-9A-F]{8}$/.test(reply.accountAlias || '')
            || !Number.isSafeInteger(reply.matchedAt) || reply.matchedAt <= 0
            || !Number.isSafeInteger(reply.observedAt) || reply.observedAt < reply.matchedAt
            || reply.observedAt > this.clock() + 120000 || !Array.isArray(reply.windows))) {
          throw new Error('CPA quota match invalid');
        }
        current.reply = reply;
        current.readAt = this.clock();
      })
      .catch(() => {
        if (this.current === current) current.reply = { status: 'unavailable' };
      })
      .finally(() => {
        if (this.current !== current) return;
        current.pending = null;
        this.onUpdate();
      });
    return job;
  }

  source(sessionId, now = this.clock()) {
    const current = typeof sessionId === 'string' && sessionId.toLowerCase() === this.current?.id
      ? this.current : null;
    const reply = current?.reply;
    const matched = reply?.status === 'matched';
    const paw = matched && reply.provider === 'paw';
    const error = matched ? null : !this.config ? UNAVAILABLE['invalid-config']
      : !sessionId ? '当前窗口的 Codex 会话尚未确认。' : UNAVAILABLE[reply?.status] || null;
    const time = (ms) => new Date(ms).toLocaleString('zh-CN', { hour12: false });
    const metrics = PERIODS.map(({ minutes, id, label }) => {
      const window = matched ? reply.windows.find((item) => item?.minutes === minutes
        && (paw ? minutes === 10080 && Number.isFinite(item.remainingUsd) && item.remainingUsd >= 0
          : Number.isFinite(item.remaining) && item.remaining >= 0 && item.remaining <= 100)
        && Number.isSafeInteger(item.resetsAt) && item.resetsAt > now) : null;
      const value = !window ? '--' : paw
        ? new Intl.NumberFormat('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(window.remainingUsd) + ' USD'
        : Math.round(window.remaining) + '%';
      const explanation = paw ? 'Paw 订阅凭证额度。' : '本会话实际使用的 CPA 账号。';
      return {
        id, label, value,
        resetsAt: window ? window.resetsAt / 1000 : null,
        detail: window ? explanation + '额度采样于 ' + time(reply.observedAt) + '。'
          : (paw && minutes === 300 ? 'Paw 周订阅不提供 5 小时额度。'
            : error || (matched ? '当前额度窗口缺失或已过重置时间。' : '正在等待 CPAM 的请求记录。')),
      };
    });
    if (matched) metrics.push({
      id: 'cpaAccount', label: paw ? '最近命中凭证' : '最近命中账号', value: reply.accountAlias, selectable: false,
      detail: '最近已归属的成功请求 ' + time(reply.matchedAt) + '。',
    });
    const hasQuota = metrics.some((metric) => metric.resetsAt);
    return { label: paw ? 'CPA 实际账号 · Paw' : 'CPA 实际账号', metrics,
      status: hasQuota ? 'ready' : error || matched ? 'unavailable' : 'loading',
      error: error || (matched && !hasQuota ? '当前账号暂无可核实的额度窗口。' : null),
      sourceAt: hasQuota ? reply.observedAt : null,
      readAt: hasQuota ? current.readAt : null,
    };
  }

  stop() {
    const old = this.current;
    this.current = null;
    old?.pending?.abort();
  }
}
