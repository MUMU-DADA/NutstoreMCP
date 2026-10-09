using Microsoft.Data.Sqlite;
using NutstoreMcp;

namespace NutstoreMcp.Tests;

/// <summary>
/// 可绑定候选提取的测试。
/// </summary>
/// <remarks>
/// 用合成数据库而不是本机的：候选列表取决于「云端有哪些、本地绑了哪些」，
/// 拿真实数据测等于把测试绑死在当前机器状态上。
/// </remarks>
public class BindCandidatesTests
{
    /// <summary>造一个只有 config 表的库，all_sandbox_list 为给定的 JSON。</summary>
    private static string NewDb(string? allSandboxList, string[]? boundSandboxIds = null)
    {
        var dir = Path.Combine(TestScratch.Root, "bindcand-" + Guid.NewGuid().ToString("N"));
        // LocalNutstoreData 约定是 <dataDir>/db1/nutstore.db
        var dbDir = Path.Combine(dir, "db1");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "nutstore.db");

        using var cn = new SqliteConnection($"Data Source={dbPath}");
        cn.Open();

        Exec(cn, "CREATE TABLE config (key TEXT PRIMARY KEY, value TEXT)");

        if (allSandboxList is not null)
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT INTO config (key, value) VALUES ('all_sandbox_list', $v)";
            cmd.Parameters.AddWithValue("$v", allSandboxList);
            cmd.ExecuteNonQuery();
        }

        // 每个「已绑定」的沙箱对应一张 sndconfig<32位hex> 表
        foreach (var id in boundSandboxIds ?? [])
        {
            var table = "sndconfig" + Guid.NewGuid().ToString("N");
            Exec(cn, $"CREATE TABLE \"{table}\" (key TEXT PRIMARY KEY, value TEXT)");
            Exec(cn, $"INSERT INTO \"{table}\" (key, value) VALUES ('sandbox_name', '{{\"id\":{id},\"localAbsPath\":\"C:\\\\x\"}}')");
        }

        return dir;
    }

    private static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Entry(long id, long magic, string name, string perms = """[{"path":"/","caps":255}]""")
        => $$"""
        [{"sandboxId":{{id}},"magic":{{magic}},"name":"{{name}}","pathPerms":{{perms}},"lanSyncCipher":"SECRET","channelId":"CH"}]
        """;

    [Fact]
    public void 未绑定的云端沙箱成为候选()
    {
        using var data = new LocalNutstoreData(NewDb(Entry(111, 222, "新文件夹")));

        var candidates = data.BindCandidates();

        Assert.Single(candidates);
        Assert.Equal(111, candidates[0].SandboxId);
        Assert.Equal(222, candidates[0].Magic);
        Assert.Equal("新文件夹", candidates[0].Name);
        Assert.Single(candidates[0].PathPerms);
        Assert.Equal("/", candidates[0].PathPerms[0].Path);
        Assert.Equal(255, candidates[0].PathPerms[0].Caps);
    }

    [Fact]
    public void 已绑定的被排除()
    {
        using var data = new LocalNutstoreData(NewDb(Entry(111, 222, "已绑定"), ["111"]));

        // 云端有 111，本地也绑了 111 → 不是候选
        Assert.Empty(data.BindCandidates());
    }

    [Fact]
    public void 只排除id相同的()
    {
        using var data = new LocalNutstoreData(NewDb(
            """[{"sandboxId":111,"magic":1,"name":"a","pathPerms":[]},{"sandboxId":222,"magic":2,"name":"b","pathPerms":[]}]""",
            ["111"]));

        var candidates = data.BindCandidates();

        Assert.Single(candidates);
        Assert.Equal(222, candidates[0].SandboxId);
    }

    [Fact]
    public void 多条路径权限()
    {
        using var data = new LocalNutstoreData(NewDb(
            Entry(1, 2, "x", """[{"path":"/a","caps":1},{"path":"/b/c","caps":2}]""")));

        var perms = data.BindCandidates()[0].PathPerms;

        Assert.Equal(2, perms.Count);
        Assert.Equal("/a", perms[0].Path);
        Assert.Equal(2, perms[1].Caps);
    }

    [Fact]
    public void 缺magic时仍返回但为null()
    {
        // 调用方应当因为缺 magic 而拒绝绑定，但候选本身要能列出来
        using var data = new LocalNutstoreData(NewDb(
            """[{"sandboxId":111,"name":"x","pathPerms":[{"path":"/","caps":1}]}]"""));

        var c = data.BindCandidates()[0];

        Assert.Equal(111, c.SandboxId);
        Assert.Null(c.Magic);
    }

    [Fact]
    public void 权限残缺时不给残缺的候选()
    {
        // path 有、caps 缺 → 那条权限被丢掉（不是补 0）
        using var data = new LocalNutstoreData(NewDb(
            """[{"sandboxId":1,"magic":2,"pathPerms":[{"path":"/"},{"caps":255}]}]"""));

        Assert.Empty(data.BindCandidates()[0].PathPerms);
    }

    [Fact]
    public void 缺sandboxId的被跳过()
    {
        using var data = new LocalNutstoreData(NewDb(
            """[{"magic":2,"name":"x","pathPerms":[]},{"sandboxId":9,"magic":3,"name":"y","pathPerms":[]}]"""));

        var candidates = data.BindCandidates();

        Assert.Single(candidates);
        Assert.Equal(9, candidates[0].SandboxId);
    }

    [Theory]
    [InlineData(null)]                      // 没有这个键
    [InlineData("not json")]
    [InlineData("{}")]                      // 不是数组
    [InlineData("[]")]
    public void 没有可用数据时返回空而不是抛异常(string? raw)
    {
        using var data = new LocalNutstoreData(NewDb(raw));

        Assert.Empty(data.BindCandidates());
    }

    [Fact]
    public void 密钥字段不出现在候选里()
    {
        // 候选里只该有绑定必需的字段。lanSyncCipher / channelId 是凭据，
        // 它们的值不该从这条路漏出去。
        using var data = new LocalNutstoreData(NewDb(Entry(1, 2, "x")));

        var json = System.Text.Json.JsonSerializer.Serialize(data.BindCandidates());

        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lanSyncCipher", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("channelId", json, StringComparison.OrdinalIgnoreCase);
    }
}
