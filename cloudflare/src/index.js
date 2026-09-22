/**
 * MidiKeyPlayer 远程同演中转站（Cloudflare Worker + Durable Object）
 *
 * 它只做一件事：把房间里的消息转给同房间的其他人。不存曲子、不存按键、不参与计时。
 *
 * 一个房间 = 一个 Durable Object。房间键由客户端按房间名算好之后放进 URL，
 * 服务器上不出现明文房间名；密码只存 PBKDF2 派生值，不存明文。
 *
 * 用量刻意压到最低：
 *   - 用 WebSocket 休眠 API（ctx.acceptWebSocket）。房间闲着就休眠，不计时长。
 *   - 没有心跳、没有轮询、没有周期上报。只有人真的操作时才发消息。
 *   - 名单只在有人进出或改声部时广播；开演/暂停/停止各一条。
 *
 * 部署：见同目录的 部署说明.md。
 */

const PROTOCOL_VERSION = 1;
const PBKDF2_ITERATIONS = 100_000;
const DEFAULT_DELAY_MS = 3000;
const MIN_DELAY_MS = 500;
const MAX_DELAY_MS = 30_000;
const MAX_NAME = 24;
const MAX_VOICE = 64;
const MAX_PEERS = 32;
const MAX_MESSAGE = 4096;
/** 10 秒内超过这么多条就断开：正常操作一秒最多几条，超过就是在刷。 */
const RATE_LIMIT_PER_10S = 60;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/health') {
      return new Response('ok', { headers: { 'content-type': 'text/plain' } });
    }
    if (url.pathname !== '/room') {
      return new Response('MidiKeyPlayer sync relay', { status: 200 });
    }
    if (request.headers.get('Upgrade') !== 'websocket') {
      return new Response('需要 WebSocket 升级请求', { status: 426 });
    }

    const key = url.searchParams.get('k') || '';
    if (!/^[0-9a-f]{32,64}$/.test(key)) {
      return new Response('房间键不合法', { status: 400 });
    }

    const stub = env.ROOMS.get(env.ROOMS.idFromName(key));
    return stub.fetch(request);
  },
};

