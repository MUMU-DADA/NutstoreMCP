using System.Globalization;

namespace NutstoreMcp;

/// <summary>
/// <c>--doctor</c>：配置与连通性自检。
///
/// **绝不输出凭据值** —— 只报「是否已配置」和账号名。
/// </summary>
public static class Doctor
{
    public static async Task<int> RunAsync()
    {
        var lines = new List<string> { "nutstore-mcp v0.2.0 — 自检" };

        AppConfig config;
        try
        {
            config = AppConfig.Load();
        }
        catch (ConfigException ex)
        {
            lines.Add($"❌ 配置错误：{ex.Message}");
            if (ex.Hint is not null)
            {
                lines.Add($"   hint: {ex.Hint}");
            }

            Console.WriteLine(string.Join('\n', lines));
            return 1;
        }

        lines.Add(string.Empty);
        lines.Add("── 配置 ──");
        lines.Add($"  WebDAV 端点     {config.Host}");
        lines.Add($"  凭据            {(config.HasCredentials ? $"已配置（{config.User}）" : "未配置 → L1 不可用")}");
        lines.Add($"  云端白名单      {(config.Roots.Count > 0 ? string.Join(", ", config.Roots) : "(空) → 拒绝一切云端访问")}");
        lines.Add($"  本机白名单      {string.Join(", ", config.LocalRoots)}");
        lines.Add($"  只读模式        {(config.ReadOnly ? "开启（写操作会被拒绝）" : "关闭（允许写操作）")}");
        lines.Add($"  限流            {config.RateLimit} 次 / {Math.Round(config.RateWindowMs / 60000.0)} 分钟");
        lines.Add($"  上传上限        {config.MaxUploadBytes / 1024 / 1024} MB");
        lines.Add($"  .env            {config.EnvFileLoaded ?? "(未加载)"}");

        lines.Add(string.Empty);
        lines.Add("── L2 本地数据 ──");
        using (var local = new LocalNutstoreData(config.DataDir))
        {
            if (!local.Available)
            {
                lines.Add($"  ⚠️ 未找到本地数据库：{local.DbPath ?? "(数据目录未知)"}");
                lines.Add("     只影响 L2 工具；L1 文件面不受影响。");
            }
            else
            {
                try
                {
                    var s = local.Summary();
                    lines.Add($"  数据库          {s.DbPath}");
                    lines.Add($"  同步文件夹      {s.SyncFolders} 个（另有无本地映射的云端目录 {s.UnmountedSandboxes} 个）");
                    lines.Add($"  受管对象        {s.TrackedObjects}（其中云端独有 {s.CloudOnlyObjects}）");
                    lines.Add($"  受管字节        {s.TrackedBytes}");
                    lines.Add($"  同步错误        {s.SyncErrorCount}");

                    var folders = local.ListSyncFolders();
                    foreach (var f in folders.Folders.Take(20))
                    {
                        var objects = f.Stats?.Objects.ToString(CultureInfo.InvariantCulture) ?? "?";
                        lines.Add($"    · {f.CloudPath}  →  {f.LocalPath}  [{objects} 对象]");
                    }

                    if (folders.Folders.Count > 20)
                    {
                        lines.Add($"    … 另有 {folders.Folders.Count - 20} 个");
                    }
                }
                catch (NutstoreException ex)
                {
                    lines.Add($"  ❌ 读取失败：{ex.Message}");
                }
            }
        }

        lines.Add(string.Empty);
        lines.Add("── L1 连通性 ──");
        if (!config.HasCredentials)
        {
            lines.Add("  ⏭  跳过（未配置凭据）");
        }
        else
        {
            var limiter = new SlidingWindowRateLimiter(config.RateLimit, config.RateWindowMs);
            using var webdav = new WebDavClient(config, limiter);
            try
            {
                var h = await webdav.HealthAsync(CancellationToken.None);
                lines.Add($"  ✅ 认证成功：{h.AuthenticatedAs}");
                if (h.Quota?.AvailableBytes is { } available)
                {
                    lines.Add($"  可用空间        {available / 1024.0 / 1024 / 1024:F2} GB");
                }

                lines.Add($"  已用请求        {h.RateLimit.Used}/{h.RateLimit.Limit}");
            }
            catch (NutstoreException ex)
            {
                lines.Add($"  ❌ {ex.Message}");
                if (ex.Hint is not null)
                {
                    lines.Add($"     hint: {ex.Hint}");
                }
            }
        }

        Console.WriteLine(string.Join('\n', lines));
        return 0;
    }
}
