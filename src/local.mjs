/**
 * L2 数据面 —— 坚果云本地库的只读视图。
 *
 * ## 安全约定（见 ARCHITECTURE.md 红线 3）
 *
 * 这个模块**从不把敏感值读进内存**，而不是「读了再过滤」。
 * 做法是：config 表只按白名单 `WHERE key IN (...)` 查询，
 * `encrypted_token` / `device_token` / `bcrypt_pwd_hash` 这类键
 * 根本不会出现在 SQL 里。白名单之外再加一层黑名单正则兜底。
 *
 * ## 只读
 *
 * 用 `node:sqlite` 的 `readOnly` 打开，物理上无法写入。
 * 客户端正在运行时也能安全并发读（WAL 模式支持多读者）。
 *
 * ## 已知的格式风险
 *
 * `nutstore.db` 是未公开格式，随版本可能变。所以这里所有查询都写成
 * 「读不到就降级」，不假设表一定存在、字段一定齐全。
 */

import { existsSync } from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';

import { LocalDataError } from './errors.mjs';

/**
 * 允许读取的 config 键白名单。
 *
 * **新增键必须显式加到这里** —— 这是默认拒绝，不是默认允许。
 * 特别注意不要加入任何 token / hash / secret 类字段。
 */
export const SAFE_CONFIG_KEYS = Object.freeze([
  'app_version',
  'user_name',
  'user_nick',
  'login_date',
  'cloud_file_enabled',
  'cache_max_usage_percent',
  'cache_remain_days',
  'is_placeholder_auto_cleanup',
  'latest_upload_file_date',
  'never_prompt',
  'prompt_times',
  'traffic_history',
  'ignore_traffic_exhausted_popup',
  'ignore_traffic_usage_near_quota_popup',
  'has_new_app_recommend',
  'pinned_sandbox_id_set',
  'notified_unreadable_sandbox_id_list',
]);

/** 兜底黑名单：即使有人误把敏感键加进白名单，也不会被输出。 */
const SECRET_KEY_PATTERN = /(token|pwd|passw|secret|hash|salt|cipher|credential)/i;

/** sandbox 条目里允许输出的字段。`lanSyncCipher` 是局域网同步密钥，必须剔除。 */
const SAFE_SANDBOX_FIELDS = Object.freeze([
  'sandboxId',
  'name',
  'desc',
  'isDefault',
  'isOwner',
  'ownerNick',
  'usedSpace',
  'canSync',
  'doNotSync',
  'decoupled',
  'caps',
]);

/**
 * 同步错误原因码 → 符号名。
 *
 * 这些符号名来自客户端程序集里的常量（`SYNC_ERROR_REASON_*`），
 * 但**数值映射未经官方确认** —— 所以输出里保留原始 code，符号名标注为推测。
 */
const SYNC_ERROR_REASON_HINTS = Object.freeze({
  0: 'UNKNOWN',
  1: 'LIMIT_UPLOAD_SIZE',
  2: 'MAYBE_VIRUS',
  3: 'RESERVED_NAME',
  4: 'SPECIAL_CHARACTER',
});

const HEX_TABLE_SUFFIX = /^[0-9a-f]{16,64}$/;

function quoteIdent(name) {
  return `"${String(name).replace(/"/g, '""')}"`;
}

