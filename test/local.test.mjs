import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';

import { LocalNutstoreData, SAFE_CONFIG_KEYS, dotNetTicksToIso } from '../src/local.mjs';

const DATA_DIR = process.env.NUTSTORE_DATA_DIR
  || (process.env.APPDATA ? path.join(process.env.APPDATA, 'Nutstore') : null);
const DB_PATH = DATA_DIR ? path.join(DATA_DIR, 'db1', 'nutstore.db') : null;
const HAS_DB = Boolean(DB_PATH && existsSync(DB_PATH));

/** 有一台装了坚果云的机器才能跑的测试；没有就跳过而不是假装通过。 */
const needsDb = { skip: HAS_DB ? false : '本机没有坚果云数据库，跳过 L2 实测' };

// ───────────────────────── 纯函数 ─────────────────────────

test('dotNetTicksToIso 用 .NET 纪元（0001）而不是 FILETIME 纪元（1601）', () => {
  // 库里的真实值：对应文件名为「某个 2026-04-15 的表格文件」
  // 若误用 FILETIME 偏移量（116444736000000000），这里会得到 3626 年
  const iso = dotNetTicksToIso('639118499520000000');
  assert.match(iso, /^2026-04-15T/, `换算错误，实际得到 ${iso}`);

  // 纪元起点：.NET ticks 0 = 0001-01-01
  assert.equal(dotNetTicksToIso('621355968000000000'), '1970-01-01T00:00:00.000Z');

  // 超出 Number 安全整数范围也必须精确（BigInt 路径生效）。
  // 注意：库里的值是 100ns 精度，转成毫秒会截断 —— 要跨过 1 万 tick 才看得出差别。
  assert.notEqual(
    dotNetTicksToIso('639118499520000000'),
    dotNetTicksToIso('639118499520100000'),
    '相差 10ms 必须能区分',
  );
  assert.equal(dotNetTicksToIso('639118499520100000'), '2026-04-15T11:39:12.010Z');

  assert.equal(dotNetTicksToIso(null), null);
  assert.equal(dotNetTicksToIso(''), null);
  assert.equal(dotNetTicksToIso('0'), null);
  assert.equal(dotNetTicksToIso('not-a-number'), null);
});

test('SAFE_CONFIG_KEYS 不含任何敏感键', () => {
  const bad = SAFE_CONFIG_KEYS.filter((k) => /token|pwd|passw|secret|hash|salt|cipher|credential/i.test(k));
  assert.deepEqual(bad, [], `白名单里混入了敏感键：${bad.join(', ')}`);
});

test('数据目录缺失时给出可操作错误而不是崩溃', () => {
  const empty = new LocalNutstoreData({ dataDir: null });
  assert.equal(empty.available, false);

  // summary 不应抛错 —— 它要能报告「不可用」这个事实本身
  const s = empty.summary();
  assert.equal(s.available, false);
  assert.match(s.note, /未找到本地坚果云数据库/);

  // 真正去读数据时才抛，且错误里必须给出修复动作
  assert.throws(() => empty.listSyncFolders(), (err) => {
    assert.equal(err.code, 'LOCAL_DATA_UNAVAILABLE');
    assert.match(err.hint, /NUTSTORE_DATA_DIR/);
    return true;
  });
});

test('数据库不存在时报明确错误', () => {
  const missing = new LocalNutstoreData({ dataDir: path.join(DATA_DIR ?? '.', '__nope__') });
  assert.equal(missing.available, false);
  assert.throws(() => missing.listSyncFolders(), (err) => {
    assert.equal(err.code, 'LOCAL_DATA_UNAVAILABLE');
    assert.match(err.hint, /坚果云客户端/);
    return true;
  });
});

// ───────────────────────── 真实数据库 ─────────────────────────

test('同步文件夹清单：云端路径 ↔ 本地路径映射', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const result = local.listSyncFolders();
    assert.ok(result.count > 0, '应当至少有一个同步文件夹');

    for (const f of result.folders) {
      assert.ok(f.cloudPath.startsWith('/'), `cloudPath 必须绝对：${f.cloudPath}`);
      assert.ok(f.localPath, `缺少 localPath：${f.name}`);
      assert.ok(!f.cloudPath.includes('\\'), '云端路径不得含反斜杠');
      assert.ok(f.stats, `缺少对象统计：${f.name}`);
      assert.equal(typeof f.stats.objects, 'number');
    }

    // 默认 sandbox 的名字来自本地目录名，必须标记为推断值
    const inferred = result.folders.filter((f) => f.cloudPathInferred);
    for (const f of inferred) {
      assert.match(f.cloudPath, /^\/.+/);
    }

    // 至少有一个默认 sandbox
    assert.ok(result.folders.some((f) => f.isDefault));
  } finally {
    local.close();
  }
});

test('sandbox 列表剔除局域网同步密钥与 channelId', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const json = JSON.stringify(local.listSyncFolders());
    assert.ok(!json.includes('lanSyncCipher'), 'lanSyncCipher 是局域网同步密钥，绝不能输出');
    assert.ok(!json.includes('channelId'), 'channelId 是内部标识，不应外泄');
    assert.ok(!/"magic"/.test(json), 'magic 是内部标识，不应外泄');
  } finally {
    local.close();
  }
});

