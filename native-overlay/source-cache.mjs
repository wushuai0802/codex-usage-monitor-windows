const MAX_AGE = 15 * 60 * 1000;
export function timestamp(value) {
  const number = typeof value === 'number' ? value : typeof value === 'string' ? Date.parse(value) : NaN;
  return Number.isFinite(number) && number > 0 ? number : null;
}
export function quotaCacheKey(model) {
  if (typeof model !== 'string') return null;
  // Eligibility can differ even inside an AG group: do not reuse another model's rows.
  if (model.startsWith('gemini-4-argon-')) return `ag:argon:${model}`;
  if (model.startsWith('gemini-')) return `ag:gemini:${model}`;
  if (/^(claude-|gpt-oss-)/.test(model)) return `ag:third-party:${model}`;
  if (model.startsWith('deepseek-')) return 'wb:cn';
  return model.startsWith('gpt-') ? 'paw' : null;
}
export function retentionOrder(keys, configured = []) {
  return [...new Set([...configured, 'session:outputSpeed', 'model-quota:remaining', ...keys])].filter(key => keys.includes(key));
}
export function moveRetention(keys, configured, key, direction) {
  const order = retentionOrder(keys, configured);
  const index = order.indexOf(key), target = index + direction;
  if (![-1, 1].includes(direction) || index < 0 || target < 0 || target >= order.length) return null;
  [order[index], order[target]] = [order[target], order[index]];
  return [...order, ...configured.filter(item => !order.includes(item))];
}
export class SourceCache {
  constructor(saved, now = Date.now()) {
    this.entries = {};
    // Legacy global timestamps cannot establish the age of individual sources.
    if (saved?.schemaVersion === 2 && saved.entries && typeof saved.entries === 'object') {
      for (const [key, entry] of Object.entries(saved.entries).slice(0, 64)) {
        if (this.valid(entry, now)) this.entries[key] = structuredClone(entry);
      }
    }
    this.dirty = false;
  }
  valid(entry, now) {
    return entry && timestamp(entry.readAt) && timestamp(entry.observedAt)
      && entry.readAt <= now + 120000 && entry.observedAt <= now + 120000
      && (entry.sourceAt === null || (timestamp(entry.sourceAt) && entry.sourceAt <= now + 120000))
      && now - entry.readAt < MAX_AGE && Array.isArray(entry.rows) && entry.rows.length <= 100;
  }
  resolve(key, source, rows, now) {
    const observedAt = timestamp(source?.fetchedAt);
    const sourceAt = timestamp(source && Object.hasOwn(source, 'dataAt') ? source.dataAt : source?.fetchedAt);
    if (key && source?.status === 'ready' && rows.length && observedAt && observedAt <= now + 120000) {
      const previous = this.entries[key];
      if (!previous || observedAt > previous.observedAt) {
        this.entries[key] = { sourceAt, observedAt, readAt: now, rows: structuredClone(rows) };
        // Bounded cache; updating another source never touches an existing entry's age.
        const oldest = Object.keys(this.entries).sort((a, b) => this.entries[a].readAt - this.entries[b].readAt);
        for (const old of oldest.slice(0, Math.max(0, oldest.length - 64))) delete this.entries[old];
        this.dirty = true;
      }
    }
    const entry = key ? this.entries[key] : null;
    if (source?.status === 'loading' && this.valid(entry, now)) {
      const sourceTime = entry.sourceAt ? new Date(entry.sourceAt).toLocaleString('zh-CN', { hour12: false }) : '未知';
      const readTime = new Date(entry.readAt).toLocaleString('zh-CN', { hour12: false });
      const cachedRows = structuredClone(entry.rows).map(row => ({ ...row,
        detail: `${row.detail || ''}；缓存读数，等待本次读取。数据时间 ${sourceTime}，读取时间 ${readTime}。` }));
      return { rows: cachedRows, status: 'stale', cached: true, sourceAt: entry.sourceAt, readAt: entry.readAt };
    }
    return { rows, status: source?.status || 'loading', cached: false, sourceAt,
      readAt: entry?.observedAt === observedAt ? entry.readAt : null };
  }
  serialize() { return { schemaVersion: 2, entries: this.entries }; }
}
