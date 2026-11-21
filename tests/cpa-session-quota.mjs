import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { CpaSessionQuotaClient, pawModelQuota } from '../scripts/cpa-session-quota.mjs';

const A = '00000000-0000-4000-8000-000000000001';
const B = '00000000-0000-4000-8000-000000000002';
let now = 1_800_000_000_000;
const pending = new Map();
const clock = () => now;
const client = new CpaSessionQuotaClient({ clock, runner: (_, id, signal) => new Promise((resolve, reject) => {
  pending.set(id, { resolve, reject, signal });
}) });
const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'cpa-quota-test-'));
const config = path.join(temp, 'test.json');

const metrics = (id) => client.source(id).metrics.map(({ value }) => value);
const reply = (accountAlias, windows = []) => ({
  version: 1, status: 'matched', provider: 'codex', accountAlias, matchedAt: now - 5000, observedAt: now - 3000, windows,
});

try {
  await client.initialize(config);
  assert.equal(client.enabled, false, 'No opt-in preserves official subscription.');
  await fs.writeFile(config, '{');
  await client.initialize(config);
  assert.equal(client.enabled, true, 'Unreadable opt-in does not fall back to the desktop account.');
  assert.deepEqual(metrics(A), ['--', '--']);
  await fs.writeFile(config, JSON.stringify({ enabled: true, sshHost: 'quota-host', remoteReader: '/opt/quota/reader.py' }));
  await client.initialize(config);
  assert.equal(client.refresh('latest', 60000), undefined);

  const first = client.refresh(A, 60000);
  await Promise.resolve();
  pending.get(A).resolve(reply('AAAAAAAA', [
    { minutes: 300, remaining: 0, resetsAt: now + 2000 },
    { minutes: 10080, remaining: 76, resetsAt: now + 3600000 },
  ]));
  await first;
  assert.equal(client.source(A).status, 'ready');
  assert.equal(pawModelQuota(client.source(A)), null, 'Direct Codex keeps its 5h and 7d metrics.');
  assert.deepEqual(metrics(A), ['0%', '76%', 'AAAAAAAA']);
  assert.equal(client.source(A).metrics.at(-1).selectable, false);
  assert.equal(client.refresh(A, 60000), undefined);
  now += 3000;
  assert.deepEqual(metrics(A), ['--', '76%', 'AAAAAAAA'], 'Expired windows stay blank.');

  const old = client.refresh(A, 60000, true);
  await Promise.resolve();
  const next = client.refresh(B, 60000);
  await Promise.resolve();
  assert.equal(pending.get(A).signal.aborted, true);
  assert.deepEqual(metrics(A), ['--', '--']);
  assert.deepEqual(metrics(B), ['--', '--']);
  pending.get(A).resolve(reply('AAAAAAAA'));
  await old;
  assert.deepEqual(metrics(B), ['--', '--'], 'Late results cannot populate the newly selected session.');
  pending.get(B).resolve(reply('BBBBBBBB', [{ minutes: 10080, remaining: 88, resetsAt: now + 3600000 }]));
  await next;
  assert.deepEqual(metrics(B), ['--', '88%', 'BBBBBBBB']);

  now += 3000;
  const paw = client.refresh(B, 60000, true);
  await Promise.resolve();
  pending.get(B).resolve({ ...reply('CCCCCCCC', [{ minutes: 10080, remainingUsd: 25.5, resetsAt: now + 3600000 }]), provider: 'paw' });
  await paw;
  assert.equal(client.source(B).label, 'CPA 实际账号 · Paw');
  assert.deepEqual(metrics(B), ['--', '25.50 USD', 'CCCCCCCC']);
  const sharedPawColumn = pawModelQuota(client.source(B));
  assert.equal(sharedPawColumn.label, '个人额度 · Paw');
  assert.equal(sharedPawColumn.metrics[0].label, 'Paw剩余');
  assert.equal(sharedPawColumn.metrics[0].resetsAt, (now + 3600000) / 1000);
  assert.deepEqual(sharedPawColumn.metrics.map(({ id, value }) => [id, value]), [
    ['remaining', '25.50 USD'], ['cpaAccount', 'CCCCCCCC'],
  ], 'The actual Paw reading fits the existing personal quota metric and omits fixed-window placeholders.');
  assert.equal(pawModelQuota(client.source(A)), null, 'The merged Paw reading is scoped to the matching session.');

  now += 3000;
  const failure = client.refresh(B, 60000, true);
  await Promise.resolve();
  pending.get(B).reject(new Error('fixture'));
  await failure;
  assert.equal(client.source(B).status, 'unavailable');
  assert.equal(pawModelQuota(client.source(B)), null, 'A failed lookup cannot claim a Paw route.');
  assert.deepEqual(metrics(B), ['--', '--']);

  client.refresh(null, 60000);
  assert.deepEqual(metrics(B), ['--', '--']);
} finally {
  client.stop();
  await fs.rm(temp, { recursive: true, force: true });
}
console.log('PASS: CPA session quota isolation, refresh, and expiry.');