test('设置快照：只含白名单键，且不含账号中的邮箱以外的凭据', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const s = local.getSettings();
    assert.ok(s.account);
    assert.ok(s.client);
    assert.ok(!('encrypted_token' in s));
    const json = JSON.stringify(s);
    assert.ok(!/encrypted_token|device_token|bcrypt_pwd_hash/.test(json));
  } finally {
    local.close();
  }
});

test('脱敏是硬保证：数据库里的 token 值不得出现在任何 L2 输出里', needsDb, () => {
  // 直接从库里把真实密文取出来（仅在测试进程内比对，不打印）
  const raw = new DatabaseSync(DB_PATH, { readOnly: true });
  const secrets = [];
  try {
    for (const key of ['encrypted_token', 'device_token', 'bcrypt_pwd_hash', 'machine_name']) {
      const row = raw.prepare('SELECT value FROM config WHERE key = ?').get(key);
      if (row?.value) {
        secrets.push(row.value);
        // 密文 JSON 里的内层值也要覆盖
        try {
          const parsed = JSON.parse(row.value);
          for (const v of Object.values(parsed)) {
            if (typeof v === 'string' && v.length >= 8) secrets.push(v);
          }
        } catch { /* 非 JSON，忽略 */ }
      }
    }
  } finally {
    raw.close();
  }

  assert.ok(secrets.length > 0, '本机应当存在至少一个敏感值，否则这个测试没有意义');

  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const outputs = [
      JSON.stringify(local.summary()),
      JSON.stringify(local.listSyncFolders()),
      JSON.stringify(local.getSettings()),
      JSON.stringify(local.getSyncErrors({ limit: 50 })),
    ];
    for (const blob of outputs) {
      for (const secret of secrets) {
        assert.ok(
          !blob.includes(secret),
          `敏感值泄漏到 L2 输出中（前缀 ${secret.slice(0, 4)}…）`,
        );
      }
    }
  } finally {
    local.close();
  }
});

test('单文件同步状态：从库里挑一个真实条目反查', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  const raw = new DatabaseSync(DB_PATH, { readOnly: true });
  try {
    const folders = local.listSyncFolders().folders;
    const target = folders.find((f) => (f.stats?.objects ?? 0) > 0);
    assert.ok(target, '应当存在非空的同步文件夹');

    const table = raw
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'sndobject%'")
      .all()
      .map((r) => r.name)
      .find((n) => {
        const r = raw.prepare(`SELECT file_name, dir_path FROM "${n}" WHERE dir = 0 LIMIT 1`).get();
        return Boolean(r);
      });
    assert.ok(table, '应当存在含文件的 sndobject 表');

    const row = raw.prepare(`SELECT file_name, dir_path FROM "${table}" WHERE dir = 0 LIMIT 1`).get();
    // dir_path 是 sandbox 内相对路径；拼成云端绝对路径
    const relDir = row.dir_path === '/' ? '' : row.dir_path;
    const cloudPath = `${target.cloudPath}${relDir}/${row.file_name}`;

    const status = local.fileStatus(cloudPath);
    assert.equal(status.tracked, true, `应当跟踪 ${cloudPath}`);
    assert.equal(status.entry.isDirectory, false);
    assert.ok(status.mtimeIso === null || /^\d{4}-\d{2}-\d{2}T/.test(status.mtimeIso));
    assert.ok(status.localPathHint.includes(row.file_name));
  } finally {
    raw.close();
    local.close();
  }
});

test('未挂载路径的 fileStatus 给出解释而不是抛错', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const r = local.fileStatus('/definitely-not-a-sandbox/x.md');
    assert.equal(r.tracked, false);
    assert.equal(r.reason, 'LOCAL_PATH_NOT_FOUND');
    assert.match(r.note, /已挂载/);
  } finally {
    local.close();
  }
});

test('summary 汇总数字自洽', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    const s = local.summary();
    assert.equal(s.available, true);
    assert.ok(s.syncFolders > 0);
    assert.ok(s.trackedObjects > 0);
    assert.ok(s.trackedBytes > 0);
    assert.ok(s.syncErrorCount >= 0);
    assert.ok(s.settings.account.user);
  } finally {
    local.close();
  }
});

test('只读打开：写入被 SQLite 层物理拒绝', needsDb, () => {
  const local = new LocalNutstoreData({ dataDir: DATA_DIR });
  try {
    local.listSyncFolders(); // 触发 open
    const db = local._connectionForTests();

    // 用事务包住，万一连接不是只读的也不会留下痕迹
    db.exec('BEGIN');
    let writeRejected = false;
    try {
      db.exec('CREATE TABLE __nutstore_mcp_should_not_exist (x)');
    } catch (err) {
      writeRejected = /readonly|read-only|attempt to write/i.test(err.message);
    } finally {
      try {
        db.exec('ROLLBACK');
      } catch { /* 只读连接上 BEGIN 也应失败，忽略 */ }
    }
    assert.ok(writeRejected, '连接必须是只读的：写入应当被 SQLite 拒绝');

    const leftover = db
      .prepare("SELECT name FROM sqlite_master WHERE name = '__nutstore_mcp_should_not_exist'")
      .get();
    assert.equal(leftover, undefined, '测试不得在用户数据库里留下任何表');
  } finally {
    local.close();
  }
});