function toNumberOrNull(v) {
  if (v === null || v === undefined || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

export class LocalNutstoreData {
  #dataDir;
  #dbPath;
  #db = null;

  constructor({ dataDir }) {
    this.#dataDir = dataDir ?? null;
    this.#dbPath = dataDir ? path.join(dataDir, 'db1', 'nutstore.db') : null;
  }

  get dbPath() {
    return this.#dbPath;
  }

  get available() {
    return Boolean(this.#dbPath && existsSync(this.#dbPath));
  }

  #open() {
    if (this.#db) return this.#db;
    if (!this.#dataDir) {
      throw new LocalDataError('无法定位坚果云数据目录', {
        hint: '请设置 NUTSTORE_DATA_DIR 指向坚果云的配置目录（Windows 下通常是 %APPDATA%\\Nutstore）。',
      });
    }
    if (!existsSync(this.#dbPath)) {
      throw new LocalDataError(`本地数据库不存在：${this.#dbPath}`, {
        hint: '确认坚果云客户端已安装并至少登录过一次。若只使用 L1 文件面，可忽略此错误。',
      });
    }
    try {
      this.#db = new DatabaseSync(this.#dbPath, { readOnly: true });
    } catch (cause) {
      throw new LocalDataError(
        `打开本地数据库失败：${this.#dbPath}`,
        {
          hint: '数据库可能正被客户端独占。WAL 模式下并发只读通常是安全的；'
            + '若持续失败，请确认文件权限。',
          cause,
        },
      );
    }
    return this.#db;
  }

  close() {
    if (this.#db) {
      try {
        this.#db.close();
      } catch {
        /* 关闭失败无需上报 */
      }
      this.#db = null;
    }
  }

  /**
   * 仅供测试：拿到底层连接以验证它确实是只读的。
   * 生产代码不要用它 —— 它绕过了本模块所有的白名单与结构假设。
   */
  _connectionForTests() {
    return this.#open();
  }

  #tables(pattern) {
    const db = this.#open();
    try {
      return db
        .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE ?")
        .all(pattern)
        .map((r) => r.name);
    } catch {
      return [];
    }
  }

  /** config 表：只取白名单内的键。 */
  #safeConfigMap() {
    const db = this.#open();
    const keys = SAFE_CONFIG_KEYS.filter((k) => !SECRET_KEY_PATTERN.test(k));
    if (keys.length === 0) return {};
    const placeholders = keys.map(() => '?').join(',');
    let rows = [];
    try {
      rows = db
        .prepare(`SELECT key, value FROM config WHERE key IN (${placeholders})`)
        .all(...keys);
    } catch {
      return {};
    }
    const out = {};
    for (const { key, value } of rows) {
      if (SECRET_KEY_PATTERN.test(key)) continue; // 双保险
      out[key] = value;
    }
    return out;
  }

  /** 云端 sandbox 列表（已剔除密钥字段）。 */
  #sandboxList() {
    const db = this.#open();
    let raw;
    try {
      raw = db.prepare("SELECT value FROM config WHERE key = 'all_sandbox_list'").get();
    } catch {
      return [];
    }
    if (!raw?.value) return [];
    let parsed;
    try {
      parsed = JSON.parse(raw.value);
    } catch {
      return [];
    }
    if (!Array.isArray(parsed)) return [];
    return parsed.map((s) => {
      const out = {};
      for (const f of SAFE_SANDBOX_FIELDS) {
        if (f in s) out[f] = s[f];
      }
      return out;
    });
  }

  /** 本地 `sndconfig<hash>` 表 → sandbox 身份信息。 */
  #localSandboxes() {
    const db = this.#open();
    const out = [];
    for (const table of this.#tables('sndconfig%')) {
      const suffix = table.slice('sndconfig'.length);
      if (!HEX_TABLE_SUFFIX.test(suffix)) continue;
      let row;
      try {
        row = db.prepare(`SELECT value FROM ${quoteIdent(table)} WHERE key = 'sandbox_name'`).get();
      } catch {
        continue;
      }
      if (!row?.value) continue;
      let info;
      try {
        info = JSON.parse(row.value);
      } catch {
        continue;
      }
      // 只取路径与 id，magic 是内部标识不外泄
      out.push({
        suffix,
        sandboxId: toNumberOrNull(info.id),
        localPath: typeof info.localAbsPath === 'string' ? info.localAbsPath : null,
        configTable: table,
        objectTable: `sndobject${suffix}`,
      });
    }
    return out;
  }

  /** 某个 sandbox 的对象统计。 */
  #objectStats(objectTable) {
    const db = this.#open();
    try {
      const row = db
        .prepare(
          `SELECT
             COUNT(*) AS total,
             COALESCE(SUM(CASE WHEN dir = 1 THEN 1 ELSE 0 END), 0) AS dirs,
             COALESCE(SUM(CASE WHEN dir = 1 THEN 0 ELSE 1 END), 0) AS files,
             COALESCE(SUM(CASE WHEN unpopulated = 1 THEN 1 ELSE 0 END), 0) AS cloudOnly,
             COALESCE(SUM(CAST(size AS INTEGER)), 0) AS totalBytes
           FROM ${quoteIdent(objectTable)}`,
        )
        .get();
      return {
        objects: Number(row?.total ?? 0),
        directories: Number(row?.dirs ?? 0),
        files: Number(row?.files ?? 0),
        cloudOnly: Number(row?.cloudOnly ?? 0),
        trackedBytes: Number(row?.totalBytes ?? 0),
      };
    } catch {
      return null; // 表不存在或结构变了
    }
  }

  /**
   * 同步文件夹清单：云端 sandbox ⨝ 本地镜像路径 ⨝ 对象统计。
   *
   * `cloudPathInferred` 为 true 表示云端路径是从本地目录名推出来的 ——
   * 默认 sandbox 在 `all_sandbox_list` 里 `name` 为空，只能这样推。
   */
  listSyncFolders() {
    const cloud = this.#sandboxList();
    const cloudById = new Map(cloud.map((s) => [s.sandboxId, s]));
    const local = this.#localSandboxes();

    const folders = local.map((l) => {
      const meta = cloudById.get(l.sandboxId) ?? {};
      const localName = l.localPath ? path.basename(l.localPath) : null;
      const displayName = meta.name || localName || `(sandbox ${l.sandboxId})`;
      return {
        sandboxId: l.sandboxId,
        name: displayName,
        cloudPath: `/${displayName}`,
        cloudPathInferred: !meta.name,
        localPath: l.localPath,
        isDefault: meta.isDefault ?? false,
        isOwner: meta.isOwner ?? null,
        ownerNick: meta.ownerNick ?? null,
        usedSpace: toNumberOrNull(meta.usedSpace),
        doNotSync: meta.doNotSync ?? null,
        decoupled: meta.decoupled ?? null,
        description: meta.desc || null,
        stats: this.#objectStats(l.objectTable),
      };
    });

    // 云端有、本地未挂载的 sandbox —— 这正是「远程目录同步到本地」的候选
    const mounted = new Set(local.map((l) => l.sandboxId));
    const unmounted = cloud
      .filter((s) => !mounted.has(s.sandboxId))
      .map((s) => ({
        sandboxId: s.sandboxId,
        name: s.name || '(未命名)',
        usedSpace: toNumberOrNull(s.usedSpace),
        doNotSync: s.doNotSync ?? null,
        description: s.desc || null,
      }));

    folders.sort((a, b) => (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0)
      || a.name.localeCompare(b.name, 'zh-Hans-CN'));

    return {
      count: folders.length,
      folders,
      unmountedSandboxes: unmounted,
      ...(unmounted.length > 0
        ? {
          note: 'unmountedSandboxes 是云端存在但本地未建立同步的目录。'
            + '把它们挂载到本地属于 L3 控制面能力，尚未实现（见 ARCHITECTURE.md 第 7 节）。',
        }
        : {}),
    };
  }

  /** 客户端设置快照（只含白名单键）。 */
  getSettings() {
    const cfg = this.#safeConfigMap();
    let traffic = null;
    if (cfg.traffic_history) {
      try {
        traffic = JSON.parse(cfg.traffic_history);
      } catch {
        traffic = null;
      }
    }
    delete cfg.traffic_history;

    return {
      account: {
        user: cfg.user_name ?? null,
        nick: cfg.user_nick ?? null,
        loginDate: cfg.login_date ?? null,
      },
      client: {
        appVersion: cfg.app_version ?? null,
        cloudFileEnabled: cfg.cloud_file_enabled ?? null,
        cacheMaxUsagePercent: toNumberOrNull(cfg.cache_max_usage_percent),
        cacheRemainDays: toNumberOrNull(cfg.cache_remain_days),
        placeholderAutoCleanup: cfg.is_placeholder_auto_cleanup ?? null,
        latestUploadDate: cfg.latest_upload_file_date ?? null,
        pinnedSandboxes: cfg.pinned_sandbox_id_set ?? null,
      },
      traffic,
      note: '这是只读快照，来自本地数据库。修改客户端设置属于 L3 控制面能力，尚未实现。',
    };
  }

  /** 同步错误清单。 */
  getSyncErrors({ limit = 100 } = {}) {
    const db = this.#open();
    let rows = [];
    try {
      rows = db
        .prepare(
          `SELECT file_path, reason, type FROM sync_errors LIMIT ?`,
        )
        .all(Math.max(1, Math.min(1000, limit)));
    } catch {
      return { count: 0, errors: [], note: '本地库中没有 sync_errors 表或结构已变化。' };
    }

    const errors = rows.map((r) => {
      const code = toNumberOrNull(r.reason);
      return {
        path: r.file_path,
        reasonCode: code,
        reasonName: code !== null ? (SYNC_ERROR_REASON_HINTS[code] ?? null) : null,
        type: toNumberOrNull(r.type),
      };
    });

    return {
      count: errors.length,
      errors,
      note: 'reasonName 是根据客户端常量符号推测的，数值映射未经官方确认；请以 reasonCode 为准。',
    };
  }

  /**
   * 查询单个云端路径的同步状态。
   *
   * 先在同步文件夹里定位所属 sandbox，再查 `sndobject` 表。
   */
  fileStatus(cloudPath) {
    const db = this.#open();
    const { folders } = this.listSyncFolders();

    const normalized = cloudPath === '/' ? '/' : cloudPath.replace(/\/+$/, '');
    const owner = folders
      .filter((f) => normalized === f.cloudPath || normalized.startsWith(`${f.cloudPath}/`))
      .sort((a, b) => b.cloudPath.length - a.cloudPath.length)[0];

    if (!owner) {
      return {
        path: cloudPath,
        tracked: false,
        reason: 'LOCAL_PATH_NOT_FOUND',
        note: `没有同步文件夹覆盖该路径。当前已挂载：${folders.map((f) => f.cloudPath).join(', ') || '(无)'}`,
      };
    }

    const rel = normalized.slice(owner.cloudPath.length) || '/';
    const lastSlash = rel.lastIndexOf('/');
    const dirPath = lastSlash <= 0 ? '/' : rel.slice(0, lastSlash);
    const fileName = rel.slice(lastSlash + 1) || null;

    const table = `sndobject${this.#suffixFor(owner.sandboxId)}`;
    let row = null;
    try {
      row = fileName
        ? db
          .prepare(
            `SELECT dir_path, file_name, version, dir,
                    CAST(size AS TEXT) AS size, CAST(mtime AS TEXT) AS mtime,
                    CAST(ctime AS TEXT) AS ctime, unpopulated
             FROM ${quoteIdent(table)}
             WHERE dir_path = ? AND file_name = ? LIMIT 1`,
          )
          .get(dirPath, fileName)
        : db
          .prepare(
            `SELECT dir_path, file_name, version, dir,
                    CAST(size AS TEXT) AS size, CAST(mtime AS TEXT) AS mtime,
                    CAST(ctime AS TEXT) AS ctime, unpopulated
             FROM ${quoteIdent(table)}
             WHERE dir_path = ? AND dir = 1 LIMIT 1`,
          )
          .get(dirPath);
    } catch {
      row = null;
    }

    if (!row) {
      return {
        path: cloudPath,
        tracked: false,
        sandbox: { id: owner.sandboxId, name: owner.name, cloudPath: owner.cloudPath, localPath: owner.localPath },
        reason: 'NOT_IN_SYNC_INDEX',
        note: '该路径不在本地同步索引里 —— 可能是云端新增、尚未同步下来，或路径大小写不一致。',
      };
    }

    const unpopulated = toNumberOrNull(row.unpopulated) === 1;
    return {
      path: cloudPath,
      tracked: true,
      sandbox: { id: owner.sandboxId, name: owner.name, cloudPath: owner.cloudPath, localPath: owner.localPath },
      entry: {
        isDirectory: toNumberOrNull(row.dir) === 1,
        version: toNumberOrNull(row.version),
        sizeBytes: toNumberOrNull(row.size),
        mtimeTicks: toNumberOrNull(row.mtime),
        ctimeTicks: toNumberOrNull(row.ctime),
        cloudOnly: unpopulated,
      },
      // .NET ticks → ISO 时间，便于人读
      mtimeIso: dotNetTicksToIso(row.mtime),
      localPathHint: owner.localPath
        ? path.join(owner.localPath, rel.replace(/^\//, '').replace(/\//g, path.sep))
        : null,
    };
  }

  #suffixFor(sandboxId) {
    const local = this.#localSandboxes().find((l) => l.sandboxId === sandboxId);
    return local ? local.suffix : null;
  }

  /** 总体健康快照。 */
  summary() {
    if (!this.available) {
      return {
        available: false,
        dbPath: this.dbPath,
        note: '未找到本地坚果云数据库。L1 文件面不受影响。',
      };
    }
    const folders = this.listSyncFolders();
    const totalObjects = folders.folders.reduce((n, f) => n + (f.stats?.objects ?? 0), 0);
    const cloudOnly = folders.folders.reduce((n, f) => n + (f.stats?.cloudOnly ?? 0), 0);
    const trackedBytes = folders.folders.reduce((n, f) => n + (f.stats?.trackedBytes ?? 0), 0);
    const errors = this.getSyncErrors({ limit: 1000 });

    return {
      available: true,
      dbPath: this.dbPath,
      syncFolders: folders.count,
      unmountedSandboxes: folders.unmountedSandboxes.length,
      trackedObjects: totalObjects,
      cloudOnlyObjects: cloudOnly,
      trackedBytes,
      syncErrorCount: errors.count,
      settings: this.getSettings(),
    };
  }
}

