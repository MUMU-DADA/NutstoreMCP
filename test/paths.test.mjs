import test from 'node:test';
import assert from 'node:assert/strict';
import os from 'node:os';
import path from 'node:path';

import {
  assertLocalPathAllowed,
  assertPathAllowed,
  encodeCloudPath,
  hrefToCloudPath,
  isPathAllowed,
  normalizeCloudPath,
  normalizeRoots,
  toWebdavUrl,
} from '../src/paths.mjs';
import { PathNotAllowedError } from '../src/errors.mjs';

test('normalizeCloudPath 基本规范化', () => {
  assert.equal(normalizeCloudPath('/a/b'), '/a/b');
  assert.equal(normalizeCloudPath('a/b'), '/a/b');
  assert.equal(normalizeCloudPath('/a//b/'), '/a/b');
  assert.equal(normalizeCloudPath('/'), '/');
  assert.equal(normalizeCloudPath(''), '/');
  assert.equal(normalizeCloudPath('/我的坚果云/文档'), '/我的坚果云/文档');
});

test('normalizeCloudPath 拒绝穿越与非法字符', () => {
  assert.throws(() => normalizeCloudPath('/a/../b'), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath('/..'), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath('/a/./b'), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath('C:\\Users\\x'), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath('/a\u0000b'), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath(null), PathNotAllowedError);
  assert.throws(() => normalizeCloudPath(42), PathNotAllowedError);
});

test('encodeCloudPath 按段编码且保留分隔符', () => {
  assert.equal(encodeCloudPath('/a/b'), '/a/b');
  assert.equal(encodeCloudPath('/我的坚果云'), '/%E6%88%91%E7%9A%84%E5%9D%9A%E6%9E%9C%E4%BA%91');
  assert.equal(encodeCloudPath('/a b/c'), '/a%20b/c');
});

test('百分号编码的穿越尝试不会逃出白名单', () => {
  // 字面量语义：%2e%2e 是普通目录名，编码后 % 变成 %25，
  // 服务端收到的是字面字符串而不是 ".."
  const p = normalizeCloudPath('/root/%2e%2e/secret');
  assert.equal(p, '/root/%2e%2e/secret');
  const url = encodeCloudPath(p);
  assert.ok(url.includes('%252e%252e'), `期望双重编码，实际：${url}`);
  assert.ok(!url.includes('%2e%2e/'), '不得输出可直接解码为穿越的 URL');
  // 且它仍然落在 /root 白名单内（因为它本来就是 /root 下的字面路径）
  assert.equal(isPathAllowed(p, ['/root']), true);
});

test('toWebdavUrl 拼接', () => {
  assert.equal(toWebdavUrl('https://dav.jianguoyun.com/dav', '/a'), 'https://dav.jianguoyun.com/dav/a');
  assert.equal(toWebdavUrl('https://dav.jianguoyun.com/dav/', '/a'), 'https://dav.jianguoyun.com/dav/a');
  assert.equal(toWebdavUrl('https://dav.jianguoyun.com/dav', '/'), 'https://dav.jianguoyun.com/dav/');
});

test('hrefToCloudPath 处理绝对 URL、绝对路径与相对路径', () => {
  const base = 'https://dav.jianguoyun.com/dav';
  assert.equal(hrefToCloudPath('https://dav.jianguoyun.com/dav/a/b', base), '/a/b');
  assert.equal(hrefToCloudPath('/dav/a/b', base), '/a/b');
  assert.equal(hrefToCloudPath('/dav/', base), '/');
  assert.equal(hrefToCloudPath('/dav', base), '/');
  assert.equal(
    hrefToCloudPath('/dav/%E6%88%91%E7%9A%84/note.md', base),
    '/我的/note.md',
  );
  assert.equal(hrefToCloudPath('/dav/a/b?x=1#frag', base), '/a/b');
  // 目录 href 通常带尾斜杠
  assert.equal(hrefToCloudPath('/dav/docs/', base), '/docs');
});

test('isPathAllowed 空白名单 = 拒绝一切（fail-closed）', () => {
  assert.equal(isPathAllowed('/anything', []), false);
  assert.equal(isPathAllowed('/anything', null), false);
  assert.equal(isPathAllowed('/anything', undefined), false);
});

test('isPathAllowed 在片段边界上匹配，不是字符串前缀', () => {
  assert.equal(isPathAllowed('/code', ['/code']), true);
  assert.equal(isPathAllowed('/code/a.md', ['/code']), true);
  assert.equal(isPathAllowed('/code2', ['/code']), false, '/code2 不应被 /code 放行');
  assert.equal(isPathAllowed('/code2/a', ['/code']), false);
  assert.equal(isPathAllowed('/other', ['/code']), false);
});

test('isPathAllowed 显式根白名单放行一切', () => {
  assert.equal(isPathAllowed('/any/thing', ['/']), true);
});

test('assertPathAllowed 的错误里带上白名单', () => {
  try {
    assertPathAllowed('/secret', ['/code']);
    assert.fail('应当抛出');
  } catch (err) {
    assert.ok(err instanceof PathNotAllowedError);
    assert.match(err.hint, /\/code/);
  }
  // 空白名单时的提示要明确说明是「拒绝一切」
  try {
    assertPathAllowed('/secret', []);
  } catch (err) {
    assert.match(err.hint, /拒绝一切/);
  }
});

test('normalizeRoots 丢弃非法条目而不是崩溃', () => {
  assert.deepEqual(normalizeRoots(['code', '/code', '  ', '/a/../b', null]), ['/code']);
  assert.deepEqual(normalizeRoots(['/']), ['/']);
  assert.deepEqual(normalizeRoots(undefined), []);
});

test('assertLocalPathAllowed 用 path.relative 判断，避免前缀误放行', () => {
  const root = path.join(os.tmpdir(), 'ns-mcp-test-root');
  const evil = path.join(os.tmpdir(), 'ns-mcp-test-root-evil');

  assert.equal(assertLocalPathAllowed(path.join(root, 'a.txt'), [root]), path.resolve(root, 'a.txt'));
  assert.equal(assertLocalPathAllowed(root, [root]), path.resolve(root));
  assert.equal(assertLocalPathAllowed(path.join(root, 'sub', 'b'), [root]), path.resolve(root, 'sub', 'b'));

  // 关键：相邻前缀目录不得被放行
  assert.throws(() => assertLocalPathAllowed(path.join(evil, 'x'), [root]), PathNotAllowedError);
  assert.throws(() => assertLocalPathAllowed(path.join(os.tmpdir(), 'elsewhere'), [root]), PathNotAllowedError);

  // 用 .. 也不能逃出去
  assert.throws(
    () => assertLocalPathAllowed(path.join(root, '..', 'outside'), [root]),
    PathNotAllowedError,
  );

  assert.throws(() => assertLocalPathAllowed('/x', []), PathNotAllowedError);
});
