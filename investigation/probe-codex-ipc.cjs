// Read-only feasibility probe for the installed Codex extension's private IPC.
// Does not start tasks, change read status, or persist conversation contents.
// --follow registers a temporary state subscription, removed when it exits.
const net = require('node:net');
const crypto = require('node:crypto');
const durationMs = Number(process.argv[2] || 15000);
const follow = process.argv.includes('--follow');
const socket = net.connect('\\\\.\\pipe\\codex-ipc');
let buffered = Buffer.alloc(0);
let clientId;
const counts = {};
const discovered = new Map();
const subscribe = (params) => {
  const key = `${params.hostId}:${params.conversationId}`;
  if (discovered.has(key)) return;
  discovered.set(key, params);
  if (clientId && follow) send({ type: 'broadcast',
    method: 'thread-stream-following-changed', version: 1, sourceClientId: clientId,
    params: { hostId: params.hostId, conversationId: params.conversationId, following: true } });
};
const send = (message) => {
  const payload = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length);
  socket.write(Buffer.concat([header, payload]));
};
socket.on('connect', () => {
  console.log(JSON.stringify({ event: 'connected' }));
  send({ type: 'request', method: 'initialize', requestId: crypto.randomUUID(),
    sourceClientId: 'initializing-client', version: 0,
    params: { clientType: 'stoplight-feasibility-observer' } });
});
socket.on('data', (chunk) => {
  buffered = Buffer.concat([buffered, chunk]);
  while (buffered.length >= 4) {
    const length = buffered.readUInt32LE();
    if (length > 256 * 1024 * 1024) { socket.destroy(new Error('Invalid frame')); return; }
    if (buffered.length < length + 4) return;
    const message = JSON.parse(buffered.subarray(4, length + 4));
    buffered = buffered.subarray(length + 4);
    if (message.type === 'client-discovery-request') {
      send({ type: 'client-discovery-response', requestId: message.requestId,
        response: { canHandle: false } });
      continue;
    }
    if (message.method === 'initialize' && message.resultType === 'success') {
      clientId = message.result.clientId;
      console.log(JSON.stringify({ event: 'initialized', clientId }));
      if (follow) for (const params of discovered.values()) send({ type: 'broadcast',
        method: 'thread-stream-following-changed', version: 1, sourceClientId: clientId,
        params: { hostId: params.hostId, conversationId: params.conversationId, following: true } });
      continue;
    }
    const method = message.method || message.type;
    counts[method] = (counts[method] || 0) + 1;
    const params = message.params || {};
    if (message.method === 'thread-stream-following-changed' && params.following) subscribe(params);
    const state = params.change?.conversationState;
    const turns = state ? [...(state.turns || []),
      ...Object.values(state.turnHistory?.history?.entitiesByKey || {})] : [];
    console.log(JSON.stringify({ event: 'message', type: message.type, method,
      version: message.version, parameterKeys: Object.keys(params),
      changeType: params.change?.type,
      changeKeys: params.change ? Object.keys(params.change) : undefined,
      stateKeys: state ? Object.keys(state) : undefined,
      state: state ? { hasUnreadTurn: state.hasUnreadTurn,
        threadRuntimeStatus: state.threadRuntimeStatus, resumeState: state.resumeState,
        turnStatuses: turns.map(t => t.status),
        terminalErrorItems: turns.flatMap(t => t.items || [])
          .filter(item => item.type === 'error' && item.willRetry === false).length,
        canonicalHistoryKeys: state.turnHistory ? Object.keys(state.turnHistory) : undefined } : undefined,
      patches: params.change?.patches?.map(p => ({ op: p.op, path: p.path,
        value: ['hasUnreadTurn', 'threadRuntimeStatus', 'status', 'resumeState'].includes(p.path?.at(-1)) ? p.value : undefined })),
      hasUnreadTurn: method === 'thread-read-state-changed' ? params.hasUnreadTurn : undefined,
      readKeys: method === 'thread-read-state-changed' ? Object.keys(params) : undefined }));
  }
});
socket.on('error', (error) => {
  console.log(JSON.stringify({ event: 'error', code: error.code, message: error.message }));
  process.exitCode = 1;
});
const timer = setTimeout(() => {
  console.log(JSON.stringify({ event: 'summary', initialized: !!clientId, durationMs, follow, discoveredThreads: discovered.size, counts }));
  if (follow && clientId) for (const params of discovered.values()) send({ type: 'broadcast',
    method: 'thread-stream-following-changed', version: 1, sourceClientId: clientId,
    params: { hostId: params.hostId, conversationId: params.conversationId, following: false } });
  socket.end();
}, durationMs);
socket.on('close', () => clearTimeout(timer));
