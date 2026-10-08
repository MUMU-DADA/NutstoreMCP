using System.Text.Json;
using NutstoreMcp;

namespace NutstoreMcp.Tests;

/// <summary>
/// L2 对**真实数据库**验证。没有装坚果云的机器上，这些测试会被跳过
/// （<see cref="DbFactAttribute"/>），而不是假装通过。
/// </summary>
public class LocalNutstoreDataTests
{
    private static readonly string? DataDir =
        Environment.GetEnvironmentVariable("NUTSTORE_DATA_DIR")
        ?? (Environment.GetEnvironmentVariable("APPDATA") is { } appData
            ? Path.Combine(appData, "Nutstore")
            : null);

    private static bool HasDb => DataDir is not null && File.Exists(Path.Combine(DataDir, "db1", "nutstore.db"));

    private static LocalNutstoreData Open() => new(DataDir);

    // ───────────────────────── 纯函数 ─────────────────────────

    [Fact]
    public void DotNetTicksToIso_UsesDotNetEpochNotFiletimeEpoch()
    {
        // 库里的真实值：对应文件名为「某个 2026-04-15 的表格文件」
        // 若误用 FILETIME 偏移量（116444736000000000），这里会得到 3626 年
        var iso = LocalNutstoreData.DotNetTicksToIso(639118499520000000);
        Assert.NotNull(iso);
        Assert.StartsWith("2026-04-15T", iso, StringComparison.Ordinal);

        // 纪元起点：.NET ticks 0 = 0001-01-01，621355968000000000 = 1970-01-01
        Assert.Equal("1970-01-01T00:00:00.000Z", LocalNutstoreData.DotNetTicksToIso(621355968000000000));

        // 100ns 精度，转毫秒会截断；跨过 1 万 tick 才看得出差别
        Assert.NotEqual(
            LocalNutstoreData.DotNetTicksToIso(639118499520000000),
            LocalNutstoreData.DotNetTicksToIso(639118499520100000));
        Assert.Equal("2026-04-15T11:39:12.010Z", LocalNutstoreData.DotNetTicksToIso(639118499520100000));

        Assert.Null(LocalNutstoreData.DotNetTicksToIso(null));
        Assert.Null(LocalNutstoreData.DotNetTicksToIso(0));
        Assert.Null(LocalNutstoreData.DotNetTicksToIso(-1));
    }

    [Fact]
    public void SafeConfigKeys_ContainsNoSecrets()
    {
        var bad = LocalNutstoreData.SafeConfigKeys
            .Where(k => new[] { "token", "pwd", "passw", "secret", "hash", "salt", "cipher", "credential" }
                .Any(f => k.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(bad);
    }

    [Fact]
    public void MissingDataDirGivesActionableErrorInsteadOfCrashing()
    {
        using var empty = new LocalNutstoreData(null);
        Assert.False(empty.Available);

        var summary = empty.Summary();
        Assert.False(summary.Available);
        Assert.Contains("未找到本地坚果云数据库", summary.Note!, StringComparison.Ordinal);

        var ex = Assert.Throws<LocalDataException>(() => empty.ListSyncFolders());
        Assert.Equal("LOCAL_DATA_UNAVAILABLE", ex.Code);
        Assert.Contains("NUTSTORE_DATA_DIR", ex.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDatabaseGivesActionableError()
    {
        using var missing = new LocalNutstoreData(Path.Combine(Path.GetTempPath(), "__ns_mcp_nope__"));
        Assert.False(missing.Available);

        var ex = Assert.Throws<LocalDataException>(() => missing.ListSyncFolders());
        Assert.Equal("LOCAL_DATA_UNAVAILABLE", ex.Code);
        Assert.Contains("坚果云客户端", ex.Hint!, StringComparison.Ordinal);
    }

    // ───────────────────────── 真实数据库 ─────────────────────────

    [DbFact]
    public void SyncFolders_MapsCloudPathsToLocalPaths()
    {
        using var local = Open();
        var result = local.ListSyncFolders();

        Assert.True(result.Count > 0, "应当至少有一个同步文件夹");
        foreach (var f in result.Folders)
        {
            Assert.StartsWith("/", f.CloudPath, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(f.LocalPath), $"缺少 localPath：{f.Name}");
            Assert.DoesNotContain('\\', f.CloudPath);
            Assert.NotNull(f.Stats);
        }

        Assert.Contains(result.Folders, f => f.IsDefault);
        Assert.Contains(result.Folders, f => f.CloudPathInferred);
    }

    [DbFact]
    public void SandboxList_StripsLanSyncCipherAndChannelId()
    {
        using var local = Open();
        var json = JsonSerializer.Serialize(local.ListSyncFolders());

        Assert.DoesNotContain("lanSyncCipher", json, StringComparison.Ordinal);
        Assert.DoesNotContain("channelId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"magic\"", json, StringComparison.Ordinal);
    }

    [DbFact]
    public void SettingsSnapshot_ExcludesSecretKeys()
    {
        using var local = Open();
        var json = JsonSerializer.Serialize(local.GetSettings());

        Assert.DoesNotContain("encrypted_token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("device_token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bcrypt_pwd_hash", json, StringComparison.Ordinal);
        Assert.NotNull(local.GetSettings().Account.User);
    }

    [DbFact]
    public void Redaction_IsHardGuarantee_NoSecretValueLeaksIntoAnyOutput()
    {
        // 直接从库里取真实密文（仅在测试进程内比对，不打印），再断言任何 L2 输出都不含它们
        var dbPath = Path.Combine(DataDir!, "db1", "nutstore.db");
        var secrets = new List<string>();

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString()))
        {
            raw.Open();
            foreach (var key in new[] { "encrypted_token", "device_token", "bcrypt_pwd_hash", "machine_name" })
            {
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "SELECT value FROM config WHERE key = $k";
                cmd.Parameters.AddWithValue("$k", key);
                if (cmd.ExecuteScalar() is not string value)
                {
                    continue;
                }

                secrets.Add(value);
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: >= 8 } inner)
                        {
                            secrets.Add(inner);
                        }
                    }
                }
                catch (JsonException)
                {
                    // 非 JSON，忽略
                }
            }
        }

