import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { Readable } from 'node:stream';

import { loadConfig } from '../src/config.mjs';
import { buildTools } from '../src/tools.mjs';

async function tempDir(name) {
  return mkdtemp(path.join(os.tmpdir(), `ns-mcp-${name}-`));
}

function fakeWebdav() {
  const calls = [];
  return {
    calls,
    async list(p) { calls.push(['list', p]); return { path: p, entries: [], truncated: false }; },
    async stat(p) { calls.push(['stat', p]); return { path: p, isCollection: false, size: 1 }; },
    async readText(p) { calls.push(['readText', p]); return { text: 'hi', bytes: 2 }; },
    async download(p) {
      calls.push(['download', p]);
      return { stream: Readable.from([Buffer.from('payload')]), size: 7, etag: 'x' };
    },
    async uploadFile(cloudPath, localPath) {
      calls.push(['uploadFile', cloudPath, localPath]);
      return { path: cloudPath, bytes: 7, sizeKnown: true, created: true };
    },
    async mkdir(p) { calls.push(['mkdir', p]); return { path: p, created: true }; },
    async mkdirp(p) { calls.push(['mkdirp', p]); return { path: p, created: true, createdPaths: [p] }; },
    async move(from, to, o) { calls.push(['move', from, to, o]); return { from, to }; },
    async remove(p) { calls.push(['remove', p]); return { path: p, deleted: true }; },
    async health() {
      calls.push(['health']);
      return { authenticatedAs: 'u@example.com', quota: null, rateLimit: { limit: 600, used: 1, remaining: 599 }, roots: ['/code'], readOnly: false };
    },
  };
}

function fakeLocal({ available = true } = {}) {
  const calls = [];
  return {
    calls,
    available,
    dbPath: available ? 'C:\\fake\\nutstore.db' : null,
    listSyncFolders() { calls.push(['listSyncFolders']); return { count: 1, folders: [{ cloudPath: '/code', name: 'code', localPath: 'C:\\fake\\code' }], unmountedSandboxes: [] }; },
    summary() { calls.push(['summary']); return { available: true, syncFolders: 1, trackedObjects: 2 }; },
    getSettings() { calls.push(['getSettings']); return { account: { user: 'u@example.com' }, client: {} }; },
    getSyncErrors(o) { calls.push(['getSyncErrors', o]); return { count: 0, errors: [] }; },
    fileStatus(p) { calls.push(['fileStatus', p]); return { path: p, tracked: true }; },
    close() {},
  };
}

async function harness({ env = {}, localOpts = {} } = {}) {
  const localRoot = await tempDir('local');
  const config = loadConfig({
    NUTSTORE_ROOTS: '/code',
    NUTSTORE_READ_ONLY: 'true',
    NUTSTORE_LOCAL_ROOTS: localRoot,
    ...env,
  });
  const webdav = fakeWebdav();
  const local = fakeLocal(localOpts);
  const tools = new Map(buildTools({ config, webdav, local }).map((t) => [t.name, t]));
  return {
    config,
    webdav,
    local,
    tools,
    localRoot,
    call: (name, args) => {
      const tool = tools.get(name);
      if (!tool) throw new Error(`没有这个工具：${name}`);
      return tool.handler(args ?? {});
    },
  };
}

// ───────────────────────── 注册表 ─────────────────────────

test('注册了预期的工具集，且每个都有合法 schema', async () => {
  const h = await harness();
  const expected = [
    'nutstore_health', 'nutstore_list', 'nutstore_stat', 'nutstore_read_text',
    'nutstore_download', 'nutstore_upload', 'nutstore_mkdir', 'nutstore_move',
    'nutstore_delete', 'nutstore_sync_folders', 'nutstore_sync_status',
    'nutstore_file_status', 'nutstore_sync_errors', 'nutstore_settings',
  ];
  assert.deepEqual([...h.tools.keys()].sort(), expected.sort());

  for (const [name, tool] of h.tools) {
    assert.ok(tool.description?.length > 10, `${name} 缺少可读描述`);
    assert.equal(tool.inputSchema.type, 'object', `${name} 的 schema 必须是 object`);
    assert.equal(tool.inputSchema.additionalProperties, false, `${name} 应禁止未声明参数`);
    for (const req of tool.inputSchema.required ?? []) {
      assert.ok(req in tool.inputSchema.properties, `${name} 的 required 里有未声明的 ${req}`);
    }
    assert.equal(typeof tool.annotations?.readOnlyHint, 'boolean', `${name} 缺少 readOnlyHint`);
  }
});

test('写工具被明确标注为 destructive', async () => {
  const h = await harness();
  for (const name of ['nutstore_delete', 'nutstore_move', 'nutstore_upload']) {
    assert.equal(h.tools.get(name).annotations.destructiveHint, true, `${name} 应标注 destructive`);
  }
  for (const name of ['nutstore_list', 'nutstore_read_text', 'nutstore_settings']) {
    assert.equal(h.tools.get(name).annotations.readOnlyHint, true, `${name} 应标注 readOnly`);
  }
});

