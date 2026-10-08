/**
 * L1 文件面 —— 零依赖 WebDAV 客户端。
 *
 * 只实现坚果云实际支持、且本项目需要的动词：
 *   PROPFIND / HEAD / GET / PUT / MKCOL / MOVE / COPY / DELETE
 *
 * 三个必须记住的官方限制（见 ARCHITECTURE.md 第 3 节）：
 *   1. 单次目录枚举最多 750 项 —— 超了会被静默截断，本模块**显式报告**而非假装完整
 *   2. 单文件上传 500 MB
 *   3. 600 次 / 30 分钟（免费）—— 每次请求都过限流器
 */

import { createReadStream } from 'node:fs';
import { stat as fsStat } from 'node:fs/promises';
import { Readable } from 'node:stream';

import { AuthError, NotFoundError, RemoteError } from './errors.mjs';
import { assertPathAllowed, hrefToCloudPath, toWebdavUrl } from './paths.mjs';
import { child, children, find, parseXml, text } from './xml.mjs';

/** 坚果云 WebDAV 单次目录枚举的硬上限。 */
export const LIST_LIMIT = 750;

const PROPFIND_BODY = `<?xml version="1.0" encoding="utf-8"?>
<D:propfind xmlns:D="DAV:">
  <D:prop>
    <D:resourcetype/>
    <D:getcontentlength/>
    <D:getlastmodified/>
    <D:getetag/>
    <D:getcontenttype/>
  </D:prop>
</D:propfind>`;

const QUOTA_BODY = `<?xml version="1.0" encoding="utf-8"?>
<D:propfind xmlns:D="DAV:">
  <D:prop>
    <D:quota-available-bytes/>
    <D:quota-used-bytes/>
  </D:prop>
</D:propfind>`;

const REDIRECT_STATUS = new Set([301, 302, 303, 307, 308]);
const MAX_REDIRECTS = 5;

function isReadableStream(v) {
  return typeof ReadableStream !== 'undefined' && v instanceof ReadableStream;
}

/**
 * 把各种「像流的东西」规范成 fetch 能吃的 BodyInit。
 *
 * **这个函数存在的唯一理由是防止静默数据损坏。** 实测踩过：把一个 generator
 * 直接交给 fetch，undici 不接受它，转成字符串后上传了字面量
 * `[object Generator]` —— 不报错、状态码 201、内容全错。
 * 所以这里是白名单式校验：认得就转换，不认得就抛错。
 */
export function toBodyInit(value) {
  if (value === null || value === undefined) return undefined;
  if (typeof value === 'string') return value;
  if (Buffer.isBuffer(value) || value instanceof Uint8Array) return value;
  if (isReadableStream(value)) return value;

  // Node 的 Readable（含 createReadStream 的产物）
  if (typeof value.pipe === 'function' && typeof value.on === 'function') {
    return Readable.toWeb(value);
  }
  // 同步/异步可迭代对象（generator、数组、Set…）
  if (typeof value?.[Symbol.asyncIterator] === 'function'
    || typeof value?.[Symbol.iterator] === 'function') {
    return Readable.toWeb(Readable.from(value));
  }

  throw new TypeError(
    'upload() 的 body 必须是 string / Buffer / Uint8Array / Readable / ReadableStream，'
    + `或返回上述之一的工厂函数；收到 ${Object.prototype.toString.call(value)}`,
  );
}

