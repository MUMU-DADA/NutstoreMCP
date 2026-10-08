namespace NutstoreMcp.Tests;

/// <summary>
/// 测试用临时目录。
///
/// ⚠️ 刻意**不用 <c>Path.GetTempPath()</c>**：实测在本机上，测试进程
/// （xunit v3 进程内 runner）在 <c>%TEMP%</c> 下 <c>Directory.CreateDirectory</c>
/// 会抛 <see cref="UnauthorizedAccessException"/>，而同一个 pwsh 会话里
/// PowerShell 与 <c>[System.IO.Directory]::CreateDirectory</c> 都能建。
/// 换成仓库内的 <c>.test-tmp/</c> 后一切正常，且与 Kestrel 的启动无关
/// （探针在启动 Kestrel 前后都失败）。
///
/// 放在仓库里还有两个好处：路径可发现、被 .gitignore 覆盖。
/// </summary>
internal static class TestScratch
{
    private static readonly List<string> Created = [];
    private static readonly Lock Gate = new();

    static TestScratch()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    public static string Root => Path.Combine(RepoRoot(), ".test-tmp");

    public static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        lock (Gate)
        {
            Created.Add(path);
        }

        return path;
    }

    public static void Cleanup()
    {
        List<string> paths;
        lock (Gate)
        {
            paths = [.. Created];
            Created.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
        }
    }

    /// <summary>仓库根目录。.NET 10 的解决方案文件是 .slnx（XML 格式），不是 .sln。</summary>
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "NutstoreMCP.slnx"))
                || File.Exists(Path.Combine(dir, "ARCHITECTURE.md")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("找不到仓库根目录（NutstoreMCP.slnx / ARCHITECTURE.md）");
    }
}

public class TestScratchTests
{
    [Fact]
    public void ScratchDirectoryIsWritable()
    {
        var dir = TestScratch.NewDirectory("probe");
        try
        {
            File.WriteAllText(Path.Combine(dir, "probe.txt"), "ok");
            Assert.True(File.Exists(Path.Combine(dir, "probe.txt")));
            Assert.StartsWith(TestScratch.Root, dir, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
