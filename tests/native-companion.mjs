import assert from 'node:assert/strict';
import { RouteState } from '../native-overlay/ipc-route.mjs';

const a = '00000000-0000-0000-0000-000000000001';
const b = '00000000-0000-0000-0000-000000000002';
const route = new RouteState();
route.connected = true;

const follow = (client, thread, following = true, host = 'local') => route.update({
  type: 'broadcast',
  method: 'thread-stream-following-changed',
  sourceClientId: client,
  params: { conversationId: thread, hostId: host, following },
});

follow('one', a);
assert.equal(route.threadId, a);

follow('one', b);
follow('one', a, false);
assert.equal(route.threadId, b, 'Stale unfollow must not clear a newly selected task.');

follow('one', b, false);
assert.equal(route.threadId, b, 'Transient unfollow gap retains last known task while connected.');

follow('one', b, true);
follow('two', a, true);
assert.equal(route.threadId, a, 'Most recently focused local window sequence wins.');

route.update({
  type: 'broadcast',
  method: 'client-status-changed',
  params: { clientId: 'two', status: 'disconnected' },
});
assert.equal(route.threadId, b, 'Disconnecting one window falls back to the remaining active window.');

follow('remote', a, true, 'ssh');
assert.equal(route.threadId, b, 'Remote host broadcasts must not override local session tracking.');

route.reset();
assert.equal(route.threadId, null);
console.log('PASS: native companion IPC route tracking.');