/**
 * 把本地库里的时间戳转成 ISO 8601。
 *
 * ⚠️ 这里的时间戳是 **.NET DateTime ticks**（100ns 计数，纪元 0001-01-01），
 * **不是** Windows FILETIME（纪元 1601-01-01）。两者差 1600 年：
 * 用 FILETIME 的偏移量去换算，`639118499520000000` 会得到 3626 年。
 * 实测校准依据：库里 mtime=639118499520000000 的条目，对应文件名为
 * 「某个 2026-04-15 的表格文件」——换算出 2026-04-15 才自洽。
 *
 * 输入可能是字符串：ticks 会超出 Number 安全整数范围，必须先转 BigInt。
 */
const DOTNET_EPOCH_OFFSET_MS = 62135596800000n; // 0001-01-01 → 1970-01-01

export function dotNetTicksToIso(value) {
  if (value === null || value === undefined || value === '') return null;
  try {
    const ticks = BigInt(String(value).trim());
    if (ticks <= 0n) return null;
    const ms = Number(ticks / 10000n - DOTNET_EPOCH_OFFSET_MS);
    // 边界包含 Unix 纪元本身（ms === 0）；早于 1970 的时间戳对本项目无意义
    if (!Number.isFinite(ms) || ms < 0) return null;
    return new Date(ms).toISOString();
  } catch {
    return null;
  }
}
