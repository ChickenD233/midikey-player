// 远程同演中转站的本地联调：把真的一对客户端连到 wrangler dev 起的 Worker 上，
// 按真实协议跑一遍。不碰 Cloudflare 云端，全在本机。
//
// 用 node --experimental-strip-types 跑不了，这是纯 JS，直接 node test-relay.mjs。

import WebSocket from 'ws';
import { pbkdf2Sync } from 'node:crypto';

const URL_BASE = process.env.RELAY_URL || 'ws://127.0.0.1:8787';
const ROOM = process.env.RELAY_ROOM || '测试琴房';
const PASS = process.env.RELAY_PASS || 'pw123';

// 房间键的算法必须与程序（Engine/SyncModel.cs 的 DeriveRoomKey）和中转站一致：
// PBKDF2-SHA256，盐 "midikeyplayer:room:v1"，10 万次迭代，输出 32 字节十六进制。
const KEY_SALT = 'midikeyplayer:room:v1';
const KEY_ITERATIONS = 100_000;
const roomKey = pbkdf2Sync(ROOM, KEY_SALT, KEY_ITERATIONS, 32, 'sha256').toString('hex');

let pass = 0, fail = 0;
function check(name, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '   [' + detail + ']' : ''}`);
  if (ok) pass++; else fail++;
}

// 客户端：连上、发 join、把收到的消息排进队列
function connect(peerId, name, voice, xpose = 0, password = PASS) {
  const url = `${URL_BASE}/room?k=${roomKey}`;
  const ws = new WebSocket(url);
  const inbox = [];
  const waiters = [];
  ws.on('message', (raw) => {
    const msg = JSON.parse(raw.toString());
    for (let i = 0; i < waiters.length; i++) {
      if (waiters[i].pred(msg)) { waiters[i].resolve(msg); waiters.splice(i, 1); return; }
    }
    inbox.push(msg);
  });
  const api = {
    ws,
    inbox,
    open: () => new Promise((res, rej) => { ws.once('open', res); ws.once('error', rej); }),
    send: (obj) => ws.send(JSON.stringify(obj)),
    join: () => ws.send(JSON.stringify({ t: 'join', id: peerId, name, pass: password, voice, xpose })),
    // 等一条满足条件的消息；先翻已收到的，再等新的
    wait: (pred, ms = 3000) => new Promise((resolve, reject) => {
      const hit = inbox.find(pred);
      if (hit) return resolve(hit);
      const w = { pred, resolve };
      waiters.push(w);
      setTimeout(() => {
        const i = waiters.indexOf(w);
        if (i >= 0) { waiters.splice(i, 1); reject(new Error('超时')); }
      }, ms);
    }),
    close: () => ws.close(),
  };
  return api;
}

const roster = (m) => m.t === 'roster';
const names = (m) => (m.peers || []).map((p) => p.name).sort();

async function main() {
  console.log(`中转站：${URL_BASE}   房间：${ROOM}`);
  console.log(`房间键：${roomKey.slice(0, 16)}…（与程序同一套算法）\n`);

  // ---------- 1. 第一个客户端定义房间密码 ----------
  const a = connect('m-aaaa', '小明', '1、3', 0);
  await a.open();
  a.join();
  const welcomeA = await a.wait((m) => m.t === 'welcome');
  check('甲连接成功并收到 welcome', welcomeA.t === 'welcome', `peerId=${welcomeA.peerId}`);
  const r1 = await a.wait(roster);
  check('甲收到名单，只有自己', names(r1).join(',') === '小明', names(r1).join(','));

  // ---------- 2. 第二个客户端用对密码进来 ----------
  const b = connect('m-bbbb', '小红', '2、4', -5);
  await b.open();
  b.join();
  await b.wait((m) => m.t === 'welcome');
  const r2 = await a.wait((m) => roster(m) && m.peers.length === 2);
  check('乙用对密码进来了，甲看到两人', names(r2).join(',') === '小明,小红', names(r2).join(','));

  const bInfo = r2.peers.find((p) => p.name === '小红');
  check('乙的声部传对了', bInfo.voice === '2、4', bInfo.voice);
  check('乙的移调传对了', bInfo.xpose === -5, String(bInfo.xpose));
  check('刚进来时未就绪', bInfo.ready === false, String(bInfo.ready));

  // ---------- 3. 密码不对要被拒 ----------
  const bad = connect('m-bad', '坏人', '1', 0, '错误密码');
  await bad.open();
  bad.join();
  const err = await bad.wait((m) => m.t === 'error');
  check('密码不对被拒', err.reason === '密码不对', err.reason);
  bad.close();

  // ---------- 4. 就绪要广播 ----------
  b.send({ t: 'ready', on: true });
  const r3 = await a.wait((m) => roster(m) && m.peers.some((p) => p.name === '小红' && p.ready));
  check('乙点就绪，甲看得到', r3.peers.find((p) => p.name === '小红').ready === true);

  // ---------- 5. 开演：房主发时刻，成员收到 ----------
  const sentAt = 123456;
  a.send({ t: 'start', delayMs: 3000, positionSec: 12.5, sentAt });
  const got = await b.wait((m) => m.t === 'start');
  check('乙收到开演', got.t === 'start');
  check('开演延迟传对了', got.delayMs === 3000, String(got.delayMs));
  check('起始位置传对了', got.positionSec === 12.5, String(got.positionSec));
  check('房主的单调时刻传对了（成员靠它减掉路上时间）',
    got.sentAt === sentAt, `${got.sentAt}`);
  check('开演消息带了发起人', got.from === 'm-aaaa', String(got.from));

  // ---------- 6. 暂停与停止 ----------
  a.send({ t: 'pause' });
  check('乙收到暂停', (await b.wait((m) => m.t === 'pause')).t === 'pause');
  a.send({ t: 'stop' });
  check('乙收到停止', (await b.wait((m) => m.t === 'stop')).t === 'stop');

  // ---------- 7. 统一移调 ----------
  a.send({ t: 'xpose', xpose: -5 });
  const xp = await b.wait((m) => m.t === 'xpose');
  check('乙收到统一移调 -5', xp.xpose === -5, String(xp.xpose));
  a.send({ t: 'xpose', xpose: null });
  const xp2 = await b.wait((m) => m.t === 'xpose' && m.xpose === 0);
  check('取消统一移调按 0 转发', xp2.xpose === 0, String(xp2.xpose));

  // ---------- 8. 改声部要广播 ----------
  b.send({ t: 'voice', voice: '3、4' });
  const r4 = await a.wait((m) => roster(m) && m.peers.some((p) => p.voice === '3、4'));
  check('乙改声部，甲看得到', r4.peers.find((p) => p.name === '小红').voice === '3、4');

  // ---------- 9. 不认识的消息类型要静默忽略 ----------
  a.send({ t: '我乱发的' });
  a.send({ t: 'stop' });
  const still = await b.wait((m) => m.t === 'stop');
  check('乱发的类型被忽略，后面的消息照常到', still.t === 'stop');

  // ---------- 10. 断开要广播 ----------
  b.close();
  const r5 = await a.wait((m) => roster(m) && m.peers.length === 1, 5000);
  check('乙断开，甲看到只剩自己', names(r5).join(',') === '小明', names(r5).join(','));
  a.close();

  console.log(`\n结果：${pass} 通过 / ${fail} 失败`);
  process.exit(fail === 0 ? 0 : 1);
}

main().catch((e) => { console.error('联调脚本异常：', e); process.exit(2); });
