/**
 * 端到端：真的把 `src/server.mjs` 作为子进程拉起来，走 stdio 说话。
 *
 * 单元测试证明各模块自洽；这个测试证明**它能作为一个 MCP 服务器被启动和使用** ——
 * 包括最容易翻车的一点：stdout 上不能出现任何非协议内容。
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const PROJECT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function startServer(env = {}) {
  const child = spawn(process.execPath, ['src/server.mjs'], {
    cwd: PROJECT_ROOT,
    env: {
      ...process.env,
      NUTSTORE_ROOTS: '/我的坚果云,code',
      NUTSTORE_READ_ONLY: 'true',
      NUTSTORE_USER: '',
      NUTSTORE_PASS: '',
      NUTSTORE_DEBUG: '',
      ...env,
    },
    stdio: ['pipe', 'pipe', 'pipe'],
  });

  const stdout = [];
  const stderr = [];
  const waiters = [];

  let buffer = '';
  child.stdout.on('data', (chunk) => {
    buffer += chunk;
    let i;
    while ((i = buffer.indexOf('\n')) !== -1) {
      const line = buffer.slice(0, i);
      buffer = buffer.slice(i + 1);
      if (!line.trim()) continue;
      stdout.push(line);
      for (const w of waiters.splice(0)) w();
    }
  });
  child.stderr.on('data', (c) => stderr.push(c.toString()));

  const send = (obj) => child.stdin.write(`${JSON.stringify(obj)}\n`);

  return {
    child,
    stdout,
    stderr,
    send,
    /** 等到 stdout 上至少有 n 行输出（或超时） */
    async waitForLines(n, timeoutMs = 10_000) {
      const deadline = Date.now() + timeoutMs;
      while (stdout.length < n) {
        if (Date.now() > deadline) {
          throw new Error(`超时：期望 ${n} 行 stdout，实际 ${stdout.length}\n${stdout.join('\n')}`);
        }
        await new Promise((resolve) => {
          const timer = setTimeout(resolve, 50);
          waiters.push(() => {
            clearTimeout(timer);
            resolve();
          });
        });
      }
    },
    async stop() {
      child.stdin.end();
      await new Promise((resolve) => {
        const timer = setTimeout(() => {
          child.kill();
          resolve();
        }, 3000);
        child.once('exit', () => {
          clearTimeout(timer);
          resolve();
        });
      });
    },
  };
}

test('端到端：初始化 → 列工具 → 调用工具，stdout 全程只有协议消息', async (t) => {
  const srv = startServer();
  t.after(() => srv.stop());

  srv.send({
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: { protocolVersion: '2025-06-18', clientInfo: { name: 'e2e', version: '1' } },
  });
  await srv.waitForLines(1);

  srv.send({ jsonrpc: '2.0', method: 'notifications/initialized' });
  srv.send({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
  await srv.waitForLines(2);

  srv.send({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'nutstore_sync_folders', arguments: {} } });
  await srv.waitForLines(3);

  // 每一行 stdout 都必须是合法 JSON-RPC —— 一个混进来的 console.log 就会让这里失败
  const messages = srv.stdout.map((line, i) => {
    try {
      return JSON.parse(line);
    } catch {
      assert.fail(`stdout 第 ${i + 1} 行不是合法 JSON（协议流被污染）：${line.slice(0, 200)}`);
    }
  });

  const [init, list, call] = messages;
  assert.equal(init.result.protocolVersion, '2025-06-18');
  assert.equal(init.result.serverInfo.name, 'nutstore-mcp');
  assert.match(init.result.instructions, /L1 文件面/);

  assert.equal(list.result.tools.length, 14);
  const names = list.result.tools.map((x) => x.name);
  assert.ok(names.includes('nutstore_list'));
  assert.ok(names.includes('nutstore_sync_folders'));

  assert.equal(call.id, 3);
  assert.notEqual(call.result.isError, true, `工具调用失败：${call.result?.content?.[0]?.text}`);
  assert.ok(call.result.structuredContent, '应当返回 structuredContent');
});

test('端到端：工具失败返回 isError 而不是断开连接', async (t) => {
  const srv = startServer();
  t.after(() => srv.stop());

  srv.send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } });
  await srv.waitForLines(1);
  srv.send({
    jsonrpc: '2.0',
    id: 2,
    method: 'tools/call',
    params: { name: 'nutstore_list', arguments: { path: '/不在白名单里' } },
  });
  await srv.waitForLines(2);

  const res = JSON.parse(srv.stdout[1]);
  assert.equal(res.error, undefined, '不该是协议级错误');
  assert.equal(res.result.isError, true);
  assert.match(res.result.content[0].text, /PATH_NOT_ALLOWED|不在允许的白名单/);

  // 会话仍然可用
  srv.send({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'nutstore_settings', arguments: {} } });
  await srv.waitForLines(3);
  const after = JSON.parse(srv.stdout[2]);
  assert.equal(after.id, 3);
});

test('端到端：写操作在只读模式下被拒', async (t) => {
  const srv = startServer({ NUTSTORE_READ_ONLY: 'true' });
  t.after(() => srv.stop());

  srv.send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } });
  await srv.waitForLines(1);
  srv.send({
    jsonrpc: '2.0',
    id: 2,
    method: 'tools/call',
    params: { name: 'nutstore_mkdir', arguments: { path: '/我的坚果云/测试目录' } },
  });
  await srv.waitForLines(2);

  const res = JSON.parse(srv.stdout[1]);
  assert.equal(res.result.isError, true);
  assert.match(res.result.content[0].text, /READ_ONLY|只读/);
});

test('--doctor 以人类可读形式输出，且不泄漏凭据', async () => {
  const child = spawn(process.execPath, ['src/server.mjs', '--doctor'], {
    cwd: PROJECT_ROOT,
    env: {
      ...process.env,
      NUTSTORE_USER: 'someone@example.com',
      NUTSTORE_PASS: 'super-secret-app-password',
      NUTSTORE_ROOTS: '我的坚果云',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let out = '';
  child.stdout.on('data', (c) => { out += c.toString(); });
  const code = await new Promise((resolve) => child.once('exit', resolve));

  assert.equal(code, 0);
  assert.match(out, /自检/);
  assert.match(out, /云端白名单/);
  assert.ok(!out.includes('super-secret-app-password'), 'doctor 不得输出应用密码');
  assert.match(out, /someone@example\.com/, '账号名可以显示');
});
