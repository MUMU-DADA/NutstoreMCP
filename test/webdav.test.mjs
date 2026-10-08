import test from 'node:test';
import assert from 'node:assert/strict';

import { loadConfig } from '../src/config.mjs';
import { AuthError, NotFoundError, PathNotAllowedError, RateLimitError, RemoteError } from '../src/errors.mjs';
import { SlidingWindowLimiter } from '../src/ratelimit.mjs';
import { WebdavClient, LIST_LIMIT, parseMultiStatus } from '../src/webdav.mjs';
import { createMockWebdav } from './helpers/mock-webdav.mjs';

function makeConfig(base, overrides = {}) {
  return loadConfig({
    NUTSTORE_USER: 'test@example.com',
    NUTSTORE_PASS: 'app-password',
    NUTSTORE_HOST: base,
    NUTSTORE_ROOTS: '/code',
    NUTSTORE_READ_ONLY: 'false',
    ...overrides,
  });
}

async function withServer(opts, fn) {
  const mock = createMockWebdav(opts);
  const base = await mock.listen();
  try {
    return await fn({ mock, base });
  } finally {
    await mock.close();
  }
}

function makeTree(entries) {
  const tree = new Map([['/', { type: 'dir' }], ['/code', { type: 'dir' }]]);
  for (const [p, v] of entries) tree.set(p, v);
  return tree;
}

function client(base, overrides = {}) {
  const config = makeConfig(base, overrides);
  const limiter = new SlidingWindowLimiter({
    limit: config.rateLimit,
    windowMs: config.rateWindowMs,
  });
  return { webdav: new WebdavClient({ config, limiter }), config };
}

test('列目录：解析 multistatus，目录与文件区分正确', async () => {
  await withServer(
    {
      tree: makeTree([
        ['/code/a.md', { type: 'file', data: Buffer.from('hello') }],
        ['/code/sub', { type: 'dir' }],
        ['/code/sub/b.txt', { type: 'file', data: Buffer.from('x') }],
      ]),
    },
    async ({ base }) => {
      const { webdav } = client(base);
      const result = await webdav.list('/code');
      assert.equal(result.path, '/code');
      assert.equal(result.truncated, false);

      const byName = Object.fromEntries(result.entries.map((e) => [e.path, e]));
      assert.deepEqual(Object.keys(byName).sort(), ['/code/a.md', '/code/sub']);

      assert.equal(byName['/code/a.md'].isCollection, false);
      assert.equal(byName['/code/a.md'].size, 5);
      assert.equal(byName['/code/sub'].isCollection, true);
      assert.ok(byName['/code/sub'].etag);
      // 只返回直接子项，不含孙项
      assert.equal(byName['/code/sub/b.txt'], undefined);
    },
  );
});

test('stat / exists', async () => {
  await withServer(
    { tree: makeTree([['/code/a.md', { type: 'file', data: Buffer.from('abc') }]]) },
    async ({ base }) => {
      const { webdav } = client(base);
      const s = await webdav.stat('/code/a.md');
      assert.equal(s.path, '/code/a.md');
      assert.equal(s.size, 3);
      assert.equal(s.isCollection, false);

      assert.equal(await webdav.exists('/code/a.md'), true);
      assert.equal(await webdav.exists('/code/missing.md'), false);
    },
  );
});

test('stat 不存在时抛 NotFoundError', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base }) => {
    const { webdav } = client(base);
    await assert.rejects(() => webdav.stat('/code/nope'), NotFoundError);
  });
});

test('读文本与大小上限', async () => {
  await withServer(
    {
      tree: makeTree([
        ['/code/small.txt', { type: 'file', data: Buffer.from('你好，世界') }],
        ['/code/big.txt', { type: 'file', data: Buffer.alloc(5000, 0x61) }],
      ]),
    },
    async ({ base }) => {
      const { webdav } = client(base);

      const r = await webdav.readText('/code/small.txt');
      assert.equal(r.text, '你好，世界');
      assert.equal(r.bytes, Buffer.byteLength('你好，世界'));

      await assert.rejects(
        () => webdav.readText('/code/big.txt', { maxBytes: 1000 }),
        (err) => {
          assert.ok(err instanceof RemoteError);
          assert.match(err.message, /超过读取上限/);
          return true;
        },
      );
    },
  );
});

