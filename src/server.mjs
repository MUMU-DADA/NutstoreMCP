#!/usr/bin/env node
/**
 * NutstoreMCP 入口。
 *
 *   node src/server.mjs            以 MCP stdio 服务运行
 *   node src/server.mjs --doctor   打印配置与连通性自检（人类可读，不启动协议）
 *   node src/server.mjs --help
 *
 * ⚠️ 走 MCP 模式时 stdout 只属于协议。所有诊断信息一律走 stderr。
 */

import { loadConfig } from './config.mjs';
import { LocalNutstoreData } from './local.mjs';
import { McpServer } from './mcp.mjs';
import { SlidingWindowLimiter } from './ratelimit.mjs';
import { buildTools } from './tools.mjs';
import { WebdavClient } from './webdav.mjs';

const NAME = 'nutstore-mcp';
const VERSION = '0.1.0';

const INSTRUCTIONS = `坚果云本地 MCP。

能力分两层，稳定性不同：

- L1 文件面（nutstore_list / stat / read_text / download / upload / mkdir / move / delete）
  经官方 WebDAV 接口读写云端文件。受两个硬限制约束：单次目录枚举 750 项、
  请求频率 600 次/30 分钟。因此**不要做递归全盘扫描**。
- L2 数据面（nutstore_sync_* / nutstore_settings）
  读本地客户端数据库，只读。可回答「哪些目录在同步」「某文件同步到哪一步」
  「有没有同步失败」。

安全约束：
1. 云端访问受 NUTSTORE_ROOTS 白名单限制，本机文件访问受 NUTSTORE_LOCAL_ROOTS 限制。
   路径不在白名单内会被直接拒绝 —— 这是刻意的，不要尝试绕过。
2. 只读模式（NUTSTORE_READ_ONLY=true）下所有写操作会被拒绝。
3. 「创建同步文件夹」「把远程目录挂载到本地」「修改客户端设置」属于 L3 控制面，
   尚未实现。请不要用文件操作去模拟它们 —— 那样只会造出云端有、本地没挂载的
   孤立目录（客户端会标记为 doNotSync）。`;

function log(...args) {
  if (process.env.NUTSTORE_DEBUG === '1') {
    process.stderr.write(`[nutstore-mcp] ${args.join(' ')}\n`);
  }
}

function buildRuntime(env = process.env) {
  const config = loadConfig(env);
  const limiter = new SlidingWindowLimiter({
    limit: config.rateLimit,
    windowMs: config.rateWindowMs,
  });
  const webdav = new WebdavClient({ config, limiter });
  const local = new LocalNutstoreData({ dataDir: config.dataDir });
  return { config, limiter, webdav, local };
}

