/**
 * 配置加载与护栏参数。
 *
 * 配置来源优先级：进程环境变量 > 项目根目录的 `.env`（用 Node 内置的
 * `process.loadEnvFile` 读，零依赖）。**凭据永远不进代码、不进 git**。
 */

import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { ConfigError } from './errors.mjs';
import { normalizeRoots } from './paths.mjs';

const PROJECT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export const DEFAULT_HOST = 'https://dav.jianguoyun.com/dav';
export const DEFAULT_RATE_LIMIT = 600;
export const DEFAULT_RATE_WINDOW_MS = 30 * 60 * 1000;
export const DEFAULT_MAX_UPLOAD_BYTES = 500 * 1024 * 1024;
export const DEFAULT_TIMEOUT_MS = 60_000;

/** 尝试加载 .env，失败静默忽略（没有配置文件是完全正常的）。 */
export function loadEnvFile(envPath = path.join(PROJECT_ROOT, '.env')) {
  const target = existsSync(envPath) ? envPath : null;
  if (!target) return null;
  try {
    process.loadEnvFile(target);
    return target;
  } catch {
    return null;
  }
}

function readInt(env, key, fallback) {
  const raw = env[key];
  if (raw === undefined || raw === '') return fallback;
  const n = Number(raw);
  if (!Number.isFinite(n) || n <= 0) {
    throw new ConfigError(`${key} 必须是正数，收到 "${raw}"`);
  }
  return Math.floor(n);
}

function readBool(env, key, fallback) {
  const raw = env[key];
  if (raw === undefined || raw === '') return fallback;
  const v = String(raw).trim().toLowerCase();
  if (['1', 'true', 'yes', 'on'].includes(v)) return true;
  if (['0', 'false', 'no', 'off'].includes(v)) return false;
  throw new ConfigError(`${key} 必须是布尔值（true/false），收到 "${raw}"`);
}

/** 默认的本地坚果云数据目录。 */
export function defaultDataDir(env = process.env) {
  if (env.APPDATA) return path.join(env.APPDATA, 'Nutstore');
  return null;
}

/**
 * 读取配置。**不因为缺凭据而抛错** —— L2（本地只读）在没有 WebDAV
 * 凭据时依然应当可用，只是 L1 工具会返回明确的错误。
 */
export function loadConfig(env = process.env, { envPath } = {}) {
  const loadedFrom = loadEnvFile(envPath ?? (env.NUTSTORE_ENV_FILE || undefined));

  const user = env.NUTSTORE_USER?.trim() || null;
  const pass = env.NUTSTORE_PASS || null;
  const host = (env.NUTSTORE_HOST?.trim() || DEFAULT_HOST).replace(/\/+$/, '');

  let parsedHost;
  try {
    parsedHost = new URL(host);
  } catch {
    throw new ConfigError(`NUTSTORE_HOST 不是合法 URL：${host}`);
  }
  if (parsedHost.protocol !== 'https:' && parsedHost.hostname !== 'localhost' && parsedHost.hostname !== '127.0.0.1') {
    throw new ConfigError(
      `NUTSTORE_HOST 必须使用 https（本地测试可用 localhost）：${host}`,
      { hint: 'Basic 认证是明文传输的，走 http 会把应用密码暴露在链路上。' },
    );
  }

  const roots = normalizeRoots(
    (env.NUTSTORE_ROOTS ?? '').split(',').map((s) => s.trim()).filter(Boolean),
  );

  // 本机可读写目录白名单。上传的**来源**和下载的**目标**都必须落在其中 ——
  // 否则模型可以借「上传」把本机任意文件（比如密码本）传到云端。
  const defaultDownloadDir = path.join(PROJECT_ROOT, '.downloads');
  const localRoots = (env.NUTSTORE_LOCAL_ROOTS ?? '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => path.resolve(s));
  if (localRoots.length === 0) localRoots.push(defaultDownloadDir);

  return {
    host,
    user,
    pass,
    hasCredentials: Boolean(user && pass),
    roots,
    localRoots,
    readOnly: readBool(env, 'NUTSTORE_READ_ONLY', true),
    rateLimit: readInt(env, 'NUTSTORE_RATE_LIMIT', DEFAULT_RATE_LIMIT),
    rateWindowMs: readInt(env, 'NUTSTORE_RATE_WINDOW_MS', DEFAULT_RATE_WINDOW_MS),
    maxUploadBytes: readInt(env, 'NUTSTORE_MAX_UPLOAD_BYTES', DEFAULT_MAX_UPLOAD_BYTES),
    timeoutMs: readInt(env, 'NUTSTORE_TIMEOUT_MS', DEFAULT_TIMEOUT_MS),
    dataDir: env.NUTSTORE_DATA_DIR?.trim() || defaultDataDir(env),
    loadedFrom,
    projectRoot: PROJECT_ROOT,
  };
}
