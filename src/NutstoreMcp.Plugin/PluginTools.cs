using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using NutstoreMcp.Plugin.Contract;

namespace NutstoreMcp.Plugin;

/// <summary>
/// 插件工具集。
///
/// <para>
/// <b>当前阶段硬性只读</b>：不动本地文件、不同步、绝不改远程内容。
/// 开发期的不确定性不该有机会影响到用户的同步数据。
/// 所有工具一律 <c>ReadOnly = true, Destructive = false</c>。
/// </para>
/// <para>
/// 这里刻意只放「只有进程内才能拿到」的信息。能用 WebDAV 或本地库拿到的能力
/// 仍由独立的 MCP 服务器负责，两边不重复。
/// </para>
/// <para>
/// 界面相关的工具全部经 <see cref="IUiHook"/> 转发到客户端默认域执行 ——
/// 插件自己在子域里，碰不到 WPF。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class PluginTools
{
    private const string UiUnavailable =
        "{\"available\": false, \"reason\": \"UI 钩子未接入（桥没能在客户端默认域里加载 NutstoreMcp.UiHook.dll）\"}";

    [McpServerTool(Name = "nutstore_l3_create_sync_folder", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("【默认只预演】把本地空目录绑定为坚果云同步文件夹。这是 L3 唯一可能丢数据的操作，因此放行条件极严：需要环境变量 NUTSTORE_L3_WRITE=1、confirm=true、dryRun=false，且本地目录必须为空、不与现有同步根重叠。任何一条不满足都拒绝执行。**当前执行通道未接通**，即使在满足全部条件时也只返回判定结果。")]
    public static string L3CreateSyncFolder(
        [Description("要绑定的本地目录（必须已存在且为空）")] string localPath,
        [Description("要绑定的云端沙箱 ID（必须 >0）。用 nutstore_sync_folders 查未绑定的沙箱")] long sandboxId = 0,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true（只预演不执行）")] bool dryRun = true,
        [Description("沙箱的同步状态；必须是 NotSynced 才放行")] string? sandboxSyncState = null,
        [Description("现有同步根路径列表，分号分隔。不传则因无法确认重叠而拒绝")] string? existingBindings = null,
        [Description("沙箱的内部标识（来自 nutstore_l3_bind_candidates 的 magic）。绑定必须原样带上")] long magic = 0,
        [Description("路径权限的 path 列表（来自 bind_candidates 的 pathPerms[].path），与 permCaps 一一对应")] string[]? permPaths = null,
        [Description("路径权限的 caps 列表（来自 bind_candidates 的 pathPerms[].caps），与 permPaths 一一对应")] long[]? permCaps = null)
    {
        try
        {
            // 环境变量（进程级，需重启客户端才生效）或令牌文件（随时可建可删）
            var unlock = PluginEntry.IsWriteUnlocked();

            var exists = !string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath);
            var isDir = exists;

            var isEmpty = false;
            var entryCount = -1;
            var probeError = (string?)null;

            if (isDir)
            {
                try
                {
                    // 只数条目，不递归、不读内容 —— 这个操作本身绝不能有副作用
                    entryCount = Directory.EnumerateFileSystemEntries(localPath).Take(2).Count();
                    isEmpty = entryCount == 0;
                }
                catch (Exception ex)
                {
                    probeError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            var bindings = string.IsNullOrWhiteSpace(existingBindings)
                ? []
                // net48 没有 Split(char, StringSplitOptions)，只有数组重载
                : existingBindings!.Split([';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

            var request = new L3.WriteGuards.Request(
                UnlockPresent: unlock,
                Confirmed: confirm,
                DryRun: dryRun,
                LocalPath: localPath,
                LocalPathExists: exists,
                LocalPathIsDirectory: isDir,
                LocalPathIsEmpty: isEmpty,
                PathAlreadyBound: false,
                ExistingBindingPaths: bindings,
                SandboxId: sandboxId,
                SandboxSyncState: sandboxSyncState);

            var verdict = L3.WriteGuards.Evaluate(request);

            var refusals = verdict.Refusals.ToList();

            // 无法确认重叠时 fail-closed：宁可拒绝也不要赌
            if (bindings.Count == 0)
            {
                refusals.Add(
                    "未提供现有同步根列表，无法确认新路径是否与它们重叠。"
                    + "请先用 nutstore_l3_sync_folders 取到后通过 existingBindings 传入。");
            }

            if (probeError is not null)
            {
                refusals.Add($"无法检查目录内容: {probeError}");
            }

            // ── 执行通道 ──
            //
            // 只有全部放行、且不是预演时才真的调客户端。
            // 钩子没有 BindSandbox（旧版本）时会抛 RemotingException —— 捕获后如实报告，
            // 不假装成功。
            string? executionResult = null;
            var executionAvailable = false;

            if (refusals.Count == 0 && !dryRun)
            {
                var ui = PluginEntry.Ui;
                if (ui is null)
                {
                    refusals.Add("UI 钩子不可用 —— 无法在默认域里执行绑定。");
                }
                else if (permPaths is null || permCaps is null || permPaths.Length == 0)
                {
                    // fail-closed：没有权限信息就不绑定
                    refusals.Add(
                        "缺少 pathPerms（permPaths / permCaps）—— 绑定一个权限不明的沙箱"
                        + "比不绑定危险得多。请从 nutstore_l3_bind_candidates 取。");
                }
                else
                {
                    try
                    {
                        executionResult = ui.BindSandbox(sandboxId, magic, localPath, permPaths, permCaps);
                        executionAvailable = true;
                    }
                    catch (Exception ex)
                    {
                        refusals.Add(
                            $"调用默认域失败（钩子可能是旧版本，需要重启客户端）: "
                            + $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                allowed = refusals.Count == 0,
                wouldWrite = refusals.Count == 0 && !dryRun && executionAvailable,
                dryRun,
                unlockVariablePresent = unlock,
                probe = new
                {
                    localPath,
                    exists,
                    isDirectory = isDir,
                    isEmpty,
                    entryCount,
                    sandboxId,
                    magic,
                    permCount = permPaths?.Length ?? 0,
                    sandboxSyncState = sandboxSyncState ?? "(未提供 —— 将因无法确认状态而被拒)",
                    existingBindingCount = bindings.Count,
                },
                refusals,
                notes = verdict.Notes,
                executionAvailable,
                executionResult,
                executionUnavailableReason = executionAvailable
                    ? null
                    : "尚未执行。需要：环境变量 NUTSTORE_L3_WRITE=1 + confirm=true + dryRun=false，"
                      + "且默认域钩子已更新（需重启客户端一次）。",
            }, Indented);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_forbidden_methods", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出永远不允许经 MCP 调用的客户端方法及理由（注销同步根、解绑沙箱、重建对象表等后果不可逆的操作）。这些是代码级闸门，不只是文档。")]
    public static string L3ForbiddenMethods()
    {
        try
        {
            return L3.ForbiddenClientMethods.Describe();
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_env", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("报告插件子域里的基础路径（AppData、当前目录、入口程序集等）。用于诊断客户端静态初始化为什么在子域里失败。只读。")]
    public static string L3Env()
    {
        try
        {
            return L3.ClientAssemblies.ProbeEnvironment();
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_sync_folders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("用坚果云客户端自己的 DAO 读取所有同步文件夹绑定（沙箱 ID、云端名、本地路径、是否占位符）。数据来自客户端模型而非猜表结构，客户端升级后更不容易失准。**读的是数据库副本，真实数据库不会被打开**。")]
    public static string L3SyncFolders()
    {
        // 优先走默认域：客户端那些静态初始化依赖 Assembly.GetEntryAssembly()，
        // 而它在子域里恒为 null（CLR 固有行为，已实测）。所以默认域是唯一能跑通的地方。
        var ui = PluginEntry.Ui;
        if (ui is not null)
        {
            try
            {
                var viaHook = ui.ReadClientSyncFolders();
                if (viaHook is not null && viaHook.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
                {
                    return viaHook;
                }

                // 钩子没成功（例如钩子还是旧版本），退回子域实现并说明原因
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    viaDefaultDomain = viaHook,
                    fallback = TryPluginDomain(),
                    note = "默认域通道不可用（钩子可能是旧版本，需要重启客户端才能更新）。"
                         + "下面是子域尝试的结果 —— 它注定会失败，因为客户端静态初始化在子域里起不来。",
                }, Indented);
            }
            catch (Exception ex)
            {
                // 旧版钩子没有这个方法时会抛 RemotingException
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    viaDefaultDomain = $"{ex.GetType().Name}: {ex.Message}",
                    note = "钩子没有 ReadClientSyncFolders —— 需要重启客户端让新钩子生效。",
                }, Indented);
            }
        }

        return L3.SandboxApi.ReadSyncFolders();
    }

    private static string TryPluginDomain()
    {
        try
        {
            return L3.SandboxApi.ReadSyncFolders();
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_create_cloud_folder", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("【默认只预演】在云端新建一个同步文件夹，走客户端自己的实现（不需要任何第三方凭据）。放行条件与绑定相同：解锁令牌 + confirm=true + dryRun=false。返回新沙箱的 sandboxId / magic / pathPerms，可直接喂给 nutstore_l3_bind 绑到本地。")]
    public static string L3CreateCloudFolder(
        [Description("云端文件夹名（不能含路径分隔符等非法字符）")] string name,
        [Description("描述，可留空")] string? description = null,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true（只预演不执行）")] bool dryRun = true)
    {
        try
        {
            var refusals = new List<string>();
            var unlock = PluginEntry.IsWriteUnlocked();

            if (!unlock)
            {
                refusals.Add(
                    "L3 写入未解锁。解锁方式：把内容为 yes 的令牌文件放到"
                    + " %LOCALAPPDATA%\\NutstoreMcp\\l3-write.enabled。撤销只需删掉它。");
            }

            if (!confirm)
            {
                refusals.Add("调用方没有传 confirm=true。");
            }

            var nameError = L3.CloudFolderName.Validate(name);
            if (nameError is not null)
            {
                refusals.Add(nameError);
            }

            // 建云端文件夹是**远程写**，即使云端没有同名项也不会覆盖任何东西，
            // 但它会真实落到用户的云盘里 —— 所以和绑定用同一把锁。
            string? result = null;
            var executed = false;

            if (refusals.Count == 0 && !dryRun)
            {
                var ui = PluginEntry.Ui;
                if (ui is null)
                {
                    refusals.Add("UI 钩子不可用 —— 无法调用客户端实现。");
                }
                else
                {
                    try
                    {
                        result = ui.CreateCloudSyncFolder(name, description ?? string.Empty);
                        executed = true;
                    }
                    catch (Exception ex)
                    {
                        refusals.Add(
                            $"调用客户端实现失败（钩子可能是旧版本，需要重启客户端）: "
                            + $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                allowed = refusals.Count == 0,
                wouldWrite = refusals.Count == 0 && !dryRun && executed,
                dryRun,
                unlockVariablePresent = unlock,
                probe = new { name, description = description ?? string.Empty },
                refusals,
                notes = new[]
                {
                    "本操作只在云端**新建**一个文件夹，不删除、不移动、不修改任何已有内容。",
                    "建好后可用 nutstore_l3_bind 把它绑到本地空目录。",
                },
                executed,
                result,
            }, Indented);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_list_cloud_dir", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("列出云端某个目录下的内容，走客户端自己的实现（不需要任何第三方凭据）。只读操作，不需要解锁令牌。沙箱 ID 与 magic 从 nutstore_l3_sync_folders 或 nutstore_l3_bind_candidates 取。")]
    public static string L3ListCloudDir(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识（magic）")] long magic,
        [Description("云端目录路径，必须以 / 开头；留空表示沙箱根目录")] string? directory = null)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ListCloudDirectory(sandboxId, magic, directory ?? "/");
        }
        catch (Exception ex)
        {
            // 钩子没有这个方法（旧版本）时抛 RemotingException
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ListCloudDirectory —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_read_settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("读出客户端当前的设置项（开机自动启动、同步完成后关机、高级安全验证、文件保护锁等），走客户端自己的 IConfigureService。只读，不修改任何设置，不需要解锁令牌。")]
    public static string L3ReadSettings()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ReadClientSettings();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadClientSettings —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_write_setting", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("【默认只预演】修改一个客户端设置项，走客户端自己的 IConfigureService。只允许改白名单里的设置：普通开关（开机自启/同步完关机/气泡提醒/直接打开/体验版）需要 l3-write.enabled；系统级（文件保护锁/高级安全验证/暂停同步）额外需要 l3-write-system.enabled。")]
    public static string L3WriteSetting(
        [Description("设置项名，例如 AutoStart / IsShutdownOnIdle / EnableSafeGuard")] string name,
        [Description("目标值。布尔设置只接受 true / false")] string value,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true（只预演不执行）")] bool dryRun = true)
    {
        try
        {
            var refusals = new List<string>();
            var tier = L3.SettableSettings.TierOf(name);

            if (tier is null)
            {
                refusals.Add(
                    $"不允许修改 {name}。可改的设置有: "
                    + string.Join(", ", L3.SettableSettings.AllNames)
                    + "。其余设置项不在白名单里 —— 不接受任意属性名。");
            }

            var normalUnlocked = PluginEntry.IsWriteUnlocked();
            var systemUnlocked = PluginEntry.IsSystemWriteUnlocked();

            if (!normalUnlocked)
            {
                refusals.Add(
                    "L3 写入未解锁。把内容为 yes 的令牌文件放到 "
                    + "%LOCALAPPDATA%\\NutstoreMcp\\l3-write.enabled 即可。撤销只需删掉它。");
            }

            if (tier == "system" && !systemUnlocked)
            {
                refusals.Add(
                    $"{name} 是**系统级**设置，需要第二把锁："
                    + "把内容为 yes 的令牌文件放到 "
                    + "%LOCALAPPDATA%\\NutstoreMcp\\l3-write-system.enabled。"
                    + "与普通写入分开是有意的 —— 共用一把锁的话，"
                    + "「为了改开机自启而解锁」就等于同时允许装驱动。"
                    + (L3.SettableSettings.SystemLevel.TryGetValue(name, out var why) ? " 影响: " + why : string.Empty));
            }

            if (!confirm)
            {
                refusals.Add("调用方没有传 confirm=true。");
            }

            // 值的形状先挡一道，免得把明显不对的东西交给客户端
            if (tier == "normal" || tier == "system")
            {
                var looksBoolean = value is "true" or "false" or "True" or "False" or "TRUE" or "FALSE";
                if (!looksBoolean)
                {
                    // 白名单里的都是布尔开关；非布尔值先拒绝并说明
                    refusals.Add(
                        $"白名单里的设置项都是布尔开关，但收到的是 \"{value}\"。"
                        + "只接受 true / false。");
                }
            }

            string? result = null;
            var executed = false;

            if (refusals.Count == 0 && !dryRun)
            {
                var ui = PluginEntry.Ui;
                if (ui is null)
                {
                    refusals.Add("UI 钩子不可用 —— 无法调用客户端实现。");
                }
                else
                {
                    try
                    {
                        result = ui.WriteClientSetting(name, value);
                        executed = true;
                    }
                    catch (Exception ex)
                    {
                        refusals.Add(
                            $"调用客户端实现失败（钩子可能是旧版本，需要重启客户端）: "
                            + $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                allowed = refusals.Count == 0,
                wouldWrite = refusals.Count == 0 && !dryRun && executed,
                dryRun,
                tier,
                unlocked = new { normal = normalUnlocked, system = systemUnlocked },
                probe = new { name, value },
                refusals,
                notes = new[]
                {
                    "改动会写入客户端的设置存储，与在设置界面里点一下等价。",
                    tier == "system"
                        ? "这是系统级设置：客户端会自己决定是否需要重启或安装驱动。"
                        : "普通开关，改错了再改回来即可。",
                },
                executed,
                result,
            }, Indented);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_verify_call_sites", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("校验我们依赖的客户端方法是否还在（含混淆过的哈希名）。客户端升级后混淆名可能变化 —— 那个失败如果等到真要写的时候才发现，代价高得多。只读，不调用任何方法。")]
    public static string L3VerifyCallSites()
    {
        try
        {
            var results = new List<object>();
            var gone = new List<string>();

            foreach (var site in L3.ClientCallSites.Registered)
            {
                var type = L3.ClientAssemblies.FindType(site.TypeName);

                var present = false;
                var overloads = 0;
                var detail = "类型不存在";

                if (type is not null)
                {
                    // 不能用不带参数类型的 GetMethod(name, flags) ——
                    // 有重载时会抛 AmbiguousMatchException。
                    // c05097d3... 就有两个重载（NutstorePath 版和 SandboxIdentifier 版）。
                    var found = type
                        .GetMethods(System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Static
                            | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name == site.MethodName)
                        .ToList();

                    overloads = found.Count;
                    present = overloads > 0;

                    detail = overloads switch
                    {
                        0 => "类型在但方法不在",
                        1 => "在（1 个重载）",
                        _ => $"在（{overloads} 个重载 —— 调用时必须按参数类型精确取，"
                           + "不能用不带类型的 GetMethod）",
                    };
                }

                if (!present)
                {
                    gone.Add($"{site.TypeName}.{site.MethodName}（{site.Purpose}）");
                }

                results.Add(new
                {
                    site.TypeName,
                    site.MethodName,
                    site.Purpose,
                    mutates = site.Mutates,
                    present,
                    overloads,
                    detail,
                });
            }

            return JsonSerializer.Serialize(new
            {
                ok = gone.Count == 0,
                checkedCount = results.Count,
                missing = gone,
                callSites = results,
                notes = gone.Count == 0
                    ? new[] { "全部调用点都还在。客户端升级后如果这里开始报缺失，就是混淆名变了 —— 需要重新反编译确认。" }
                    : new[]
                    {
                        "有调用点找不到了 —— 客户端多半升级了，混淆名变了。",
                        "在重新反编译确认之前，不要执行任何会写状态的 L3 操作。",
                    },
            }, Indented);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_l3_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("在云端按关键词搜索文件和文件夹（走客户端自己的实现）。只读，不需要解锁令牌。返回按同步文件夹分组的结果，每组含 sandboxId 和文件列表（路径、是否目录、修改时间）。")]
    public static string L3Search([Description("搜索关键词")] string keyword)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.SearchCloud(keyword);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 SearchCloud —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_usage", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读坚果云账号的容量与流量用量（走客户端自己的实现）。只读，不需要解锁令牌。返回已用/总容量（字节与人类可读两种）、账号类型、以及上传下载流量余量。")]
    public static string L3Usage()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ReadAccountUsage();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadAccountUsage —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_user_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读坚果云账号信息（昵称、账号类型、是否团队、在线编辑开关）。只读，不需要解锁令牌。客户端的基础设施端点（webdomain 等）不导出。")]
    public static string L3UserInfo()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ReadUserInfo();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadUserInfo —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_sync_folder_detail", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读某个同步文件夹的详情与权限（名称、描述、所有者、是否默认、是否独占、占用空间、以及各路径的权限）。只读，不需要解锁令牌。sandboxId 与 magic 从 nutstore_l3_sync_folders 取。")]
    public static string L3SyncFolderDetail(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识（magic）")] long magic)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ReadSyncFolderDetail(sandboxId, magic);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadSyncFolderDetail —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_file_versions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("列出某个本地文件在云端的历史版本（走客户端自己的实现）。只读，不需要解锁令牌。传本地文件路径即可 —— 会按路径匹配到对应的同步文件夹。返回每个版本的编号、操作类型、编辑者、时间和大小。")]
    public static string L3FileVersions([Description("本地文件的绝对路径")] string localFilePath)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    reason = "需要绝对的本地文件路径。",
                }, Indented);
            }

            // 用已经验证过的接口拿沙箱列表 —— 不在钩子里重复一遍数据库读取。
            //
            // 注意：JsonElement 只在 JsonDocument 存活期间有效。原来把 doc 放在
            // using 里、出了 try 再用 sandboxes，读的就是已释放的内存 ——
            var resolved = TryResolveSandbox(ui, localFilePath);
            if (resolved.Error is not null)
            {
                return resolved.Error;
            }

            return ui.ListFileVersions(resolved.Id, resolved.Magic, resolved.Root!, localFilePath);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ListFileVersions —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    /// <summary>
    /// 把本地文件路径解析到所属的同步文件夹。
    /// </summary>
    /// <remarks>
    /// A4（版本历史）和 A7（分享状态）都要这一步，抽出来避免两份拷贝漂移。
    /// 匹配时取**最长**的根 —— 同步根可以嵌套，取最短的会选错沙箱。
    ///
    /// 注意：JsonElement 只在 JsonDocument 存活期间有效，所以必须在 using 内
    /// 就把需要的字段提取成普通值。
    /// </remarks>
    private static (string? Root, long Id, long Magic, string? Error) TryResolveSandbox(
        Contract.IUiHook ui, string localFilePath)
    {
        var roots = new List<(string Root, long Id, long Magic)>();

        try
        {
            using var doc = JsonDocument.Parse(ui.ReadClientSyncFolders());

            if (!doc.RootElement.TryGetProperty("sandboxes", out var sandboxes))
            {
                return (null, 0, 0, JsonSerializer.Serialize(new
                {
                    ok = false,
                    reason = "读同步文件夹列表失败，无法确定这个文件属于哪个沙箱。",
                }, Indented));
            }

            foreach (var s in sandboxes.EnumerateArray())
            {
                var local = s.TryGetProperty("localPath", out var lp) ? lp.GetString() : null;
                if (string.IsNullOrWhiteSpace(local))
                {
                    continue;
                }

                var id = s.TryGetProperty("id", out var idEl)
                    && long.TryParse(idEl.GetString(), out var parsedId) ? parsedId : 0;

                var magic = s.TryGetProperty("magic", out var mEl)
                    && long.TryParse(mEl.GetString(), out var parsedMagic) ? parsedMagic : 0;

                if (id > 0 && magic > 0)
                {
                    roots.Add((local!.TrimEnd('\\', '/'), id, magic));
                }
            }
        }
        catch (Exception ex)
        {
            return (null, 0, 0, JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"解析同步文件夹列表失败: {ex.Message}",
            }, Indented));
        }

        (string Root, long Id, long Magic)? best = null;

        foreach (var (root, id, magic) in roots)
        {
            if (!localFilePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && !localFilePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(localFilePath, root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || root.Length > best.Value.Root.Length)
            {
                best = (root, id, magic);
            }
        }

        if (best is null)
        {
            return (null, 0, 0, JsonSerializer.Serialize(new
            {
                ok = false,
                reason = "这个路径不在任何已绑定的同步文件夹里。"
                       + "该操作需要文件属于某个本地已绑定的同步文件夹。",
                checkedSandboxes = roots.Count,
            }, Indented));
        }

        return (best.Value.Root, best.Value.Id, best.Value.Magic, null);
    }

    [McpServerTool(Name = "nutstore_l3_share_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读某个本地文件在云端的分享状态（分享链接、是否设了密码、过期时间、是否禁止下载等）。只读，不需要解锁令牌。传本地文件路径即可。**分享密码不导出**，只报是否设了密码。")]
    public static string L3ShareInfo([Description("本地文件的绝对路径")] string localFilePath)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
            {
                return JsonSerializer.Serialize(new { ok = false, reason = "需要绝对的本地文件路径。" }, Indented);
            }

            var resolved = TryResolveSandbox(ui, localFilePath);
            if (resolved.Error is not null)
            {
                return resolved.Error;
            }

            return ui.ReadShareInfo(resolved.Id, resolved.Magic, resolved.Root!, localFilePath);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadShareInfo —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_contacts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读坚果云的协作联系人列表（团队成员、已邀请、其他）。只读，不需要解锁令牌。")]
    public static string L3Contacts()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.ReadContacts();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadContacts —— 需要重启客户端让新钩子生效。",
            }, Indented);
        }
    }
    [McpServerTool(Name = "nutstore_bridge", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("通用反射桥：按脚本调用客户端的实现。**加这个是为了不再为每一个新能力重启客户端**。脚本一行一个操作，行号即槽位号（从 0 开始），后续行可用 #N 引用前面的结果。支持 NEW（构造）/ CALL（静态调用）/ INSTANCE（实例调用）/ GET（取成员）/ CHAIN（沿成员链取）/ ENUM。参数编码：s:<base64> 字符串、l:<数字> long、i:<数字> int、b:true/false 布尔、#<槽位> 引用、- null、无前缀按字符串。桥内部限制：只能操作客户端自己的程序集、危险方法名一律拒绝。")]
    public static string Bridge([Description("脚本，例如：CALL|A.c3795a08....|c915cc8f3fd71da2191b3d1dff9bbda1f")] string script)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.InvokeBridge(script);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 InvokeBridge —— 需要用一次重启装上桥。装好之后加新能力就不用再重启了。",
            }, Indented);
        }
    }
    [McpServerTool(Name = "nutstore_l3_object_detail", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读某个本地文件在云端的对象详情（云端 ID、版本号、大小、哈希、是否已删除、来源机器）。只读，不需要解锁令牌。传本地文件路径即可。")]
    public static string L3ObjectDetail([Description("本地文件的绝对路径")] string localFilePath)
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;

        try
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
                return JsonSerializer.Serialize(new { ok = false, reason = "需要绝对的本地文件路径。" }, Indented);

            var r = TryResolveSandbox(ui, localFilePath);
            if (r.Error is not null) return r.Error;

            return ui.ReadObjectDetail(r.Id, r.Magic, r.Root!, localFilePath);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadObjectDetail —— 需要一次重启装上。",
            }, Indented);
        }
    }

    [McpServerTool(Name = "nutstore_l3_latest_event", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("读某个同步文件夹的最新事件 ID（增量同步的游标）。只读，不需要解锁令牌。sandboxId 与 magic 从 nutstore_l3_sync_folders 取。")]
    public static string L3LatestEvent(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识（magic）")] long magic)
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;

        try
        {
            // localRoot 从同步文件夹列表里取 —— NSSandbox 需要它
            using var doc = JsonDocument.Parse(ui.ReadClientSyncFolders());
            if (!doc.RootElement.TryGetProperty("sandboxes", out var list))
                return JsonSerializer.Serialize(new { ok = false, reason = "读同步文件夹列表失败。" }, Indented);

            string? root = null;
            foreach (var s in list.EnumerateArray())
            {
                var id = s.TryGetProperty("id", out var i) && long.TryParse(i.GetString(), out var pid) ? pid : 0;
                if (id == sandboxId)
                {
                    root = s.TryGetProperty("localPath", out var lp) ? lp.GetString() : null;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(root))
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    reason = $"沙箱 {sandboxId} 没有本地路径 —— 事件 ID 需要该沙箱已绑定到本地。",
                }, Indented);

            return ui.ReadLatestEventId(sandboxId, magic, root!);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = $"{ex.GetType().Name}: {ex.Message}",
                note = "钩子没有 ReadLatestEventId —— 需要一次重启装上。",
            }, Indented);
        }
    }
    [McpServerTool(Name = "nutstore_client_search_types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按名字子串搜索坚果云客户端的所有类型（跨全部程序集）。用于在不知道类型全名时定位 API。只读，不执行任何客户端代码。")]
    public static string ClientSearchTypes(
        [Description("类型名/命名空间子串，例如 Sandbox")] string pattern,
        [Description("最多返回多少个，默认 80")] int limit = 80)
    {
        try
        {
            return L3.ClientAssemblies.SearchTypes(pattern, limit);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_client_reflect", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("反射探查坚果云客户端某个类型的构造函数/方法/属性签名（区分 static/instance、带参数类型）。用于在不确定 API 形状时先看清楚再调用。只读，不执行任何客户端代码。")]
    public static string ClientReflect(
        [Description("完整类型名，例如 NutstoreClient.DAO.NSSandboxDAO")] string typeName,
        [Description("成员名子串过滤，留空返回全部")] string? filter = null,
        [Description("每类成员最多返回多少个，默认 60")] int limit = 60)
    {
        try
        {
            return L3.ClientAssemblies.Reflect(typeName, filter ?? string.Empty, limit);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_plugin_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("插件自身的运行状态：版本、监听端口、宿主进程、日志位置、UI 钩子是否就绪。用于确认插件已被客户端加载。")]
    public static string Status()
    {
        var proc = Process.GetCurrentProcess();

        return JsonSerializer.Serialize(new
        {
            plugin = new
            {
                version = PluginEntry.Version,
                running = PluginEntry.IsRunning,
                port = PluginEntry.Port,
                endpoint = PluginEntry.Port > 0 ? $"http://127.0.0.1:{PluginEntry.Port}/mcp" : null,
                logPath = PluginEntry.LogPath,
                lastError = PluginEntry.LastError,
                domain = AppDomain.CurrentDomain.FriendlyName,
                uiHookAttached = PluginEntry.Ui is not null,
            },
            host = new
            {
                process = proc.ProcessName,
                pid = proc.Id,
                startedAt = proc.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                is64Bit = Environment.Is64BitProcess,
                workingSetMB = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1),
            },
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_client_assemblies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出**插件所在子 AppDomain** 里已加载的程序集。注意：客户端自己的程序集在默认域，这里看不到它们 —— 这是版本隔离的代价，不是故障。")]
    public static string ClientAssemblies(
        [Description("按名称子串过滤，留空返回全部")] string? filter = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => new
            {
                name = a.GetName().Name,
                version = a.GetName().Version?.ToString(),
            })
            .Where(a => a.name is not null)
            .Where(a => string.IsNullOrWhiteSpace(filter)
                        || a.name!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.name, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            domain = AppDomain.CurrentDomain.FriendlyName,
            note = "只包含本子域的程序集；客户端的程序集在默认域",
            count = assemblies.Count,
            filter = filter ?? string.Empty,
            assemblies,
        }, Indented);
    }

    [McpServerTool(Name = "nutstore_ui_windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("列出客户端当前打开的 WPF 窗口（在客户端默认域里枚举）。只读，不操作界面。")]
    public static string UiWindows()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.DescribeWindows();
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("导出某个客户端窗口的控件树。用来定位设置界面该把开关注入到哪里。只读。")]
    public static string UiTree(
        [Description("窗口类型名的子串，例如 Settings 或 MainWindow")] string windowType,
        [Description("最大深度，1-40，默认 6")] int maxDepth = 6)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.DescribeTree(windowType ?? string.Empty, maxDepth);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_find", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("在客户端窗口里按类型名/控件名/文本查找控件。只读，用于定位注入点。")]
    public static string UiFind(
        [Description("窗口类型名的子串")] string windowType,
        [Description("要查找的类型名、x:Name 或文本片段")] string query,
        [Description("最多返回多少个，1-200，默认 50")] int limit = 50)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.FindElements(windowType ?? string.Empty, query ?? string.Empty, limit);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_install_toggle", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("在客户端的设置页里放一个 MCP 开关（用客户端自己的 SettingItem 控件，外观与原生设置项一致）。幂等，重复调用不会出现两行。需要设置面板处于打开状态。")]
    public static string InstallToggle()
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            return ui.AddToggle(
                "MCP 服务器",
                "允许 AI 助手读取坚果云的同步状态。关闭后停止监听，重启客户端或切换开关可恢复。",
                PluginEntry.StateFilePath,
                PluginEntry.ReadEnabledState());
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_ui_dump", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("把客户端所有窗口的完整控件树导出到一个文件，返回文件路径。写文件，不改客户端状态。用于离线分析界面结构，避免反复重启客户端。")]
    public static string UiDump(
        [Description("导出目录，留空则用插件旁的 dumps 目录")] string? directory = null)
    {
        var ui = PluginEntry.Ui;
        if (ui is null)
        {
            return UiUnavailable;
        }

        try
        {
            var dir = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(PluginEntry.Home, "dumps")
                : directory!;

            return ui.DumpAll(dir);
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    [McpServerTool(Name = "nutstore_plugin_reload", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("热重载插件：桥会卸载子 AppDomain 并重新加载。改插件代码后用它，不必重启坚果云。注意：UI 钩子在客户端默认域，卸载不了，钩子的改动仍需重启客户端。")]
    public static string Reload()
    {
        var trigger = PluginEntry.RequestReload();

        return JsonSerializer.Serialize(new
        {
            requested = true,
            triggerFile = trigger,
            note = "桥最多 1.5 秒后重载。MCP 连接会断开，客户端需要重连。",
            limitation = "只重载插件；UI 钩子在默认域卸不掉，改它仍需重启客户端。",
        }, Indented);
    }

    private static string Error(Exception ex) => JsonSerializer.Serialize(new
    {
        ok = false,
        error = ex.GetType().Name,
        message = ex.Message,
    }, Indented);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