// ───────────────────────── 只读闸门 ─────────────────────────

test('只读模式拦截全部写操作，且不触达 WebDAV', async () => {
  const h = await harness();
  const attempts = [
    ['nutstore_upload', { localPath: 'a.txt', path: '/code/a.txt' }],
    ['nutstore_mkdir', { path: '/code/x' }],
    ['nutstore_move', { from: '/code/a', to: '/code/b' }],
    ['nutstore_delete', { path: '/code/a', confirm: true }],
  ];
  for (const [name, args] of attempts) {
    await assert.rejects(() => h.call(name, args), (err) => {
      assert.equal(err.code, 'READ_ONLY', `${name} 应被只读闸门拦下`);
      assert.match(err.hint, /NUTSTORE_READ_ONLY/);
      return true;
    });
  }
  assert.deepEqual(h.webdav.calls, [], '只读拦截必须发生在发请求之前');
});

test('只读模式下读操作照常可用', async () => {
  const h = await harness();
  await h.call('nutstore_list', { path: '/code' });
  await h.call('nutstore_stat', { path: '/code/a.md' });
  await h.call('nutstore_read_text', { path: '/code/a.md' });
  await h.call('nutstore_sync_folders');
  await h.call('nutstore_settings');
  assert.equal(h.webdav.calls.length, 3);
});

test('开启写模式后 mkdir 可用', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false' } });
  const r = await h.call('nutstore_mkdir', { path: '/code/new' });
  assert.equal(r.created, true);
  const r2 = await h.call('nutstore_mkdir', { path: '/code/deep/sub', recursive: true });
  assert.deepEqual(r2.createdPaths, ['/code/deep/sub']);
});

// ───────────────────────── 删除的二次确认 ─────────────────────────

test('删除必须显式 confirm，且确认后才真正执行', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false' } });

  await assert.rejects(() => h.call('nutstore_delete', { path: '/code/a' }), /需要显式确认/);
  await assert.rejects(() => h.call('nutstore_delete', { path: '/code/a', confirm: false }), /需要显式确认/);
  assert.deepEqual(h.webdav.calls, []);

  const r = await h.call('nutstore_delete', { path: '/code/a', confirm: true });
  assert.equal(r.deleted, true);
  assert.deepEqual(h.webdav.calls, [['remove', '/code/a']]);
});

test('confirm 在只读模式下也不放行（两道闸门都要过）', async () => {
  const h = await harness();
  await assert.rejects(() => h.call('nutstore_delete', { path: '/code/a', confirm: true }), (err) => {
    assert.equal(err.code, 'READ_ONLY');
    return true;
  });
});

// ───────────────────────── 云端白名单 ─────────────────────────

test('云端白名单对每个工具都生效', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false', NUTSTORE_ROOTS: '/code' } });
  const cases = [
    ['nutstore_list', { path: '/secret' }],
    ['nutstore_stat', { path: '/secret/a' }],
    ['nutstore_read_text', { path: '/secret/a' }],
    ['nutstore_download', { path: '/secret/a', localPath: 'a' }],
    ['nutstore_mkdir', { path: '/secret/x' }],
    ['nutstore_move', { from: '/code/a', to: '/secret/a' }],
    ['nutstore_delete', { path: '/secret/a', confirm: true }],
    ['nutstore_upload', { localPath: path.join('PLACEHOLDER', 'f.txt'), path: '/secret/f.txt' }],
  ];
  for (const [name, args] of cases) {
    await assert.rejects(() => h.call(name, args), (err) => {
      assert.ok(
        err.code === 'PATH_NOT_ALLOWED' || err.code === 'ESRCH' || err.code === 'ENOENT' || err.code === 'EACCES',
        `${name} 期望被白名单拦下，实际 ${err.code}: ${err.message}`,
      );
      return true;
    });
  }
  assert.deepEqual(h.webdav.calls, []);
});

test('空白名单时一切云端访问都被拒绝（fail-closed）', async () => {
  const h = await harness({ env: { NUTSTORE_ROOTS: '' } });
  await assert.rejects(() => h.call('nutstore_list', { path: '/code' }), (err) => {
    assert.equal(err.code, 'PATH_NOT_ALLOWED');
    return true;
  });
});

test('路径穿越与反斜杠被拒绝', async () => {
  const h = await harness();
  await assert.rejects(() => h.call('nutstore_list', { path: '/code/../../etc' }), (err) => {
    assert.equal(err.code, 'PATH_NOT_ALLOWED');
    return true;
  });
  await assert.rejects(() => h.call('nutstore_list', { path: 'C:\\Users' }), (err) => {
    assert.equal(err.code, 'PATH_NOT_ALLOWED');
    return true;
  });
});