export class Room {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    /**
     * 成员号 → 成员信息。
     * 只放内存：休眠换出后会重新构造，所以名单同时写一份进 storage，
     * 重新醒来时能接着用（见 loadPeers）。
     */
    this.peers = null;
  }

  async fetch(request) {
    const roomKey = new URL(request.url).searchParams.get('k') || '';
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    // 休眠 API：房间空闲时对象可以被换出内存，不计计算时长。
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({ peerId: null, sent: 0, windowStart: Date.now() });

    // 房间键要留给 handleJoin 当密码的盐。一个 DO 只有一个房间，所以存在实例上就够。
    this.roomKey = roomKey;

    return new Response(null, { status: 101, webSocket: client });
  }

  // ================= 名单存取 =================

  async loadPeers() {
    if (this.peers !== null) return this.peers;
    const stored = await this.ctx.storage.get('peers');
    this.peers = new Map();
    if (Array.isArray(stored)) {
      for (const p of stored) this.peers.set(p.id, p);
    }
    return this.peers;
  }

  savePeers() {
    // 名单不大（上限 32 人），整份写回最省事。写一次算一次「行写入」。
    return this.ctx.storage.put('peers', [...this.peers.values()]);
  }

  // ================= 消息处理 =================

  async webSocketMessage(ws, raw) {
    let msg;
    try {
      const text = typeof raw === 'string' ? raw : new TextDecoder().decode(raw);
      if (text.length > MAX_MESSAGE) return this.reject(ws, '消息太长');
      msg = JSON.parse(text);
    } catch {
      return this.reject(ws, '消息不是合法的 JSON');
    }

    const att = ws.deserializeAttachment();
    if (!this.allow(ws, att)) return;

    if (att.peerId === null) {
      if (msg.t !== 'join') return this.reject(ws, '第一条消息必须是 join');
      return this.handleJoin(ws, msg, this.roomKey || '');
    }

    await this.loadPeers();

    switch (msg.t) {
      case 'leave':
        await this.dropPeer(ws, true);
        try {
          ws.close(1000, 'bye');
        } catch {
          // 已经断了
        }
        return;

      case 'ready': {
        const peer = this.peers.get(att.peerId);
        if (!peer) return;
        peer.ready = msg.on === true;
        await this.savePeers();
        await this.broadcastRoster();
        return;
      }

      case 'voice': {
        const peer = this.peers.get(att.peerId);
        if (!peer) return;
        peer.voice = clean(msg.voice, MAX_VOICE);
        await this.savePeers();
        await this.broadcastRoster();
        return;
      }

      case 'xpose': {
        const peer = this.peers.get(att.peerId);
        // 房主的统一移调。null 表示取消统一，此时按 0 转发（0 就是"没有统一"）。
        const unified = msg.xpose === null ? 0 : clampSemitones(msg.xpose);
        if (peer) {
          peer.xpose = unified;
          await this.savePeers();
        }
        await this.broadcast({ t: 'xpose', from: att.peerId, xpose: unified }, ws);
        return;
      }

      case 'start':
        await this.broadcast({
          t: 'start',
          from: att.peerId,
          delayMs: clampDelay(msg.delayMs),
          positionSec: Number.isFinite(msg.positionSec) ? Math.max(0, msg.positionSec) : 0,
          // 房主的单调时刻原样转发：收到方靠它算这条消息在路上花了多久
          sentAt: Number.isFinite(msg.sentAt) ? msg.sentAt : null,
        }, ws);
        return;

      case 'pause':
      case 'stop':
        await this.broadcast({ t: msg.t, from: att.peerId }, ws);
        return;

      default:
        return; // 不认识的类型直接忽略：不报错、不回消息、省流量
    }
  }

  async webSocketClose(ws) {
    await this.dropPeer(ws, true);
  }

  async webSocketError(ws) {
    await this.dropPeer(ws, true);
  }

  // ================= 加入房间 =================

  async handleJoin(ws, msg, roomKey) {
    const peerId = clean(msg.id, 40);
    if (!peerId) return this.reject(ws, '成员号是空的');

    const password = typeof msg.pass === 'string' ? msg.pass : '';
    if (password.length > 128) return this.reject(ws, '密码太长');

    // 盐用房间键：它每个房间都不同，而且不含明文房间名。
    // **不能用成员号当盐**：成员号是每人本机随机的，同一个人重装程序换了成员号就算不出一致的值，
    // 会被当成"密码不对"。房间键与密码一起才决定这个值。
    const verifier = await pbkdf2Hex(password, roomKey);
    const stored = await this.ctx.storage.get('verifier');

    if (stored === undefined) {
      // 第一个进来的人定义这个房间的密码。
      await this.ctx.storage.put('verifier', verifier);
    } else if (stored !== verifier) {
      return this.reject(ws, '密码不对');
    }

    const peers = await this.loadPeers();
    if (!peers.has(peerId) && peers.size >= MAX_PEERS) {
      return this.reject(ws, '房间满了');
    }

    peers.set(peerId, {
      id: peerId,
      name: clean(msg.name, MAX_NAME) || '无名',
      voice: clean(msg.voice, MAX_VOICE),
      xpose: clampSemitones(msg.xpose),
      ready: false,
    });
    await this.savePeers();

    ws.serializeAttachment({ peerId, sent: 0, windowStart: Date.now() });
    send(ws, { t: 'welcome', v: PROTOCOL_VERSION, peerId });
    await this.broadcastRoster();
  }

  // ================= 转发 =================

  async broadcastRoster() {
    await this.loadPeers();
    const list = [...this.peers.values()].map((p) => ({
      id: p.id, name: p.name, voice: p.voice, xpose: p.xpose, ready: p.ready,
    }));
    await this.broadcast({ t: 'roster', peers: list });
  }

  /** 发给房间里所有人。except 非空时不发给他自己，省一条。 */
  async broadcast(msg, except) {
    const text = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      if (ws === except) continue;
      try {
        ws.send(text);
      } catch {
        // 发不出去说明这条连接已经坏了，等 close 回调来清
      }
    }
  }

  async dropPeer(ws, announce) {
    const att = safeAttachment(ws);
    if (announce && att && att.peerId) {
      const peers = await this.loadPeers();
      if (peers.delete(att.peerId)) {
        await this.savePeers();
        await this.broadcastRoster();
      }
    }
    try {
      ws.serializeAttachment({ peerId: null, sent: 0, windowStart: Date.now() });
    } catch {
      // 连接已经没了，忽略
    }
  }

  // ================= 小工具 =================

  allow(ws, att) {
    const now = Date.now();
    if (now - att.windowStart > 10_000) {
      att.windowStart = now;
      att.sent = 0;
    }
    att.sent++;
    ws.serializeAttachment(att);
    if (att.sent > RATE_LIMIT_PER_10S) {
      this.reject(ws, '发得太快');
      return false;
    }
    return true;
  }

  reject(ws, reason) {
    try {
      ws.send(JSON.stringify({ t: 'error', reason }));
      ws.close(1008, reason);
    } catch {
      // 已经断了
    }
  }
}

// ================= 纯函数 =================

function send(ws, obj) {
  try {
    ws.send(JSON.stringify(obj));
  } catch {
    // 忽略
  }
}

function safeAttachment(ws) {
  try {
    return ws.deserializeAttachment();
  } catch {
    return null;
  }
}

function clampDelay(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return DEFAULT_DELAY_MS;
  return Math.max(MIN_DELAY_MS, Math.min(MAX_DELAY_MS, Math.round(n)));
}

function clampSemitones(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return 0;
  return Math.max(-24, Math.min(24, Math.round(n)));
}

/** 去掉控制字符、首尾空白，并截到上限。名字与声部名都要过这一道。 */
function clean(value, max) {
  if (typeof value !== 'string') return '';
  // eslint-disable-next-line no-control-regex
  return value.replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, max);
}

async function pbkdf2Hex(input, salt) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey('raw', enc.encode(input), 'PBKDF2', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', salt: enc.encode(salt), iterations: PBKDF2_ITERATIONS, hash: 'SHA-256' },
    key,
    256,
  );
  return [...new Uint8Array(bits)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