test('上传（Buffer 与流工厂）、下载、往返一致', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base, mock }) => {
    const { webdav } = client(base);

    const up = await webdav.upload('/code/new.txt', Buffer.from('payload-1'));
    assert.equal(up.created, true);
    assert.equal(up.bytes, 9);
    assert.equal(up.sizeKnown, true);

    const up2 = await webdav.upload('/code/stream.txt', function* gen() {
      yield Buffer.from('str');
      yield Buffer.from('eam');
    });
    // 工厂体长度未知 → 走 chunked，bytes 为 null 且显式标记
    assert.equal(up2.bytes, null);
    assert.equal(up2.sizeKnown, false);

    const { stream, size } = await webdav.download('/code/new.txt');
    const chunks = [];
    for await (const c of stream) chunks.push(c);
    assert.equal(Buffer.concat(chunks).toString(), 'payload-1');
    assert.equal(size, 9);

    assert.equal(mock.tree.get('/code/stream.txt').data.toString(), 'stream');
  });
});

test('上传超过上限时在发请求前就拒绝', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base, mock }) => {
    const { webdav } = client(base, { NUTSTORE_MAX_UPLOAD_BYTES: '10' });
    const before = mock.requests.length;
    await assert.rejects(
      () => webdav.upload('/code/big.bin', Buffer.alloc(100)),
      (err) => {
        assert.match(err.message, /超过上传上限/);
        assert.match(err.hint, /500 MB/);
        return true;
      },
    );
    assert.equal(mock.requests.length, before, '不应发出请求');
  });
});

test('mkdir 幂等，mkdirp 逐级创建', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base }) => {
    const { webdav } = client(base);

    const a = await webdav.mkdir('/code/x');
    assert.equal(a.created, true);
    const again = await webdav.mkdir('/code/x');
    assert.equal(again.created, false);
    assert.equal(again.existed, true);

    const deep = await webdav.mkdirp('/code/p/q/r');
    assert.deepEqual(deep.createdPaths, ['/code/p', '/code/p/q', '/code/p/q/r']);
  });
});

test('move / copy / delete', async () => {
  await withServer(
    { tree: makeTree([['/code/a.md', { type: 'file', data: Buffer.from('v') }]]) },
    async ({ base, mock }) => {
      const { webdav } = client(base);

      const moved = await webdav.move('/code/a.md', '/code/b.md', { overwrite: false });
      assert.equal(moved.overwritten, false);
      assert.ok(mock.tree.has('/code/b.md'));
      assert.ok(!mock.tree.has('/code/a.md'));

      const del = await webdav.remove('/code/b.md');
      assert.equal(del.deleted, true);

      const gone = await webdav.remove('/code/b.md');
      assert.equal(gone.deleted, false);
      assert.equal(gone.existed, false);
    },
  );
});

test('认证失败给出可操作的提示（应用密码 vs 登录密码）', async () => {
  await withServer({ tree: makeTree([]), pass: 'the-right-one' }, async ({ base }) => {
    const { webdav } = client(base);
    await assert.rejects(
      () => webdav.list('/code'),
      (err) => {
        assert.ok(err instanceof AuthError);
        assert.equal(err.code, 'AUTH_FAILED');
        assert.match(err.hint, /应用密码/);
        assert.match(err.hint, /不是登录密码/);
        return true;
      },
    );
  });
});