// ───────────────────────── 本机白名单 ─────────────────────────

test('下载目标被限制在本机白名单内', async () => {
  const h = await harness();
  const outside = path.join(os.tmpdir(), 'ns-mcp-outside-target', 'x.txt');

  await assert.rejects(() => h.call('nutstore_download', { path: '/code/a', localPath: outside }), (err) => {
    assert.equal(err.code, 'PATH_NOT_ALLOWED');
    assert.match(err.hint, /NUTSTORE_LOCAL_ROOTS/);
    return true;
  });
  assert.deepEqual(h.webdav.calls, []);
});

test('下载到白名单内会真正落盘，并报告覆盖情况', async () => {
  const h = await harness();
  const r = await h.call('nutstore_download', { path: '/code/a.txt', localPath: 'sub/a.txt' });
  assert.equal(r.bytes, 7);
  assert.equal(r.overwritten, false);
  assert.equal(path.dirname(r.localPath), path.join(h.localRoot, 'sub'));
  assert.equal(await readFile(r.localPath, 'utf8'), 'payload');

  const again = await h.call('nutstore_download', { path: '/code/a.txt', localPath: 'sub/a.txt' });
  assert.equal(again.overwritten, true);
});

test('上传源文件被限制在本机白名单内（防任意文件外传）', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false' } });
  await assert.rejects(
    () => h.call('nutstore_upload', { localPath: 'C:\\Windows\\win.ini', path: '/code/win.ini' }),
    (err) => {
      assert.equal(err.code, 'PATH_NOT_ALLOWED');
      return true;
    },
  );
  assert.deepEqual(h.webdav.calls, []);
});

test('上传：以 "/" 结尾时补文件名，否则视为完整路径', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false' } });
  await writeFile(path.join(h.localRoot, 'report.md'), 'x');

  const a = await h.call('nutstore_upload', { localPath: 'report.md', path: '/code/2026/' });
  assert.equal(a.path, '/code/2026/report.md');

  const b = await h.call('nutstore_upload', { localPath: 'report.md', path: '/code/renamed.md' });
  assert.equal(b.path, '/code/renamed.md');

  // 无扩展名的目标不应被「猜」成目录
  const c = await h.call('nutstore_upload', { localPath: 'report.md', path: '/code/notes' });
  assert.equal(c.path, '/code/notes');
});

// ───────────────────────── health ─────────────────────────

test('无凭据时 health 不报错，而是如实说明 L1 不可用', async () => {
  const h = await harness({ env: { NUTSTORE_USER: '', NUTSTORE_PASS: '' } });
  const r = await h.call('nutstore_health');
  assert.equal(r.config.hasCredentials, false);
  assert.equal(r.webdav.ok, false);
  assert.equal(r.webdav.reason, 'NO_CREDENTIALS');
  assert.match(r.webdav.hint, /NUTSTORE_USER/);
  assert.equal(r.local.available, true, 'L2 不依赖凭据，必须仍报告可用');
});

test('有凭据时 health 汇总连通性', async () => {
  const h = await harness({ env: { NUTSTORE_USER: 'u@example.com', NUTSTORE_PASS: 'p' } });
  const r = await h.call('nutstore_health');
  assert.equal(r.webdav.ok, true);
  assert.equal(r.webdav.authenticatedAs, 'u@example.com');
});

test('本地库不可用时 health 给出提示但不失败', async () => {
  const h = await harness({ localOpts: { available: false } });
  const r = await h.call('nutstore_health');
  assert.equal(r.local.available, false);
  assert.match(r.local.hint, /L1 文件面不受影响/);
});

// ───────────────────────── 透传正确性 ─────────────────────────

test('read_text 的上限参数被透传', async () => {
  const h = await harness();
  let seen = null;
  h.webdav.readText = async (p, o) => { seen = [p, o]; return { text: '', bytes: 0 }; };
  await h.call('nutstore_read_text', { path: '/code/a.md', maxBytes: 123 });
  assert.deepEqual(seen, ['/code/a.md', { maxBytes: 123 }]);
});

test('sync_errors 的 limit 被透传', async () => {
  const h = await harness();
  await h.call('nutstore_sync_errors', { limit: 7 });
  assert.deepEqual(h.local.calls.at(-1), ['getSyncErrors', { limit: 7 }]);
});

test('move 默认不覆盖', async () => {
  const h = await harness({ env: { NUTSTORE_READ_ONLY: 'false' } });
  await h.call('nutstore_move', { from: '/code/a', to: '/code/b' });
  assert.deepEqual(h.webdav.calls.at(-1), ['move', '/code/a', '/code/b', { overwrite: false }]);

  await h.call('nutstore_move', { from: '/code/a', to: '/code/b', overwrite: true });
  assert.deepEqual(h.webdav.calls.at(-1), ['move', '/code/a', '/code/b', { overwrite: true }]);
});
