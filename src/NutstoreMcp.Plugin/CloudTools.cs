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

    [McpServerTool(Name = "nutstore_l3_create_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("【默认只预演】上传内容并在云端创建一个新文件。需要写入解锁、confirm=true、dryRun=false。父目录有两种模式：**新建目录**（directoryName，沿用结构安全：只允许在**本次调用新建、且建后确认为空**的目录里创建，实测别名冲突会触发客户端改名，而改名的同内容/目录分支可能移动或删除既有对象）或**已有目录**（directoryPath，两端预检：云端父目录全量列举 + 本地绑定文件夹枚举，任一大小写等价即拒绝；提交元数据前再复查一次；写后确认父目录只多出目标、其余成员版本/哈希未变，并把云端字节整文件回读核对 SHA-256；父目录成员上限 100 项）。fileName 与新建目录名各为一个路径段。**空文件**传 contentBase64 为空字符串（走客户端空文件分支：跳过内容上传、云端 hash 用 32 个零字节）；非空内联 base64 不超过 256 KiB；本地文件来源（sourcePath）不超过 16 MiB，超过 393216 字节时走客户端分块上传（按 4 MiB 块 + 块列表 + 多块云端 hash），本地文件来源**默认关闭**（要先把可信目录一行一个写进 %LOCALAPPDATA%\\NutstoreMcp\\read-roots.txt；本地文件只在真正执行时读取，预演不读内容）。失败或未知只读复查，不重试、不回滚、不删除。")]
    public static string L3CreateFile(
        [Description("沙箱 ID")] long sandboxId,
        [Description("沙箱的内部标识")] long magic,
        [Description("新文件名，单个路径段，不能含路径分隔符")] string fileName,
        [Description("本次新建的目录名，单个路径段；与 directoryPath 二选一")] string? directoryName = null,
        [Description("已存在的云端目录路径，以 / 开头（/ 表示沙箱根）；与 directoryName 二选一")] string? directoryPath = null,
        [Description("文件内容的 base64；与 sourcePath 二选一。空字符串表示创建空文件；非空时解码后不超过 256 KiB")] string? contentBase64 = null,
        [Description("要上传的本地文件绝对路径；与 contentBase64 二选一，必须落在读取白名单内，且不超过 16 MiB")] string? sourcePath = null,
        [Description("必须显式传 true 才可能执行")] bool confirm = false,
        [Description("默认 true，只预演")] bool dryRun = true)
    {
        var ui = PluginEntry.Ui;
        if (ui is null) return UiUnavailable;

        // contentBase64 只看「有没有给」：显式给空字符串是合法的空文件请求，
        // 不能与「没给」混为一谈（否则空文件无法表达）。
        var hasInline = contentBase64 is not null;
        var hasFile = !string.IsNullOrWhiteSpace(sourcePath);

        if (hasInline == hasFile)
        {
            return JsonSerializer.Serialize(
                new { ok = false, reason = "contentBase64 与 sourcePath 必须且只能给一个；创建空文件请显式传 contentBase64=\"\"。" }, Indented);
        }

        // 父目录二选一：本次新建（directoryName）或已存在（directoryPath）。
        var hasNewDirectory = directoryName is not null;
        var hasExistingDirectory = directoryPath is not null;
        if (hasNewDirectory == hasExistingDirectory)
        {
            return JsonSerializer.Serialize(
                new { ok = false, reason = "directoryName（本次新建目录）与 directoryPath（已存在目录）必须且只能给一个。" }, Indented);
        }

        // 内联模式：先按编码长度挡一次再解码；本地文件模式：只校验路径与大小，**不读内容**
        // （内容留到真正执行那一步，预演不碰文件内容）。
        string encoded = string.Empty;
        int contentLength;
        string contentSource;
        string? sourceFilePath = null;
        long sourceFileLength = 0;
        Func<string> readContent = () => string.Empty;
        Func<string> expectedSha256;

        if (hasInline)
        {
            encoded = contentBase64 ?? string.Empty;

            // 解码发生在守卫之前，不先挡的话一个远超上限的请求会先被完整解进内存才被拒绝。
            var lengthError = CloudWriteOperations.ValidateEncodedLength(encoded.Length);
            if (lengthError is not null)
                return JsonSerializer.Serialize(new { ok = false, reason = lengthError }, Indented);

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return JsonSerializer.Serialize(new { ok = false, reason = "内容不是有效的 base64。" }, Indented);
            }

            contentLength = decoded.Length;
            contentSource = "inline-base64";
            var payload = encoded;
            readContent = () => payload;

            // 内容是不变的字符串，期望的 SHA-256 在这里算一次即可。
            var sha = CloudContentCodec.Sha256Hex(decoded);
            expectedSha256 = () => sha;
        }
        else
        {
            var roots = LocalReadPolicy.ParseRoots(
                LocalReadPolicy.TryReadRootsFile(PluginEntry.ReadRootsFilePath));

            var policyError = LocalReadPolicy.Validate(
                sourcePath, roots, CloudContentCodec.MaxFileUploadBytes, out var canonicalPath, out var fileLength);

            if (policyError is not null)
                return JsonSerializer.Serialize(new { ok = false, reason = policyError }, Indented);

            contentLength = checked((int)fileLength);
            contentSource = "file:" + canonicalPath;
            sourceFilePath = canonicalPath!;
            sourceFileLength = fileLength;

            // 回读校验要在写完之后再读一次本地文件：上传与校验之间文件被改动就会不一致，
            // 那正是要报告而不是猜的情形。
            var pathForHash = sourceFilePath;
            var lengthForHash = sourceFileLength;
            expectedSha256 = () => CloudContentCodec.Sha256Hex(
                LocalReadPolicy.ReadExactly(pathForHash!, lengthForHash));
        }

        try
        {
            var bindings = ui.ReadClientSyncFolders();
            var root = BoundRoot(bindings, sandboxId, magic);
            var contentLimit = sourceFilePath is not null
                ? CloudContentCodec.MaxFileUploadBytes
                : CloudWriteOperations.MaxContentBytes;

            if (hasNewDirectory)
            {
                var directory = "/" + directoryName;
                var target = directory + "/" + fileName;
                Func<string> createFile = sourceFilePath is not null
                    ? () => ui.CreateCloudFileFromLocalFile(sandboxId, magic, root ?? string.Empty, target, sourceFilePath, sourceFileLength)
                    : () => ui.CreateCloudFile(sandboxId, magic, root ?? string.Empty, target, readContent());
                return CloudWriteOperations.CreateFile(
                    PluginEntry.IsWriteUnlocked(), confirm, dryRun, sandboxId, magic, directoryName!, fileName,
                    contentLength,
                    () => bindings,
                    path => ui.ListCloudDirectory(sandboxId, magic, path),
                    path => ui.ReadObjectDetail(sandboxId, magic, root ?? string.Empty, LocalObjectPath(root, path)),
                    () => ui.CreateCloudDirectory(sandboxId, magic, root ?? string.Empty, directory),
                    createFile,
                    contentSource,
                    maxContentBytes: contentLimit);
            }

            var existingDirectory = directoryPath!;
            var existingTarget = (existingDirectory == "/" ? string.Empty : existingDirectory) + "/" + fileName;
            Func<string> createFileInDirectory = sourceFilePath is not null
                ? () => ui.CreateCloudFileFromLocalFile(sandboxId, magic, root ?? string.Empty, existingTarget, sourceFilePath, sourceFileLength)
                : () => ui.CreateCloudFile(sandboxId, magic, root ?? string.Empty, existingTarget, readContent());
            return CloudWriteOperations.CreateFileInExistingDirectory(
                PluginEntry.IsWriteUnlocked(), confirm, dryRun, sandboxId, magic, existingDirectory, fileName,
                contentLength,
                () => bindings,
                path => ui.ListCloudDirectory(sandboxId, magic, path),
                path => ui.ReadObjectDetail(sandboxId, magic, root ?? string.Empty, LocalObjectPath(root, path)),
                (localRoot, cloudDir) => LocalDirectoryProbe.Inspect(localRoot, cloudDir),
                createFileInDirectory,
                (path, offset, length) => ui.ReadCloudFileInline(
                    sandboxId, magic, root ?? string.Empty, path, offset, length),
                expectedSha256,
                contentSource,
                maxContentBytes: contentLimit);
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