function num(value) {
  if (value === undefined || value === null || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function statusToError(status, method, cloudPath, detail) {
  const suffix = detail ? `：${detail}` : '';
  switch (status) {
    case 401:
      return new AuthError(
        `WebDAV 认证失败（401）${suffix}`,
        {
          hint: '请检查 NUTSTORE_USER / NUTSTORE_PASS。密码必须是网页端生成的'
            + '「应用密码」，不是登录密码。'
            + '生成步骤：https://help.jianguoyun.com/?p=2064',
        },
      );
    case 403:
      return new RemoteError(`无权限访问 "${cloudPath}"（403）${suffix}`, { status });
    case 404:
      return new NotFoundError(`远端不存在："${cloudPath}"（404）`, {
        hint: '路径大小写敏感，且必须从云端根开始写全。',
      });
    case 405:
      return new RemoteError(`方法 ${method} 不被支持（405）${suffix}`, {
        status,
        hint: method === 'MKCOL' ? '目标是已存在的目录，无需创建。' : null,
      });
    case 409:
      return new RemoteError(`冲突（409）${suffix}`, {
        status,
        hint: '通常是父目录不存在 —— 请先创建上级目录。',
      });
    case 423:
      return new RemoteError(`资源被锁定（423）${suffix}`, { status });
    case 429:
      return new RemoteError(`服务端限流（429）${suffix}`, {
        status,
        hint: '坚果云 WebDAV 上限为 600 次 / 30 分钟（免费版）。请降低请求频率。',
      });
    case 507:
      return new RemoteError(`存储空间或流量配额不足（507）${suffix}`, { status });
    default:
      return new RemoteError(`${method} ${cloudPath} 失败：HTTP ${status}${suffix}`, { status });
  }
}

/**
 * 解析 207 Multi-Status。
 *
 * 返回 `self`（被请求资源自身）与 `entries`（子项，Depth:1 时）。
 * `truncated` 表示结果**可能**被服务端截断 —— 我们无法确认分页机制，
 * 所以宁可显式告警，也不假装结果是完整的。
 */
export function parseMultiStatus(xmlText, baseUrl, requestPath) {
  const doc = parseXml(xmlText);
  const multistatus = doc.children.find((n) => n.name === 'multistatus')
    ?? find(doc, 'multistatus');
  if (!multistatus) {
    throw new RemoteError('响应不是合法的 WebDAV multistatus 文档');
  }

  const entries = [];
  let self = null;
  let responseCount = 0;

  for (const response of children(multistatus, 'response')) {
    const hrefNode = child(response, 'href');
    if (!hrefNode) continue;
    responseCount++; // 计数包含被跳过（非 2xx）的条目 —— 截断判断看的是响应条数

    let cloudPath;
    try {
      cloudPath = hrefToCloudPath(text(hrefNode), baseUrl);
    } catch {
      continue; // href 无法解析就跳过，不因此让整个列表失败
    }

    // 只接受 propstat 里状态为 2xx 的那个 prop 块。
    // 不能用 find() 兜底 —— 那会穿进 403 的 propstat，把一个被拒绝的条目
    // 当成有效结果混进列表（回归测试见 webdav.test.mjs）。
    let prop = null;
    for (const propstat of children(response, 'propstat')) {
      const statusLine = text(child(propstat, 'status'));
      if (/\b2\d\d\b/.test(statusLine)) {
        prop = child(propstat, 'prop');
        break;
      }
    }

    if (!prop) {
      // 极少数服务端把整体状态放在 response 层（此时无 prop 可取）
      const responseStatus = text(child(response, 'status'));
      if (/\b2\d\d\b/.test(responseStatus)) {
        const bare = {
          path: cloudPath,
          isCollection: false,
          size: null,
          lastModified: null,
          etag: null,
          contentType: null,
        };
        if (cloudPath === requestPath) self = bare;
        else entries.push(bare);
      }
      continue;
    }

    const resourcetype = child(prop, 'resourcetype');
    const isCollection = Boolean(
      resourcetype && (child(resourcetype, 'collection') || find(resourcetype, 'collection')),
    );

    const entry = {
      path: cloudPath,
      isCollection,
      size: num(text(child(prop, 'getcontentlength'))),
      lastModified: text(child(prop, 'getlastmodified')) || null,
      etag: text(child(prop, 'getetag')) || null,
      contentType: text(child(prop, 'getcontenttype')) || null,
    };

    if (cloudPath === requestPath) {
      self = entry;
    } else {
      entries.push(entry);
    }
  }

  entries.sort((a, b) => a.path.localeCompare(b.path, 'zh-Hans-CN'));

  return {
    self,
    entries,
    responseCount,
    // 坚果云的 750 项上限**含被请求的目录自身**，所以看的是响应条数而非子项数。
    truncated: responseCount >= LIST_LIMIT,
  };
}

export class WebdavClient {
  #config;
  #limiter;
  #fetch;
  #authHeader;

  constructor({ config, limiter, fetchImpl = globalThis.fetch }) {
    if (typeof fetchImpl !== 'function') {
      throw new TypeError('需要 fetch 实现（Node 18+ 自带）');
    }
    this.#config = config;
    this.#limiter = limiter;
    this.#fetch = fetchImpl;
    this.#authHeader = config.hasCredentials
      ? `Basic ${Buffer.from(`${config.user}:${config.pass}`).toString('base64')}`
      : null;
  }

  get baseUrl() {
    return this.#config.host;
  }

  /** 凭据缺失时给出可操作的错误，而不是发一个注定 401 的请求。 */
  #requireCredentials() {
    if (!this.#authHeader) {
      throw new AuthError('未配置 WebDAV 凭据，L1 文件面不可用', {
        hint: '请设置 NUTSTORE_USER 与 NUTSTORE_PASS（应用密码）。'
          + '只使用 L2 本地只读能力时无需凭据。',
      });
    }
  }

  #guard(cloudPath) {
    assertPathAllowed(cloudPath, this.#config.roots);
  }

  /**
   * 发一次带认证的请求，手动处理重定向以便**保留方法**。
   *
   * 拒绝跨源重定向：Basic 凭据是明文，跟着跳到别的域等于泄露应用密码。
   */
  async #send(method, cloudPath, { headers = {}, body, timeoutMs } = {}) {
    this.#requireCredentials();
    this.#guard(cloudPath);

    const url = toWebdavUrl(this.baseUrl, cloudPath);
    const origin = new URL(url).origin;
    const signal = AbortSignal.timeout(timeoutMs ?? this.#config.timeoutMs);

    let current = url;
    let hops = 0;

    for (;;) {
      this.#limiter.acquire();

      const produced = typeof body === 'function' ? body() : body;
      const payload = toBodyInit(produced);

      const res = await this.#fetch(current, {
        method,
        headers: {
          Authorization: this.#authHeader,
          'User-Agent': 'nutstore-mcp/0.1 (+local)',
          ...headers,
        },
        body: payload,
        redirect: 'manual',
        signal,
        ...(isReadableStream(payload) ? { duplex: 'half' } : {}),
      });

      if (!REDIRECT_STATUS.has(res.status)) return { res, finalUrl: current };

      const location = res.headers.get('location');
      if (!location) {
        throw new RemoteError(`收到 ${res.status} 重定向但缺少 Location 头`, { status: res.status });
      }
      // 丢弃未读的响应体，释放连接
      await res.body?.cancel().catch(() => {});

      const next = new URL(location, current);
      if (next.origin !== origin) {
        throw new RemoteError(
          `拒绝跟随跨源重定向：${origin} → ${next.origin}`,
          { hint: 'Basic 凭据会随重定向泄露到第三方域，已主动中止。' },
        );
      }
      if (++hops > MAX_REDIRECTS) {
        throw new RemoteError(`重定向次数超过 ${MAX_REDIRECTS} 次`);
      }
      // 工厂形式可重放，下一轮会重新创建流；已消费的流则会在下一轮自行失败
      current = next.toString();
    }
  }

  async #errorFrom(res, method, cloudPath) {
    let detail = '';
    try {
      const raw = await res.text();
      detail = raw.replace(/\s+/g, ' ').trim().slice(0, 200);
    } catch {
      /* 读不到就算了 */
    }
    return statusToError(res.status, method, cloudPath, detail);
  }

  /** PROPFIND。`depth` 为 0 或 1。 */
  async propfind(cloudPath, depth = 1) {
    const { res } = await this.#send('PROPFIND', cloudPath, {
      headers: { Depth: String(depth), 'Content-Type': 'application/xml; charset=utf-8' },
      body: PROPFIND_BODY,
    });

    if (res.status === 207) {
      return parseMultiStatus(await res.text(), this.baseUrl, cloudPath);
    }
    throw await this.#errorFrom(res, 'PROPFIND', cloudPath);
  }

  /** 列目录。返回 `{ path, entries, truncated }`。 */
  async list(cloudPath) {
    const result = await this.propfind(cloudPath, 1);
    return {
      path: cloudPath,
      entries: result.entries,
      truncated: result.truncated,
      ...(result.truncated
        ? {
          warning: `返回条目达到 ${LIST_LIMIT} 条上限，结果可能不完整。`
            + '坚果云 WebDAV 单次枚举上限为 750 项；请改用更具体的子目录重新列举。',
        }
        : {}),
    };
  }

  /** 取单个资源的元信息。 */
  async stat(cloudPath) {
    const result = await this.propfind(cloudPath, 0);
    if (!result.self) {
      throw new NotFoundError(`远端不存在："${cloudPath}"`);
    }
    return result.self;
  }

  /** HEAD —— 比 PROPFIND 便宜，用于存在性检查。 */
  async exists(cloudPath) {
    const { res } = await this.#send('HEAD', cloudPath);
    await res.body?.cancel().catch(() => {});
    if (res.status === 200 || res.status === 204) return true;
    if (res.status === 404 || res.status === 403) return false;
    throw await this.#errorFrom(res, 'HEAD', cloudPath);
  }

  /**
   * 诊断探针的落点。
   *
   * 刻意**不用**云端根 `/` —— 那会绕过 NUTSTORE_ROOTS 白名单。
   * 白名单是安全边界，诊断工具没有理由成为例外；配额属性在任意资源上
   * 查询都会返回账号级数值。
   */
  #probePath() {
    return this.#config.roots[0] ?? null;
  }

  /** 查询配额。服务端不支持时返回 null，而不是报错。 */
  async quota() {
    const path = this.#probePath();
    if (!path) return null;
    const { res } = await this.#send('PROPFIND', path, {
      headers: { Depth: '0', 'Content-Type': 'application/xml; charset=utf-8' },
      body: QUOTA_BODY,
    });
    if (res.status !== 207) return null;
    const doc = parseXml(await res.text());
    const available = find(doc, 'quota-available-bytes');
    const used = find(doc, 'quota-used-bytes');
    if (!available && !used) return null;
    return {
      availableBytes: num(text(available)),
      usedBytes: num(text(used)),
    };
  }

  /** 下载为流。调用方负责消费或取消。 */
  async download(cloudPath, { timeoutMs } = {}) {
    const { res } = await this.#send('GET', cloudPath, { timeoutMs: timeoutMs ?? 5 * 60_000 });
    if (res.status === 200) {
      return {
        stream: res.body,
        size: num(res.headers.get('content-length')),
        etag: res.headers.get('etag'),
        lastModified: res.headers.get('last-modified'),
        contentType: res.headers.get('content-type'),
      };
    }
    await res.body?.cancel().catch(() => {});
    throw await this.#errorFrom(res, 'GET', cloudPath);
  }

  /**
   * 读小文本文件。默认上限 1 MB —— 再大就不该塞进模型上下文了。
   */
  async readText(cloudPath, { maxBytes = 1024 * 1024, encoding = 'utf8' } = {}) {
    const { res } = await this.#send('GET', cloudPath);
    if (res.status !== 200) {
      await res.body?.cancel().catch(() => {});
      throw await this.#errorFrom(res, 'GET', cloudPath);
    }
    const declared = num(res.headers.get('content-length'));
    if (declared !== null && declared > maxBytes) {
      await res.body?.cancel().catch(() => {});
      throw new RemoteError(
        `文件大小 ${declared} 字节，超过读取上限 ${maxBytes} 字节`,
        { hint: `如需完整文件请改用下载工具保存到本地。上限可用 maxBytes 参数调整。` },
      );
    }
    const buf = Buffer.from(await res.arrayBuffer());
    if (buf.byteLength > maxBytes) {
      throw new RemoteError(`文件超过读取上限 ${maxBytes} 字节`);
    }
    return { text: buf.toString(encoding), bytes: buf.byteLength };
  }

  /** 上传。`body` 可为 string / Buffer / `() => Readable`（工厂，可重放）。 */
  async upload(cloudPath, body, { contentLength, contentType } = {}) {
    const size = contentLength ?? (typeof body === 'string'
      ? Buffer.byteLength(body)
      : Buffer.isBuffer(body) || body instanceof Uint8Array
        ? body.byteLength
        : null);

    if (size !== null && size > this.#config.maxUploadBytes) {
      throw new RemoteError(
        `文件大小 ${size} 字节超过上传上限 ${this.#config.maxUploadBytes} 字节`,
        {
          hint: '坚果云 WebDAV 单文件上传限制为 500 MB。'
            + '更大的文件请放入本地同步目录，由客户端同步。',
        },
      );
    }

    const { res } = await this.#send('PUT', cloudPath, {
      headers: {
        ...(contentType ? { 'Content-Type': contentType } : {}),
        ...(size !== null ? { 'Content-Length': String(size) } : {}),
      },
      body,
      timeoutMs: 10 * 60_000,
    });

    await res.body?.cancel().catch(() => {});
    if (res.status === 201 || res.status === 204 || res.status === 200) {
      return {
        path: cloudPath,
        bytes: size,
        // 未知长度时走 chunked 传输 —— 部分服务端会拒绝，调用方需知情
        sizeKnown: size !== null,
        created: res.status === 201,
      };
    }
    throw await this.#errorFrom(res, 'PUT', cloudPath);
  }

  /** 上传本地文件（流式，不进内存）。 */
  async uploadFile(cloudPath, localPath, { contentType } = {}) {
    const info = await fsStat(localPath);
    if (!info.isFile()) {
      throw new RemoteError(`"${localPath}" 不是普通文件`);
    }
    return this.upload(cloudPath, () => createReadStream(localPath), {
      contentLength: info.size,
      contentType,
    });
  }

  /** 建目录。已存在时返回 `created: false`，不算错误。 */
  async mkdir(cloudPath) {
    const { res } = await this.#send('MKCOL', cloudPath);
    await res.body?.cancel().catch(() => {});
    if (res.status === 201) return { path: cloudPath, created: true };
    if (res.status === 405) return { path: cloudPath, created: false, existed: true };
    throw await this.#errorFrom(res, 'MKCOL', cloudPath);
  }

  /** 递归建目录（逐级 MKCOL）。 */
  async mkdirp(cloudPath) {
    if (cloudPath === '/') return { path: '/', created: false, existed: true };
    const segments = cloudPath.split('/').slice(1);
    const made = [];
    let current = '';
    for (const seg of segments) {
      current += `/${seg}`;
      const r = await this.mkdir(current);
      if (r.created) made.push(current);
    }
    return { path: cloudPath, created: made.length > 0, createdPaths: made };
  }

  async #resourceOp(method, from, to, { overwrite = true } = {}) {
    const { res } = await this.#send(method, from, {
      headers: {
        Destination: toWebdavUrl(this.baseUrl, to),
        Overwrite: overwrite ? 'T' : 'F',
      },
    });
    await res.body?.cancel().catch(() => {});
    if (res.status === 201 || res.status === 204) {
      return { from, to, overwritten: res.status === 204 };
    }
    throw await this.#errorFrom(res, method, from);
  }

  /**
   * 移动 / 重命名。
   *
   * 声明为 async 是刻意的：目标路径的守卫若同步抛错，调用方用
   * `assert.rejects` / `.catch()` 都接不住（Node 把同步调用放在 try 之外），
   * 异步 API 必须一律以 reject 表达失败。
   */
  async move(from, to, opts) {
    this.#guard(to);
    return this.#resourceOp('MOVE', from, to, opts);
  }

  /** 复制。理由同 `move`。 */
  async copy(from, to, opts) {
    this.#guard(to);
    return this.#resourceOp('COPY', from, to, opts);
  }

  /** 删除。 */
  async remove(cloudPath) {
    const { res } = await this.#send('DELETE', cloudPath);
    await res.body?.cancel().catch(() => {});
    if (res.status === 204 || res.status === 200) return { path: cloudPath, deleted: true };
    if (res.status === 404) return { path: cloudPath, deleted: false, existed: false };
    throw await this.#errorFrom(res, 'DELETE', cloudPath);
  }

  /** 连通性与配额自检。 */
  async health() {
    const probe = this.#probePath();
    if (!probe) {
      throw new RemoteError('云端白名单为空，没有可探测的路径', {
        hint: '请设置 NUTSTORE_ROOTS，例如 NUTSTORE_ROOTS=我的坚果云',
      });
    }
    const result = await this.propfind(probe, 0);
    let quota = null;
    try {
      quota = await this.quota();
    } catch {
      quota = null; // 服务端不支持配额查询不算故障
    }
    return {
      baseUrl: this.baseUrl,
      authenticatedAs: this.#config.user,
      probedPath: probe,
      resource: result.self,
      quota,
      rateLimit: this.#limiter.snapshot(),
      roots: this.#config.roots,
      readOnly: this.#config.readOnly,
    };
  }
}