test('未配置凭据时直接报错，不发请求', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base, mock }) => {
    const config = loadConfig({
      NUTSTORE_HOST: base,
      NUTSTORE_ROOTS: '/code',
      NUTSTORE_USER: '',
      NUTSTORE_PASS: '',
    });
    const limiter = new SlidingWindowLimiter({ limit: 10, windowMs: 1000 });
    const webdav = new WebdavClient({ config, limiter });

    await assert.rejects(
      () => webdav.list('/code'),
      (err) => {
        assert.equal(err.code, 'AUTH_FAILED');
        assert.match(err.hint, /NUTSTORE_USER/);
        return true;
      },
    );
    assert.equal(mock.requests.length, 0);
  });
});

test('白名单之外一律拒绝（且不发出请求）', async () => {
  await withServer(
    { tree: makeTree([['/secret/x', { type: 'file', data: Buffer.from('s') }]]) },
    async ({ base, mock }) => {
      const { webdav } = client(base);
      await assert.rejects(() => webdav.list('/secret'), PathNotAllowedError);
      await assert.rejects(() => webdav.stat('/secret/x'), PathNotAllowedError);
      await assert.rejects(() => webdav.remove('/secret/x'), PathNotAllowedError);
      await assert.rejects(() => webdav.move('/code/a', '/secret/a'), PathNotAllowedError);
      assert.equal(mock.requests.length, 0);
    },
  );
});

test(`枚举到 ${LIST_LIMIT} 项时显式告警，不假装结果完整`, async () => {
  const tree = new Map([['/', { type: 'dir' }], ['/code', { type: 'dir' }]]);
  for (let i = 0; i < LIST_LIMIT + 50; i++) {
    tree.set(`/code/f${String(i).padStart(4, '0')}.txt`, { type: 'file', data: Buffer.from('x') });
  }

  await withServer({ tree, propfindLimit: LIST_LIMIT }, async ({ base }) => {
    const { webdav } = client(base);
    const result = await webdav.list('/code');
    assert.equal(result.truncated, true, '达到 750 条响应就必须告警');
    // 750 条响应含目录自身，所以子项是 749
    assert.equal(result.entries.length, LIST_LIMIT - 1);
    assert.match(result.warning, /750/);
    assert.match(result.warning, /可能不完整/);
  });
});

test('条目数未达上限时不误报截断', async () => {
  const tree = new Map([['/', { type: 'dir' }], ['/code', { type: 'dir' }]]);
  for (let i = 0; i < 10; i++) {
    tree.set(`/code/f${i}.txt`, { type: 'file', data: Buffer.from('x') });
  }
  await withServer({ tree, propfindLimit: LIST_LIMIT }, async ({ base }) => {
    const { webdav } = client(base);
    const result = await webdav.list('/code');
    assert.equal(result.truncated, false);
    assert.equal(result.warning, undefined);
    assert.equal(result.entries.length, 10);
  });
});

test('同源重定向被跟随，且方法被保留', async () => {
  await withServer(
    { tree: makeTree([['/code/redir', { type: 'dir' }]]), redirectPrefix: '/code/redir' },
    async ({ base, mock }) => {
      const { webdav } = client(base);
      const result = await webdav.list('/code/redir');
      assert.equal(result.path, '/code/redir');
      const propfinds = mock.requests.filter((r) => r.method === 'PROPFIND');
      assert.equal(propfinds.length, 2, '应当跟随一次重定向');
    },
  );
});

test('跨源重定向被拒绝，避免 Basic 凭据外泄', async () => {
  const other = createMockWebdav({ tree: makeTree([]) });
  const otherBase = await other.listen();
  try {
    await withServer(
      {
        tree: makeTree([]),
        handler: ({ res }) => {
          res.writeHead(302, { Location: `${otherBase}/code/` });
          res.end();
          return true;
        },
      },
      async ({ base }) => {
        const { webdav } = client(base);
        await assert.rejects(
          () => webdav.list('/code'),
          (err) => {
            assert.match(err.message, /跨源重定向/);
            assert.match(err.hint, /泄露/);
            return true;
          },
        );
      },
    );
  } finally {
    await other.close();
  }
});

