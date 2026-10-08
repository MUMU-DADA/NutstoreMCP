import test from 'node:test';
import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';

import {
  DEFAULT_PROTOCOL_VERSION,
  McpServer,
  negotiateProtocolVersion,
  renderError,
} from '../src/mcp.mjs';

function makeServer() {
  const server = new McpServer({ name: 'test-server', version: '9.9.9', instructions: '测试用' });
  server.tool({
    name: 'echo',
    description: '回显',
    inputSchema: { type: 'object', properties: { text: { type: 'string' } } },
    handler: async ({ text }) => ({ echoed: text }),
  });
  server.tool({
    name: 'boom',
    description: '总是失败',
    handler: async () => {
      const err = new Error('磁盘满了');
      err.code = 'DISK_FULL';
      err.hint = '清理空间后重试';
      throw err;
    },
  });
  return server;
}

test('协议版本协商', () => {
  assert.equal(negotiateProtocolVersion('2025-06-18'), '2025-06-18');
  assert.equal(negotiateProtocolVersion('2026-07-28'), '2026-07-28');
  assert.equal(negotiateProtocolVersion('1999-01-01'), DEFAULT_PROTOCOL_VERSION);
  assert.equal(negotiateProtocolVersion(undefined), DEFAULT_PROTOCOL_VERSION);
});

test('initialize 返回能力与服务器信息', async () => {
  const res = await makeServer().handle({
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: { protocolVersion: '2025-06-18', clientInfo: { name: 'x' } },
  });
  assert.equal(res.id, 1);
  assert.equal(res.result.protocolVersion, '2025-06-18');
  assert.deepEqual(res.result.capabilities.tools, { listChanged: false });
  assert.equal(res.result.serverInfo.name, 'test-server');
  assert.equal(res.result.instructions, '测试用');
});

test('tools/list 输出完整 schema', async () => {
  const res = await makeServer().handle({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
  const names = res.result.tools.map((t) => t.name);
  assert.deepEqual(names.sort(), ['boom', 'echo']);
  const echo = res.result.tools.find((t) => t.name === 'echo');
  assert.equal(echo.inputSchema.properties.text.type, 'string');
});

test('tools/call 成功返回 content 与 structuredContent', async () => {
  const res = await makeServer().handle({
    jsonrpc: '2.0',
    id: 3,
    method: 'tools/call',
    params: { name: 'echo', arguments: { text: '你好' } },
  });
  assert.equal(res.result.isError, undefined);
  assert.equal(res.result.content[0].type, 'text');
  assert.match(res.result.content[0].text, /你好/);
  assert.deepEqual(res.result.structuredContent, { echoed: '你好' });
});

test('工具失败是工具级错误，不是协议级错误', async () => {
  const res = await makeServer().handle({
    jsonrpc: '2.0',
    id: 4,
    method: 'tools/call',
    params: { name: 'boom', arguments: {} },
  });
  // 关键：没有 error 字段，会话不会断
  assert.equal(res.error, undefined);
  assert.equal(res.result.isError, true);
  const text = res.result.content[0].text;
  assert.match(text, /磁盘满了/);
  assert.match(text, /DISK_FULL/);
  assert.match(text, /清理空间后重试/);
});

test('调用未知工具返回可用工具列表', async () => {
  const res = await makeServer().handle({
    jsonrpc: '2.0',
    id: 5,
    method: 'tools/call',
    params: { name: 'nope', arguments: {} },
  });
  assert.equal(res.error.code, -32602);
  assert.deepEqual(res.error.data.available.sort(), ['boom', 'echo']);
});

test('未知方法返回 METHOD_NOT_FOUND', async () => {
  const res = await makeServer().handle({ jsonrpc: '2.0', id: 6, method: 'bogus' });
  assert.equal(res.error.code, -32601);
});

test('通知不产生响应', async () => {
  const server = makeServer();
  assert.equal(await server.handle({ jsonrpc: '2.0', method: 'notifications/initialized' }), null);
  assert.equal(await server.handle({ jsonrpc: '2.0', method: 'notifications/cancelled', params: {} }), null);
  // 即使是未知方法的通知也不应回包
  assert.equal(await server.handle({ jsonrpc: '2.0', method: 'whatever' }), null);
});

test('ping 与空资源列表', async () => {
  const server = makeServer();
  assert.deepEqual((await server.handle({ jsonrpc: '2.0', id: 7, method: 'ping' })).result, {});
  assert.deepEqual((await server.handle({ jsonrpc: '2.0', id: 8, method: 'resources/list' })).result, { resources: [] });
  assert.deepEqual((await server.handle({ jsonrpc: '2.0', id: 9, method: 'prompts/list' })).result, { prompts: [] });
});

test('工具重名立即报错', () => {
  const server = new McpServer({ name: 'x', version: '1' });
  server.tool({ name: 'dup', handler: async () => 1 });
  assert.throws(() => server.tool({ name: 'dup', handler: async () => 2 }), /重名/);
});

test('renderError 在缺 hint 时也能工作', () => {
  assert.match(renderError(new Error('炸了')), /炸了/);
  assert.doesNotMatch(renderError(new Error('炸了')), /hint:/);
});

test('run() 端到端：换行分隔 JSON-RPC，stdout 只出现协议消息', async () => {
  const server = makeServer();
  const input = new PassThrough();
  const output = new PassThrough();

  const done = server.run({ input, output });

  const chunks = [];
  output.on('data', (c) => chunks.push(c));

  const send = (obj) => input.write(`${JSON.stringify(obj)}\n`);

  send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } });
  send({ jsonrpc: '2.0', method: 'notifications/initialized' });
  send({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'echo', arguments: { text: 'hi' } } });
  input.write('{ 这不是 JSON\n'); // 直接写裸字符串才是真正的语法错误
  send({ jsonrpc: '2.0', id: 3, method: 'tools/list' });

  input.end();
  await done;
  await new Promise((r) => setImmediate(r));

  const lines = Buffer.concat(chunks).toString().split('\n').filter(Boolean);
  // 4 条请求有响应（initialize / call / parse error / list），通知无响应
  assert.equal(lines.length, 4, `期望 4 条响应，实际 ${lines.length}：\n${lines.join('\n')}`);

  const parsed = lines.map((l) => JSON.parse(l));
  for (const msg of parsed) assert.equal(msg.jsonrpc, '2.0');

  assert.equal(parsed[0].id, 1);
  assert.equal(parsed[1].id, 2);
  assert.equal(parsed[1].result.structuredContent.echoed, 'hi');
  assert.equal(parsed[2].id, null);
  assert.equal(parsed[2].error.code, -32700);
  assert.equal(parsed[3].id, 3);
  assert.equal(parsed[3].result.tools.length, 2);
});

test('run() 处理半行与粘包', async () => {
  const server = makeServer();
  const input = new PassThrough();
  const output = new PassThrough();
  const done = server.run({ input, output });

  const chunks = [];
  output.on('data', (c) => chunks.push(c));

  const msg1 = JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'ping' });
  const msg2 = JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'ping' });
  input.write(msg1.slice(0, 10));
  await new Promise((r) => setImmediate(r));
  input.write(`${msg1.slice(10)}\n${msg2}\n`);
  input.end();
  await done;
  await new Promise((r) => setImmediate(r));

  const parsed = Buffer.concat(chunks).toString().split('\n').filter(Boolean).map((l) => JSON.parse(l));
  assert.deepEqual(parsed.map((p) => p.id), [1, 2]);
});
