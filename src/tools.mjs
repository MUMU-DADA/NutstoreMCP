/**
 * MCP 工具定义。
 *
 * 分两组：
 *   L1 文件面（`nutstore_*`）—— 走 WebDAV
 *   L2 数据面（`nutstore_sync_*` / `nutstore_settings`）—— 读本地库，只读
 *
 * 所有写操作先过 `#requireWritable`，再各自过白名单校验。
 */

import { mkdir as fsMkdir, stat as fsStat } from 'node:fs/promises';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createWriteStream } from 'node:fs';

import { ReadOnlyError, RemoteError } from './errors.mjs';
import { assertLocalPathAllowed, assertPathAllowed, normalizeCloudPath } from './paths.mjs';

const S = {
  cloudPath: {
    type: 'string',
    description: '云端绝对路径，从根开始，例如 "/我的坚果云/文档/a.md"。大小写敏感。',
  },
};

function ok(obj) {
  return obj;
}

export function buildTools({ config, webdav, local }) {
  /** 写操作闸门 —— 只读模式下一律拒绝。 */
  function requireWritable(action) {
    if (config.readOnly) {
      throw new ReadOnlyError(
        `当前处于只读模式，拒绝执行：${action}`,
        {
          hint: '如需写入，请显式设置 NUTSTORE_READ_ONLY=false 后重启服务。'
            + '建议只在需要时临时开启。',
        },
      );
    }
  }

  /**
   * 云端路径守卫（纵深防御）。
   *
   * WebdavClient 内部也有同样的校验，这里再做一次是刻意的：白名单是安全边界，
   * 不该只由一个组件把关。而且工具层自带守卫后，它的行为可以脱离真实网络被测试。
   */
  function guardCloud(input) {
    const cloudPath = normalizeCloudPath(input);
    assertPathAllowed(cloudPath, config.roots);
    return cloudPath;
  }

  /**
   * 解析本机路径：相对路径一律相对于**第一个白名单目录**，而不是进程 CWD。
   *
   * 这一条很关键：MCP 服务的 CWD 是宿主决定的（可能是任何地方），
   * 用 CWD 解析相对路径会让「上传 report.md」指到一个谁也说不清的文件。
   */
  function resolveLocalInput(input, label) {
    if (!input) {
      throw new RemoteError(`必须提供 ${label}`);
    }
    const candidate = path.isAbsolute(input) ? input : path.join(config.localRoots[0], input);
    return assertLocalPathAllowed(candidate, config.localRoots);
  }

  const tools = [];

  // ─────────────────────────── L1 文件面 ───────────────────────────

  tools.push({
    name: 'nutstore_health',
    title: '坚果云连通性与配额自检',
    description:
      '检查 WebDAV 连通性、账号身份、配额与本地限流余量，并报告本地同步库是否可用。'
      + '排查问题时先跑这个。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, openWorldHint: true },
    handler: async () => {
      const out = {
        config: {
          host: config.host,
          hasCredentials: config.hasCredentials,
          roots: config.roots,
          localRoots: config.localRoots,
          readOnly: config.readOnly,
          rateLimit: config.rateLimit,
          rateWindowMs: config.rateWindowMs,
        },
        local: local.available
          ? { available: true, dbPath: local.dbPath }
          : {
            available: false,
            dbPath: local.dbPath,
            hint: '未找到本地数据库；只影响 L2 工具，L1 文件面不受影响。',
          },
      };

      if (!config.hasCredentials) {
        out.webdav = {
          ok: false,
          reason: 'NO_CREDENTIALS',
          hint: '设置 NUTSTORE_USER / NUTSTORE_PASS（应用密码）后可用。',
        };
        return out;
      }

      try {
        out.webdav = { ok: true, ...(await webdav.health()) };
      } catch (err) {
        out.webdav = { ok: false, reason: err.code ?? 'ERROR', message: err.message, hint: err.hint };
      }
      return out;
    },
  });

  tools.push({
    name: 'nutstore_list',
    title: '列出云端目录',
    description:
      '列出云端某个目录下的文件与子目录（等价于 PROPFIND Depth:1）。'
      + `注意：坚果云单次枚举上限 750 项，超限时结果会标记 truncated=true。`,
    inputSchema: {
      type: 'object',
      properties: { path: S.cloudPath },
      required: ['path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, openWorldHint: true },
    handler: async ({ path: p }) => {
      const cloudPath = guardCloud(p);
      const result = await webdav.list(cloudPath);
      return ok({
        path: result.path,
        count: result.entries.length,
        truncated: result.truncated,
        ...(result.warning ? { warning: result.warning } : {}),
        entries: result.entries.map((e) => ({
          name: e.path.split('/').pop(),
          path: e.path,
          type: e.isCollection ? 'directory' : 'file',
          size: e.size,
          modified: e.lastModified,
        })),
      });
    },
  });

  tools.push({
    name: 'nutstore_stat',
    title: '查看云端文件/目录元信息',
    description: '获取单个路径的类型、大小、修改时间与 ETag。',
    inputSchema: {
      type: 'object',
      properties: { path: S.cloudPath },
      required: ['path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, openWorldHint: true },
    handler: async ({ path: p }) => ok(await webdav.stat(guardCloud(p))),
  });

  tools.push({
    name: 'nutstore_read_text',
    title: '读取云端文本文件',
    description:
      '直接读出文本内容（适合 .md/.txt/.json/.csv 等）。默认上限 1 MB —— '
      + '超过请改用 nutstore_download 保存到本地。',
    inputSchema: {
      type: 'object',
      properties: {
        path: S.cloudPath,
        maxBytes: { type: 'number', description: '读取上限，默认 1048576（1 MB）。' },
      },
      required: ['path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, openWorldHint: true },
    handler: async ({ path: p, maxBytes }) => {
      const cloudPath = guardCloud(p);
      const result = await webdav.readText(cloudPath, { maxBytes: maxBytes ?? 1024 * 1024 });
      return ok({ path: cloudPath, bytes: result.bytes, text: result.text });
    },
  });

  tools.push({
    name: 'nutstore_download',
    title: '下载云端文件到本机',
    description:
      `把云端文件流式下载到本机。目标路径必须在 NUTSTORE_LOCAL_ROOTS 之内`
      + `（当前：${config.localRoots.join(', ')}）；相对路径相对于第一个目录。`,
    inputSchema: {
      type: 'object',
      properties: {
        path: S.cloudPath,
        localPath: { type: 'string', description: '本机目标路径（绝对或相对于允许目录）。' },
      },
      required: ['path', 'localPath'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, openWorldHint: true },
    handler: async ({ path: p, localPath }) => {
      const cloudPath = guardCloud(p);
      const target = resolveLocalInput(localPath, 'localPath');

      let existed = false;
      try {
        existed = (await fsStat(target)).isFile();
      } catch {
        existed = false;
      }

      await fsMkdir(path.dirname(target), { recursive: true });
      const { stream, size, etag, lastModified } = await webdav.download(cloudPath);
      await pipeline(stream, createWriteStream(target));

      const written = (await fsStat(target)).size;
      return ok({
        cloudPath,
        localPath: target,
        bytes: written,
        declaredSize: size,
        etag,
        lastModified,
        overwritten: existed,
      });
    },
  });

  tools.push({
    name: 'nutstore_upload',
    title: '上传本机文件到云端',
    description:
      `把本机文件上传到云端（单文件 ≤ 500 MB）。源文件必须在 NUTSTORE_LOCAL_ROOTS 之内。`
      + `path 以 "/" 结尾时按目录处理并自动补上源文件名；否则视为完整目标路径。`,
    inputSchema: {
      type: 'object',
      properties: {
        localPath: { type: 'string', description: '本机源文件路径。' },
        path: { type: 'string', description: '云端目标路径。以 "/" 结尾表示目录，自动补文件名。' },
      },
      required: ['localPath', 'path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true },
    handler: async ({ localPath, path: p }) => {
      requireWritable('上传文件');
      const source = resolveLocalInput(localPath, 'localPath');
      // 只有明确以 "/" 结尾才当目录处理 —— 不做「看扩展名猜意图」的魔法，
      // 那会在上传无扩展名文件时把名字改掉。
      const cloudPath = String(p).endsWith('/')
        ? guardCloud(`${p}/${path.basename(source)}`)
        : guardCloud(p);
      const result = await webdav.uploadFile(cloudPath, source);
      return ok({ ...result, localPath: source });
    },
  });

  tools.push({
    name: 'nutstore_mkdir',
    title: '在云端创建目录',
    description: '创建目录；recursive=true 时逐级创建缺失的父目录。目录已存在不算错误。',
    inputSchema: {
      type: 'object',
      properties: {
        path: S.cloudPath,
        recursive: { type: 'boolean', description: '是否递归创建父目录，默认 false。' },
      },
      required: ['path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, openWorldHint: true },
    handler: async ({ path: p, recursive }) => {
      requireWritable('创建目录');
      const cloudPath = guardCloud(p);
      return ok(recursive ? await webdav.mkdirp(cloudPath) : await webdav.mkdir(cloudPath));
    },
  });

  tools.push({
    name: 'nutstore_move',
    title: '移动或重命名云端文件/目录',
    description: '把云端资源从 from 移动到 to（同目录内即重命名）。',
    inputSchema: {
      type: 'object',
      properties: {
        from: S.cloudPath,
        to: { type: 'string', description: '目标云端路径。' },
        overwrite: { type: 'boolean', description: '目标存在时是否覆盖，默认 false。' },
      },
      required: ['from', 'to'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true },
    handler: async ({ from, to, overwrite }) => {
      requireWritable('移动资源');
      return ok(await webdav.move(
        guardCloud(from),
        guardCloud(to),
        { overwrite: overwrite ?? false },
      ));
    },
  });

  tools.push({
    name: 'nutstore_delete',
    title: '删除云端文件/目录',
    description:
      '⚠️ 删除云端资源，目录会连同内容一起删除。客户端有回收站，但不要依赖它。',
    inputSchema: {
      type: 'object',
      properties: {
        path: S.cloudPath,
        confirm: {
          type: 'boolean',
          description: '必须显式传 true 才会执行 —— 防止模型误删。',
        },
      },
      required: ['path', 'confirm'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: false, destructiveHint: true, openWorldHint: true },
    handler: async ({ path: p, confirm }) => {
      requireWritable('删除资源');
      if (confirm !== true) {
        throw new RemoteError('删除操作需要显式确认', {
          hint: '请传入 confirm: true。这一步是刻意设置的：删除是不可逆的高风险操作。',
        });
      }
      return ok(await webdav.remove(guardCloud(p)));
    },
  });

  // ─────────────────────────── L2 数据面 ───────────────────────────

  tools.push({
    name: 'nutstore_sync_folders',
    title: '列出同步文件夹（云端 ↔ 本地映射）',
    description:
      '列出本机已挂载的同步文件夹：云端路径、本地镜像路径、对象数、云端独有对象数。'
      + '同时列出「云端存在但本地未挂载」的目录（unmountedSandboxes）—— '
      + '那正是「把远程目录同步到本地」的候选，但挂载动作属于 L3 控制面，尚未实现。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, openWorldHint: false },
    handler: async () => ok(local.listSyncFolders()),
  });

  tools.push({
    name: 'nutstore_sync_status',
    title: '本地同步总览',
    description:
      '一次性给出：同步文件夹数、受管对象数、云端独有对象数、同步错误数、客户端设置快照。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, openWorldHint: false },
    handler: async () => ok(local.summary()),
  });

  tools.push({
    name: 'nutstore_file_status',
    title: '查询单个路径的同步状态',
    description:
      '查这个云端路径在本地同步索引里的状态：是否已跟踪、版本、大小、修改时间、'
      + '是否为「不占用电脑空间」的云端占位符，以及对应的本地绝对路径。',
    inputSchema: {
      type: 'object',
      properties: { path: S.cloudPath },
      required: ['path'],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
    handler: async ({ path: p }) => ok(local.fileStatus(guardCloud(p))),
  });

  tools.push({
    name: 'nutstore_sync_errors',
    title: '列出同步失败项',
    description: '列出客户端记录的同步错误（文件名冲突、超限、特殊字符等）。',
    inputSchema: {
      type: 'object',
      properties: { limit: { type: 'number', description: '最多返回条数，默认 100。' } },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
    handler: async ({ limit }) => ok(local.getSyncErrors({ limit: limit ?? 100 })),
  });

  tools.push({
    name: 'nutstore_settings',
    title: '客户端设置快照（只读）',
    description:
      '读取客户端设置：账号、版本、云桥模式开关、缓存策略、流量统计。'
      + '修改设置属于 L3 控制面能力，尚未实现。'
      + '本工具只读取白名单字段，token / 密码哈希等敏感键在 SQL 层就被排除。',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true, openWorldHint: false },
    handler: async () => ok(local.getSettings()),
  });

  return tools;
}