test('限流器在客户端层生效', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base, mock }) => {
    const config = makeConfig(base, { NUTSTORE_RATE_LIMIT: '2' });
    const limiter = new SlidingWindowLimiter({
      limit: config.rateLimit,
      windowMs: config.rateWindowMs,
    });
    const webdav = new WebdavClient({ config, limiter });

    assert.equal(await webdav.exists('/code/a'), false);
    assert.equal(await webdav.exists('/code/b'), false);
    await assert.rejects(() => webdav.exists('/code/c'), RateLimitError);
    assert.equal(mock.requests.length, 2);
  });
});

test('health 汇总身份、配额与限流状态', async () => {
  await withServer(
    {
      tree: makeTree([]),
      handler: ({ req, res, path: p }) => {
        if (req.method === 'PROPFIND' && p === '/code') {
          const body = '<?xml version="1.0"?><D:multistatus xmlns:D="DAV:"><D:response>'
            + '<D:href>/dav/code/</D:href><D:propstat><D:prop>'
            + '<D:resourcetype><D:collection/></D:resourcetype>'
            + '<D:quota-available-bytes>1073741824</D:quota-available-bytes>'
            + '<D:quota-used-bytes>536870912</D:quota-used-bytes>'
            + '</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>';
          res.writeHead(207, { 'Content-Type': 'application/xml' });
          res.end(body);
          return true;
        }
        return false;
      },
    },
    async ({ base }) => {
      const { webdav } = client(base);
      const h = await webdav.health();
      assert.equal(h.authenticatedAs, 'test@example.com');
      assert.equal(h.probedPath, '/code', '诊断探针必须落在白名单内，不能探测 /');
      assert.equal(h.quota.availableBytes, 1073741824);
      assert.equal(h.quota.usedBytes, 536870912);
      assert.equal(h.rateLimit.limit, 600);
      assert.deepEqual(h.roots, ['/code']);
      assert.equal(h.readOnly, false);
    },
  );
});

test('health 在白名单为空时明确拒绝而不是偷偷探测根目录', async () => {
  await withServer({ tree: makeTree([]) }, async ({ base, mock }) => {
    const config = makeConfig(base, { NUTSTORE_ROOTS: '' });
    const limiter = new SlidingWindowLimiter({ limit: 10, windowMs: 1000 });
    const webdav = new WebdavClient({ config, limiter });
    await assert.rejects(
      () => webdav.health(),
      (err) => {
        assert.match(err.message, /白名单为空/);
        assert.match(err.hint, /NUTSTORE_ROOTS/);
        return true;
      },
    );
    assert.equal(mock.requests.length, 0);
  });
});

test('parseMultiStatus 忽略非 2xx 的 propstat，且 href 无法解析时跳过', () => {
  const xml = `<?xml version="1.0"?>
<D:multistatus xmlns:D="DAV:">
  <D:response>
    <D:href>/dav/code/</D:href>
    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
      <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
  <D:response>
    <D:href>/dav/code/ok.md</D:href>
    <D:propstat><D:prop><D:getcontentlength>12</D:getcontentlength></D:prop>
      <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
  </D:response>
  <D:response>
    <D:href>/dav/code/denied.md</D:href>
    <D:propstat><D:prop></D:prop>
      <D:status>HTTP/1.1 403 Forbidden</D:status></D:propstat>
  </D:response>
</D:multistatus>`;

  const r = parseMultiStatus(xml, 'https://dav.jianguoyun.com/dav', '/code');
  assert.equal(r.self.path, '/code');
  assert.equal(r.self.isCollection, true);
  assert.deepEqual(r.entries.map((e) => e.path), ['/code/ok.md']);
  assert.equal(r.entries[0].size, 12);
  assert.equal(r.truncated, false);
});

test('parseMultiStatus 对非 multistatus 输入报错', () => {
  assert.throws(() => parseMultiStatus('<html/>', 'https://x/dav', '/'), /multistatus/);
});
