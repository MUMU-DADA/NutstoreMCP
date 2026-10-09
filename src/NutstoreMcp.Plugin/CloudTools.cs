using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NutstoreMcp.Plugin.Contract;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Plugin;

/// <summary>提供云端文件移动、目录创建和路径权限查询。</summary>
public sealed partial class PluginTools
{
    [McpServerTool(Name = "nutstore_l3_move", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("【默认只预演】在同一已绑定同步文件夹内移动或重命名普通文件。需要写入解锁、confirm=true、dryRun=false。源与目标按真实云端状态核对，已有目标（含大小写别名）拒绝覆盖，目录拒绝。执行后核对源消失、目标大小和哈希；冲突或结果未知不会自动重试。")]
    public static string L3Move(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识")] long magic,
        [Description("规范源云端路径，以 / 开头")] string srcCloudPath,
        [Description("不存在的目标云端路径，其父目录必须已存在")] string destCloudPath,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true，只预演")] bool dryRun = true)
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;
        try
        {
            var bindings = ui.ReadClientSyncFolders();
            var root = BoundRoot(bindings, sandboxId, magic);
            return CloudWriteOperations.Move(
                PluginEntry.IsWriteUnlocked(), confirm, dryRun, sandboxId, magic, srcCloudPath, destCloudPath,
                () => bindings,
                path => ui.ListCloudDirectory(sandboxId, magic, path),
                path => ui.ReadObjectDetail(sandboxId, magic, root ?? string.Empty, LocalObjectPath(root, path)),
                () => ui.MoveCloudObject(sandboxId, magic, root ?? string.Empty, srcCloudPath, destCloudPath));
        }
        catch (Exception ex) { return Error(ex); }
    }

    [McpServerTool(Name = "nutstore_l3_mkdir", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("【默认只预演】在已绑定同步文件夹中创建一个云端子目录。需要写入解锁、confirm=true、dryRun=false；父目录必须已存在，目标已存在一律拒绝，不递归创建。提交后只读核对目录真实存在；结果未知不会自动重试。")]
    public static string L3Mkdir(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识")] long magic,
        [Description("规范云端目录路径，以 / 开头，不能是根")] string cloudPath,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true，只预演")] bool dryRun = true)
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;
        try
        {
            var bindings = ui.ReadClientSyncFolders();
            var root = BoundRoot(bindings, sandboxId, magic);
            return CloudWriteOperations.Mkdir(
                PluginEntry.IsWriteUnlocked(), confirm, dryRun, sandboxId, magic, cloudPath,
                () => bindings,
                path => ui.ListCloudDirectory(sandboxId, magic, path),
                path => ui.ReadObjectDetail(sandboxId, magic, root ?? string.Empty, LocalObjectPath(root, path)),
                () => ui.CreateCloudDirectory(sandboxId, magic, root ?? string.Empty, cloudPath));
        }
        catch (Exception ex) { return Error(ex); }
    }

    [McpServerTool(Name = "nutstore_l3_path_acl", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("查询已绑定同步文件夹内某路径的权限继承状态、用户权限和组权限。只读，支持根路径；不修改权限，不导出客户端会话数据。成员清单最多各 200 项，超出会明确标注截断。")]
    public static string L3PathAcl(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识")] long magic,
        [Description("规范云端路径，以 / 开头；默认根")] string cloudPath = "/")
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;
        try
        {
            var pathError = CloudObjectPath.Validate(cloudPath, allowRoot: true);
            if (pathError is not null) return JsonSerializer.Serialize(new { ok = false, reason = pathError }, Indented);
            var root = BoundRoot(ui.ReadClientSyncFolders(), sandboxId, magic);
            if (root is null) return JsonSerializer.Serialize(new { ok = false, reason = "沙箱身份或真实本地绑定无法确认。" }, Indented);
            return ui.ReadPathAcl(sandboxId, magic, root, cloudPath);
        }
        catch (Exception ex) { return Error(ex); }
    }

    private static string LocalObjectPath(string? root, string cloudPath) =>
        string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, cloudPath.Substring(1).Replace('/', '\\'));

    private static string? BoundRoot(string bindings, long sandboxId, long magic)
    {
        if (sandboxId <= 0 || magic <= 0) return null;
        using var doc = JsonDocument.Parse(bindings);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !doc.RootElement.TryGetProperty("sandboxes", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var matches = list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("id", out var id) && long.TryParse(id.ToString(), out var parsed) && parsed == sandboxId).ToList();
        if (matches.Count != 1) return null;
        foreach (var item in matches)
        {
            static long Number(JsonElement item, string name) =>
                item.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), out var number) ? number : 0;
            if (Number(item, "id") != sandboxId || Number(item, "magic") != magic) continue;
            if (!item.TryGetProperty("localExists", out var exists) || exists.ValueKind != JsonValueKind.True) return null;
            var root = item.TryGetProperty("localPath", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) return null;
            var pathRoot = Path.GetPathRoot(root);
            return pathRoot is not null && (pathRoot.StartsWith("\\\\", StringComparison.Ordinal)
                || (pathRoot.Length >= 3 && pathRoot[1] == ':')) ? root : null;
        }
        return null;
    }
}