        Assert.NotEmpty(secrets);

        using var local = Open();
        var outputs = new[]
        {
            JsonSerializer.Serialize(local.Summary()),
            JsonSerializer.Serialize(local.ListSyncFolders()),
            JsonSerializer.Serialize(local.GetSettings()),
            JsonSerializer.Serialize(local.GetSyncErrors(50)),
        };

        foreach (var blob in outputs)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, blob, StringComparison.Ordinal);
            }
        }
    }

    [DbFact]
    public void FileStatus_FindsARealTrackedEntry()
    {
        using var local = Open();
        var target = local.ListSyncFolders().Folders.FirstOrDefault(f => (f.Stats?.Objects ?? 0) > 0);
        Assert.NotNull(target);

        var dbPath = Path.Combine(DataDir!, "db1", "nutstore.db");
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        raw.Open();

        var tableNames = new List<string>();
        using (var listCmd = raw.CreateCommand())
        {
            listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'sndobject%'";
            using var listReader = listCmd.ExecuteReader();
            while (listReader.Read())
            {
                tableNames.Add(listReader.GetString(0));
            }
        }

        string? table = null;
        foreach (var name in tableNames)
        {
            using var probe = raw.CreateCommand();
            probe.CommandText = $"SELECT 1 FROM \"{name}\" WHERE dir = 0 LIMIT 1";
            if (probe.ExecuteScalar() is not null)
            {
                table = name;
                break;
            }
        }

        Assert.NotNull(table);

        string fileName;
        string dirPath;
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = $"SELECT file_name, dir_path FROM \"{table}\" WHERE dir = 0 LIMIT 1";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            fileName = reader.GetString(0);
            dirPath = reader.GetString(1);
        }

        var relDir = dirPath == "/" ? string.Empty : dirPath;
        var cloudPath = $"{target!.CloudPath}{relDir}/{fileName}";

        var status = local.FileStatus(cloudPath);
        Assert.True(status.Tracked, $"应当跟踪 {cloudPath}");
        Assert.NotNull(status.Entry);
        Assert.NotNull(status.LocalPathHint);
        Assert.Contains(fileName, status.LocalPathHint!, StringComparison.Ordinal);
        if (status.MtimeIso is not null)
        {
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", status.MtimeIso);
        }
    }

    [DbFact]
    public void FileStatus_ExplainsUnmountedPathInsteadOfThrowing()
    {
        using var local = Open();
        var r = local.FileStatus("/definitely-not-a-sandbox/x.md");
        Assert.False(r.Tracked);
        Assert.Equal("LOCAL_PATH_NOT_FOUND", r.Reason);
        Assert.Contains("已挂载", r.Note!, StringComparison.Ordinal);
    }

    [DbFact]
    public void Summary_NumbersAreSelfConsistent()
    {
        using var local = Open();
        var s = local.Summary();
        Assert.True(s.Available);
        Assert.True(s.SyncFolders > 0);
        Assert.True(s.TrackedObjects > 0);
        Assert.True(s.TrackedBytes > 0);
        Assert.True(s.SyncErrorCount >= 0);
        Assert.NotNull(s.Settings?.Account.User);
    }

    [DbFact]
    public void ReadOnlyConnection_IsPhysicallyRejected()
    {
        using var local = Open();
        local.ListSyncFolders(); // 触发 open
        var conn = local.ConnectionForTests();

        // 用事务包住，万一连接不是只读的也不会留下痕迹
        conn.ExecuteNonQuery("BEGIN");
        var rejected = false;
        try
        {
            conn.ExecuteNonQuery("CREATE TABLE __nutstore_mcp_should_not_exist (x)");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            rejected = ex.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("attempt to write", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                conn.ExecuteNonQuery("ROLLBACK");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // 只读连接上 BEGIN 也应失败，忽略
            }
        }

        Assert.True(rejected, "连接必须是只读的：写入应当被 SQLite 拒绝");

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE name = '__nutstore_mcp_should_not_exist'";
        Assert.Null(check.ExecuteScalar());
    }
}

/// <summary>数据库不存在时跳过，而不是假装通过。</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? filePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int lineNumber = 0)
        : base(filePath, lineNumber)
    {
        var dataDir = Environment.GetEnvironmentVariable("NUTSTORE_DATA_DIR")
            ?? (Environment.GetEnvironmentVariable("APPDATA") is { } appData ? Path.Combine(appData, "Nutstore") : null);

        if (dataDir is null || !File.Exists(Path.Combine(dataDir, "db1", "nutstore.db")))
        {
            Skip = "本机没有坚果云数据库，跳过 L2 实测";
        }
    }
}

internal static class SqliteTestExtensions
{
    public static void ExecuteNonQuery(this Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
