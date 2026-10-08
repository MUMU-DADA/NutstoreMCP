/**
 * 内存版 mock WebDAV 服务器。
 *
 * 用它来真实验证 WebdavClient：走真 HTTP、真状态码、真 multistatus XML，
 * 而不是 mock 掉 fetch —— 后者只能证明代码自洽，证明不了协议正确。
 */

import http from 'node:http';

const XML_ESCAPE = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' };

function esc(s) {
  return String(s).replace(/[&<>"']/g, (c) => XML_ESCAPE[c]);
}

function xmlDate(d = new Date()) {
  return d.toUTCString();
}

/**
 * @param {object} opts
 * @param {string} opts.user
 * @param {string} opts.pass
 * @param {Map<string, {type:'dir'|'file', data?:Buffer}>} [opts.tree]
 * @param {number} [opts.propfindLimit] 模拟坚果云 750 项截断
 * @param {string|null} [opts.redirectTo] 对该前缀的请求返回 302
 */
export function createMockWebdav({
  user = 'test@example.com',
  pass = 'app-password',
  tree = new Map(),
  propfindLimit = null,
  redirectPrefix = null,
  handler: customHandler = null,
  basePath = '/dav',
} = {}) {
  const requests = [];

  if (!tree.has('/')) tree.set('/', { type: 'dir' });

  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    // 剥掉挂载前缀 —— 真实 WebDAV 端点也挂在 /dav 之下
    let decoded = decodeURIComponent(url.pathname);
    if (basePath && decoded.startsWith(basePath)) decoded = decoded.slice(basePath.length);
    if (decoded === '') decoded = '/';
    const p = decoded.length > 1 ? decoded.replace(/\/+$/, '') : decoded;
    const auth = req.headers.authorization ?? '';
    const expected = `Basic ${Buffer.from(`${user}:${pass}`).toString('base64')}`;

    requests.push({ method: req.method, path: p, auth, depth: req.headers.depth });

    if (customHandler) {
      if (customHandler({ req, res, path: p, url, requests })) return;
    }

    if (auth !== expected) {
      res.writeHead(401, { 'WWW-Authenticate': 'Basic realm="nutstore"' });
      res.end('unauthorized');
      return;
    }

    // 只在未带尾斜杠时重定向，避免无限循环。
    // 用 Host 头构造同源 Location —— 用 new URL(req.url, 'http://localhost')
    // 会得到 localhost，被客户端的跨源防护正确拦下。
    if (redirectPrefix && p.startsWith(redirectPrefix) && !url.pathname.endsWith('/')) {
      res.writeHead(302, { Location: `http://${req.headers.host}${url.pathname}/` });
      res.end();
      return;
    }

    switch (req.method) {
      case 'PROPFIND': {
        const node = tree.get(p);
        if (!node) { res.writeHead(404); res.end('not found'); return; }

        const depth = req.headers.depth === '0' ? 0 : 1;
        const targets = [p];
        if (depth === 1 && node.type === 'dir') {
          for (const key of [...tree.keys()].sort()) {
            if (key === p) continue;
            const parent = key.slice(0, key.lastIndexOf('/')) || '/';
            if (parent === p) targets.push(key);
          }
        }

        let selected = targets;
        let truncated = false;
        if (propfindLimit !== null && targets.length > propfindLimit) {
          selected = targets.slice(0, propfindLimit);
          truncated = true;
        }

        const body = [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<D:multistatus xmlns:D="DAV:">',
          ...selected.map((t) => {
            const n = tree.get(t);
            const isDir = n.type === 'dir';
            const href = t === '/' ? '/' : `${t.split('/').map(encodeURIComponent).join('/')}${isDir ? '/' : ''}`;
            return [
              '<D:response>',
              `<D:href>${esc(href)}</D:href>`,
              '<D:propstat>',
              '<D:prop>',
              isDir ? '<D:resourcetype><D:collection/></D:resourcetype>' : '<D:resourcetype/>',
              isDir ? '' : `<D:getcontentlength>${n.data?.length ?? 0}</D:getcontentlength>`,
              `<D:getlastmodified>${xmlDate()}</D:getlastmodified>`,
              `<D:getetag>"${t.length}-etag"</D:getetag>`,
              isDir ? '' : '<D:getcontenttype>application/octet-stream</D:getcontenttype>',
              '</D:prop>',
              '<D:status>HTTP/1.1 200 OK</D:status>',
              '</D:propstat>',
              '</D:response>',
            ].join('');
          }),
          '</D:multistatus>',
        ].join('');

        res.writeHead(207, {
          'Content-Type': 'application/xml; charset=utf-8',
          ...(truncated ? { 'X-Mock-Truncated': '1' } : {}),
        });
        res.end(body);
        return;
      }

      case 'HEAD': {
        const node = tree.get(p);
        if (!node) { res.writeHead(404); res.end(); return; }
        res.writeHead(200, {
          'Content-Length': String(node.type === 'dir' ? 0 : (node.data?.length ?? 0)),
          ETag: `"${p.length}-etag"`,
        });
        res.end();
        return;
      }

      case 'GET': {
        const node = tree.get(p);
        if (!node || node.type === 'dir') { res.writeHead(404); res.end('not found'); return; }
        res.writeHead(200, {
          'Content-Type': 'application/octet-stream',
          'Content-Length': String(node.data.length),
          ETag: `"${p.length}-etag"`,
        });
        res.end(node.data);
        return;
      }

      case 'PUT': {
        const chunks = [];
        req.on('data', (c) => chunks.push(c));
        req.on('end', () => {
          const existed = tree.has(p);
          tree.set(p, { type: 'file', data: Buffer.concat(chunks) });
          res.writeHead(existed ? 204 : 201);
          res.end();
        });
        return;
      }

      case 'MKCOL': {
        if (tree.has(p)) { res.writeHead(405); res.end('exists'); return; }
        const parent = p.slice(0, p.lastIndexOf('/')) || '/';
        if (!tree.has(parent)) { res.writeHead(409); res.end('no parent'); return; }
        tree.set(p, { type: 'dir' });
        res.writeHead(201);
        res.end();
        return;
      }

      case 'MOVE': {
        if (!tree.has(p)) { res.writeHead(404); res.end(); return; }
        const dest = new URL(req.headers.destination, `http://${req.headers.host}`).pathname;
        let destDecoded = decodeURIComponent(dest);
        if (basePath && destDecoded.startsWith(basePath)) destDecoded = destDecoded.slice(basePath.length);
        const destPath = destDecoded.replace(/\/+$/, '') || '/';
        const existed = tree.has(destPath);
        tree.set(destPath, tree.get(p));
        tree.delete(p);
        res.writeHead(existed ? 204 : 201);
        res.end();
        return;
      }

      case 'DELETE': {
        if (!tree.has(p)) { res.writeHead(404); res.end(); return; }
        tree.delete(p);
        res.writeHead(204);
        res.end();
        return;
      }

      default:
        res.writeHead(405);
        res.end('method not allowed');
    }
  });

  return {
    server,
    tree,
    requests,
    async listen() {
      await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
      const { port } = server.address();
      return `http://127.0.0.1:${port}/dav`;
    },
    async close() {
      await new Promise((resolve) => server.close(resolve));
    },
  };
}
