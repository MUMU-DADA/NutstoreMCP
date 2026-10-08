/**
 * 云端路径的规范化、编码与白名单校验。
 *
 * ## 语义约定
 *
 * 调用方传入的路径一律按**字面量**解释，不做百分号解码。
 * 这一点是安全性的关键：如果输入 `/a/%2e%2e/b`，`%2e%2e` 被当作一个普通
 * 目录名，随后被 `encodeURIComponent` 编成 `%252e%252e` —— 服务端看到的是
 * 字面字符串，而不是 `..`。所以「先查后解码」的经典绕过在这里不成立。
 *
 * 反过来，服务端返回的 `href` 是 URL 编码的，必须解码后才能当路径用 ——
 * 见 `hrefToCloudPath`。
 */

import path from 'node:path';

import { PathNotAllowedError } from './errors.mjs';

const CONTROL_CHARS = /[\u0000-\u001f\u007f]/;

/** 判断一个规范化后的路径是否就是根。 */
export function isRoot(cloudPath) {
  return cloudPath === '/';
}

/**
 * 把任意输入规范成 `/a/b` 形式。
 *
 * 拒绝：控制字符、反斜杠、`.` / `..` 片段。
 * 折叠：重复斜杠、去掉尾部斜杠（根除外）。
 */
export function normalizeCloudPath(input) {
  if (typeof input !== 'string') {
    throw new PathNotAllowedError(`路径必须是字符串，收到 ${typeof input}`);
  }
  if (CONTROL_CHARS.test(input)) {
    throw new PathNotAllowedError('路径包含控制字符');
  }
  if (input.includes('\\')) {
    throw new PathNotAllowedError(
      '路径不能包含反斜杠 —— 云端路径一律使用正斜杠',
      { hint: '把 C:\\foo 改写成 /foo' },
    );
  }

  let p = input.replace(/\/+/g, '/');
  if (!p.startsWith('/')) p = `/${p}`;
  if (p.length > 1) p = p.replace(/\/+$/, '');
  if (p === '') p = '/';

  for (const seg of p.split('/').slice(1)) {
    if (seg === '.' || seg === '..') {
      throw new PathNotAllowedError(
        `路径不能包含 "${seg}" 片段`,
        { hint: '相对路径不受支持，请给出从云端根开始的完整路径' },
      );
    }
  }
  return p;
}

/** 按段做百分号编码，保留 `/` 分隔符。 */
export function encodeCloudPath(cloudPath) {
  return cloudPath
    .split('/')
    .map((seg) => encodeURIComponent(seg))
    .join('/');
}

/** 拼接完整 WebDAV URL。`baseUrl` 形如 `https://dav.jianguoyun.com/dav`。 */
export function toWebdavUrl(baseUrl, cloudPath) {
  const base = baseUrl.replace(/\/+$/, '');
  const encoded = encodeCloudPath(cloudPath);
  return encoded === '/' ? `${base}/` : `${base}${encoded}`;
}

/**
 * 把响应里的 `href` 还原成云端路径。
 *
 * `href` 可能是绝对 URL、绝对路径，或相对路径；一律：
 * 去 query/fragment → 百分号解码 → 规范化 → 确认落在 baseUrl 之下。
 */
export function hrefToCloudPath(href, baseUrl) {
  if (typeof href !== 'string' || href === '') {
    throw new PathNotAllowedError('响应中的 href 为空');
  }
  let raw = href.split('#')[0].split('?')[0];

  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) {
    const parsed = new URL(raw);
    raw = parsed.pathname;
  }

  let decoded;
  try {
    decoded = decodeURIComponent(raw);
  } catch {
    decoded = raw;
  }

  const basePath = new URL(baseUrl).pathname.replace(/\/+$/, '');
  if (basePath && decoded.startsWith(basePath)) {
    decoded = decoded.slice(basePath.length);
  }
  return normalizeCloudPath(decoded || '/');
}

/**
 * 白名单校验。
 *
 * **空白名单 = 拒绝一切**（fail-closed）。这是刻意的：宁可让用户配一次，
 * 也不能因为忘配而把整个云盘连同里面的凭据文件暴露给模型。
 */
export function isPathAllowed(cloudPath, roots) {
  if (!Array.isArray(roots) || roots.length === 0) return false;

  for (const r of roots) {
    let root;
    try {
      root = normalizeCloudPath(r);
    } catch {
      continue;
    }
    if (root === '/') return true;
    if (cloudPath === root) return true;
    if (cloudPath.startsWith(`${root}/`)) return true;
  }
  return false;
}

/** 校验失败时抛出，消息里带上白名单，方便调用方自行修正。 */
export function assertPathAllowed(cloudPath, roots) {
  if (isPathAllowed(cloudPath, roots)) return cloudPath;

  const shown = Array.isArray(roots) && roots.length > 0
    ? roots.join(', ')
    : '(空 —— 当前拒绝一切访问)';

  throw new PathNotAllowedError(
    `路径 "${cloudPath}" 不在允许的白名单内`,
    {
      hint: `当前白名单：${shown}。如需访问请调整 NUTSTORE_ROOTS。`,
    },
  );
}

/**
 * 本机路径白名单校验 —— 用于上传来源与下载目标。
 *
 * 存在的理由：如果没有这层，「上传本地文件到云端」就变成了一条
 * 任意文件读取 + 外传通道。模型只要被注入一句指令，就能把
 * `C:\Users\...\邮箱密码.txt` 传到云端。
 *
 * 用 `path.relative` 判断包含关系，避免 `C:\a-evil` 被 `C:\a` 前缀匹配。
 */
export function assertLocalPathAllowed(localPath, localRoots) {
  const resolved = path.resolve(localPath);
  if (!Array.isArray(localRoots) || localRoots.length === 0) {
    throw new PathNotAllowedError('未配置本机可访问目录，拒绝一切本地文件访问', {
      hint: '请设置 NUTSTORE_LOCAL_ROOTS。',
    });
  }
  for (const root of localRoots) {
    const rel = path.relative(path.resolve(root), resolved);
    if (rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel))) {
      return resolved;
    }
  }
  throw new PathNotAllowedError(
    `本机路径 "${resolved}" 不在允许的目录内`,
    { hint: `当前允许：${localRoots.join(', ')}。如需其他位置请调整 NUTSTORE_LOCAL_ROOTS。` },
  );
}

/** 规范化白名单条目，丢弃非法项。 */
export function normalizeRoots(rawRoots) {
  const list = Array.isArray(rawRoots) ? rawRoots : [];
  const out = [];
  for (const r of list) {
    if (typeof r !== 'string') continue; // 跳过 null / 数字等非字符串条目
    const s = r.trim();
    if (!s) continue;
    try {
      const norm = normalizeCloudPath(s);
      if (!out.includes(norm)) out.push(norm);
    } catch {
      // 非法白名单条目直接忽略，不阻断启动
    }
  }
  return out;
}