async function doctor(env = process.env) {
  const out = [];
  out.push(`${NAME} v${VERSION} — 自检`);

  let config;
  try {
    config = loadConfig(env);
  } catch (err) {
    out.push(`❌ 配置错误：${err.message}`);
    if (err.hint) out.push(`   hint: ${err.hint}`);
    process.stdout.write(`${out.join('\n')}\n`);
    return 1;
  }

  out.push('');
  out.push('── 配置 ──');
  out.push(`  WebDAV 端点     ${config.host}`);
  out.push(`  凭据            ${config.hasCredentials ? `已配置（${config.user}）` : '未配置 → L1 不可用'}`);
  out.push(`  云端白名单      ${config.roots.length ? config.roots.join(', ') : '(空) → 拒绝一切云端访问'}`);
  out.push(`  本机白名单      ${config.localRoots.join(', ')}`);
  out.push(`  只读模式        ${config.readOnly ? '开启（写操作会被拒绝）' : '关闭（允许写操作）'}`);
  out.push(`  限流            ${config.rateLimit} 次 / ${Math.round(config.rateWindowMs / 60000)} 分钟`);
  out.push(`  上传上限        ${(config.maxUploadBytes / 1024 / 1024).toFixed(0)} MB`);
  out.push(`  .env            ${config.loadedFrom ?? '(未加载)'}`);

  out.push('');
  out.push('── L2 本地数据 ──');
  const local = new LocalNutstoreData({ dataDir: config.dataDir });
  if (!local.available) {
    out.push(`  ⚠️ 未找到本地数据库：${local.dbPath ?? '(数据目录未知)'}`);
    out.push('     只影响 L2 工具；L1 文件面不受影响。');
  } else {
    try {
      const s = local.summary();
      out.push(`  数据库          ${s.dbPath}`);
      out.push(`  同步文件夹      ${s.syncFolders} 个（另有无本地映射的云端目录 ${s.unmountedSandboxes} 个）`);
      out.push(`  受管对象        ${s.trackedObjects}（其中云端独有 ${s.cloudOnlyObjects}）`);
      out.push(`  受管字节        ${s.trackedBytes}`);
      out.push(`  同步错误        ${s.syncErrorCount}`);
      const folders = local.listSyncFolders();
      for (const f of folders.folders.slice(0, 20)) {
        out.push(`    · ${f.cloudPath}  →  ${f.localPath}  [${f.stats?.objects ?? '?'} 对象]`);
      }
      if (folders.folders.length > 20) out.push(`    … 另有 ${folders.folders.length - 20} 个`);
    } catch (err) {
      out.push(`  ❌ 读取失败：${err.message}`);
    } finally {
      local.close();
    }
  }

  out.push('');
  out.push('── L1 连通性 ──');
  if (!config.hasCredentials) {
    out.push('  ⏭  跳过（未配置凭据）');
  } else {
    const limiter = new SlidingWindowLimiter({
      limit: config.rateLimit,
      windowMs: config.rateWindowMs,
    });
    const webdav = new WebdavClient({ config, limiter });
    try {
      const h = await webdav.health();
      out.push(`  ✅ 认证成功：${h.authenticatedAs}`);
      if (h.quota?.availableBytes != null) {
        out.push(`  可用空间        ${(h.quota.availableBytes / 1024 / 1024 / 1024).toFixed(2)} GB`);
      }
      out.push(`  已用请求        ${h.rateLimit.used}/${h.rateLimit.limit}`);
    } catch (err) {
      out.push(`  ❌ ${err.message}`);
      if (err.hint) out.push(`     hint: ${err.hint}`);
    }
  }

  process.stdout.write(`${out.join('\n')}\n`);
  return 0;
}

function printHelp() {
  process.stdout.write(
    [
      `${NAME} v${VERSION}`,
      '',
      '用法：',
      '  node src/server.mjs             以 MCP stdio 服务运行',
      '  node src/server.mjs --doctor    配置与连通性自检',
      '  node src/server.mjs --help      显示本帮助',
      '',
      '配置见 .env.example 与 ARCHITECTURE.md。',
      '',
    ].join('\n'),
  );
}

async function main() {
  const argv = process.argv.slice(2);

  if (argv.includes('--help') || argv.includes('-h')) {
    printHelp();
    return 0;
  }
  if (argv.includes('--doctor')) {
    return doctor();
  }

  let runtime;
  try {
    runtime = buildRuntime();
  } catch (err) {
    // 配置错误不能走 stdout —— 会污染协议流
    process.stderr.write(`[nutstore-mcp] 配置错误：${err.message}\n`);
    process.exitCode = 2;
    return 2;
  }

  const { config, webdav, local } = runtime;
  log(`启动：host=${config.host} roots=${JSON.stringify(config.roots)} readOnly=${config.readOnly}`);
  if (!config.hasCredentials) {
    log('未配置 WebDAV 凭据 —— L1 工具会返回明确错误，L2 工具仍可用');
  }
  if (config.roots.length === 0) {
    log('云端白名单为空 —— 所有 L1 路径访问都会被拒绝（fail-closed）');
  }

  const server = new McpServer({
    name: NAME,
    version: VERSION,
    instructions: INSTRUCTIONS,
    log,
  });

  for (const tool of buildTools({ config, webdav, local })) {
    server.tool(tool);
  }
  log(`已注册 ${server.toolNames.length} 个工具：${server.toolNames.join(', ')}`);

  const shutdown = () => {
    local.close();
  };
  process.on('SIGINT', () => {
    shutdown();
    process.exit(0);
  });
  process.on('SIGTERM', () => {
    shutdown();
    process.exit(0);
  });

  try {
    await server.run();
  } finally {
    shutdown();
  }
  return 0;
}

const isDirectRun = process.argv[1]
  && import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href;

if (isDirectRun || process.argv[1]?.endsWith('server.mjs')) {
  main()
    .then((code) => {
      if (typeof code === 'number' && code !== 0) process.exitCode = code;
    })
    .catch((err) => {
      process.stderr.write(`[nutstore-mcp] 致命错误：${err?.stack ?? err}\n`);
      process.exitCode = 1;
    });
}

export { buildRuntime, doctor, main };
