// P3 C4：把新内容发布成已有云端文件的新版本。
//
// 安全性来自调用方（插件侧台账 + 版本核对）与本方法的二次核对：
//   · 只有当前详情是普通文件、路径逐字符一致、版本等于调用方给的值时才动手；
//   · 提交元数据前紧邻时刻再核对一次版本（上传内容与提交之间可能被别的写改变）；
//   · 提交后核对大小/哈希/版本，并读一次历史版本列表（旧版本应当仍在，可回退）。
// 失败或结果未知只做只读复查，不重试、不回滚。
using NutstoreMcp.ClientBindings;
using System.Collections;
using System.Reflection;

namespace NutstoreMcp.UiHook;

public sealed partial class UiHook
{
    /// <summary>把新内容发布成已有云端文件的新版本（C4）。</summary>
    public string ModifyCloudFile(long sandboxId, long magic, string localRoot, string cloudPath,
        long expectedVersion, string base64Content)
    {
        var attempted = false;
        var contentUploaded = false;
        var metadataAttempted = false;
        long? submittedVersion = null;
        string? cloudHash = null;
        var size = 0;
        var previousVersion = 0L;
        string? previousHash = null;
        object? box = null;
        object? path = null;
        object? parent = null;
        MethodInfo? readDetail = null;
        MethodInfo? listDirectory = null;
        string? historyJson = null;
        var historyFound = false;
        var historyCount = 0;

        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null)
                return CapabilityModifyFailure(cloudPath, validation, attempted, contentUploaded, metadataAttempted);

            if (expectedVersion <= 0)
                return CapabilityModifyFailure(cloudPath, "预期版本号必须为正数", attempted, contentUploaded, metadataAttempted);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64Content ?? string.Empty);
            }
            catch (FormatException)
            {
                return CapabilityModifyFailure(cloudPath, "内容不是有效的 base64", attempted, contentUploaded, metadataAttempted);
            }

            size = bytes.Length;
            if (size <= 0)
                return CapabilityModifyFailure(cloudPath, "空内容不能用来修改文件（空内容分支未验证）",
                    attempted, contentUploaded, metadataAttempted);
            if (size > CapabilityMaxContentBytes)
                return CapabilityModifyFailure(cloudPath,
                    "C4 第一版只支持不超过 " + CapabilityMaxContentBytes + " 字节的内联内容",
                    attempted, contentUploaded, metadataAttempted);

            var api = FindClientType(CloudOpsTypeName)
                ?? throw new CapabilityFailureException("找不到云端操作层");
            box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError)
                ?? throw new CapabilityFailureException(boxError);
            path = CapabilityBuildPath(box, cloudPath);
            var slash = cloudPath.LastIndexOf('/');
            var parentPath = slash == 0 ? "/" : cloudPath.Substring(0, slash);
            parent = CapabilityBuildPath(box, parentPath);

            readDetail = CapabilityMethod(api, ClientSymbols.ReadObjectDetail, box.GetType(), path.GetType());
            listDirectory = CapabilityMethod(api, ClientSymbols.ListDirectory, path.GetType(), typeof(string));

            // ── 写前只读核对：普通文件、路径一致、版本等于调用方看到的版本 ──
            var before = readDetail.Invoke(null, new[] { box, path });
            var beforeProblem = CapabilityValidateModifyBefore(before, cloudPath, expectedVersion,
                out previousVersion, out previousHash, out var previousSize);
            if (beforeProblem is not null)
                return CapabilityModifyFailure(cloudPath, beforeProblem, attempted, contentUploaded, metadataAttempted);

            if (previousHash is null || string.IsNullOrWhiteSpace(previousHash))
                return CapabilityModifyFailure(cloudPath, "当前对象缺少云端 hash，无法安全修改",
                    attempted, contentUploaded, metadataAttempted);

            var paramType = FindClientType(ClientSymbols.ModifyFileParamType)
                ?? throw new CapabilityFailureException("找不到 ModifyFileParam");
            var identifierType = FindClientType(ClientSymbols.SandboxIdentifierType)
                ?? throw new CapabilityFailureException("找不到 SandboxIdentifier");
            var compressionType = FindClientType(ClientSymbols.CompressionMethodType)
                ?? throw new CapabilityFailureException("找不到 CompressionMethod");
            var upload = CapabilityMethod(api, ClientSymbols.UploadFullContent,
                identifierType, typeof(Stream), typeof(long), typeof(long), compressionType, typeof(string));
            var modify = CapabilityMethod(api, ClientSymbols.ModifyFileMetadata, paramType);

            cloudHash = CapabilityCloudHash(bytes);
            var md5 = CapabilityUrlSafeMd5(bytes);

            attempted = true;
            var identifier = Activator.CreateInstance(identifierType, sandboxId, magic)
                ?? throw new CapabilityFailureException("SandboxIdentifier 构造失败");
            var compression = Enum.Parse(compressionType, "NONE");
            object? uploaded;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                uploaded = upload.Invoke(null,
                    new[] { identifier, (object)stream, (object)(long)size, (object)(long)size, compression, md5 });
            }

            var contentName = uploaded as string;
            if (string.IsNullOrWhiteSpace(contentName))
                throw new CapabilityFailureException("上传没有返回内容对象标识");
            contentUploaded = true;

            // ── 提交前紧邻复查：版本仍是调用方与预检看到的那个 ──
            var confirm = readDetail.Invoke(null, new[] { box, path });
            var confirmProblem = CapabilityValidateModifyBefore(confirm, cloudPath, expectedVersion, out _, out _, out _);
            if (confirmProblem is not null)
                throw new CapabilityFailureException("提交前复查：" + confirmProblem);

            var oldObject = CapabilityBuildFileObject(path, previousSize, previousVersion, previousHash);
            var param = Activator.CreateInstance(paramType)
                ?? throw new CapabilityFailureException("ModifyFileParam 构造失败");
            CapabilitySetMember(param, "oldObject", oldObject);
            CapabilitySetMember(param, "size", (long)size);
            CapabilitySetMember(param, "hashBase64", cloudHash);
            CapabilitySetMember(param, "fullObjectNameBase64", contentName);
            CapabilitySetMember(param, "deltaObjectNameBase64", null);
            CapabilitySetMember(param, "blockListNameBase64", null);
            CapabilitySetMember(param, "newVersion", previousVersion + 1);
            CapabilitySetMember(param, "compressible", false);
            CapabilitySetMember(param, "exAttr", null);

            metadataAttempted = true;
            var result = modify.Invoke(null, new[] { param })
                ?? throw new CapabilityFailureException("修改方法返回 null");
            var version = CapabilityLong(CapabilityMember(result, "version"));
            if (version <= 0) throw new CapabilityFailureException("修改结果没有有效版本号");
            submittedVersion = version;

            // 历史版本：旧版本应当仍在（C4 与 C5 的区别正在这里 —— 网页端可回退）。
            historyJson = CapabilityVersionsJson(api, path, previousVersion,
                out historyFound, out historyCount);
        }
        catch (Exception ex)
        {
            return CapabilityModifyFailure(cloudPath, "修改文件失败：" + CapabilityError(ex),
                attempted, contentUploaded, metadataAttempted);
        }

        var verification = CapabilityVerifyFile(readDetail!, listDirectory!, box!, path!, parent!,
            cloudPath, size, cloudHash, submittedVersion);
        var ok = verification.Ok && historyFound;
        return Json.Obj(
            ("ok", ok),
            ("attempted", attempted),
            ("contentUploaded", contentUploaded),
            ("metadataAttempted", metadataAttempted),
            ("metadataSubmitted", submittedVersion is not null),
            ("cloudPath", cloudPath),
            ("size", size),
            ("cloudHash", cloudHash),
            ("previousVersion", previousVersion),
            ("previousHash", previousHash),
            ("version", submittedVersion),
            ("verification", new Json.Raw(verification.Json)),
            ("history", new Json.Raw(historyJson ?? "null")),
            ("historyOk", historyFound),
            ("historyCount", historyCount),
            ("historyReason", historyFound ? null
                : "历史版本列表里没有修改前的版本 " + previousVersion + "，旧版本可能没有保留。"),
            ("outcome", ok ? "modified" : "unknown"),
            ("reason", ok ? null
                : "已提交新版本，但没有同时确认调用成功、文件结果与历史版本；只做只读复查，不重试、不回滚。"));
    }

    private static string? CapabilityValidateModifyBefore(object? bean, string cloudPath, long expectedVersion,
        out long version, out string? hash, out long size)
    {
        version = 0;
        hash = null;
        size = 0;
        if (bean is null) return "云端没有这个对象，无法修改。";
        if (CapabilityDeleted(bean)) return "对象已被标记删除，拒绝修改。";
        if (CapabilityIsDirectory(bean)) return "目标是目录；C4 第一版只修改普通文件。";
        if (!string.Equals(CapabilityBeanPath(bean), cloudPath, StringComparison.Ordinal))
            return "对象详情路径与请求不一致，拒绝修改。";

        version = CapabilityLong(CapabilityMember(bean, "version"));
        if (version != expectedVersion)
            return "当前版本 " + version + " 与 expectedVersion " + expectedVersion
                + " 不一致（对象已变化），请重新预演后再执行。";
        hash = CapabilityMember(bean, "hash") as string;
        size = CapabilityMember(bean, "size") is { } sizeValue ? CapabilityLong(sizeValue) : 0;
        return null;
    }

    /// <summary>把已存在的云端文件包装成 NutstoreObject（ModifyFileParam.oldObject 用）。</summary>
    private static object CapabilityBuildFileObject(object path, long size, long version, string hash)
    {
        var builderType = FindClientType(ClientSymbols.NutstoreObjectBuilderType)
            ?? throw new CapabilityFailureException("找不到 NutstoreObject.Builder");
        var fileBuilder = builderType.GetMethod(
            "FileBuilder",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { path.GetType(), typeof(long), typeof(long), typeof(long), typeof(long), typeof(string) },
            modifiers: null)
            ?? throw new CapabilityFailureException("找不到 FileBuilder(NutstorePath,long,long,long,long,string)");
        var builder = fileBuilder.Invoke(null, new object?[] { path, 0L, 0L, size, version, hash })
            ?? throw new CapabilityFailureException("FileBuilder 返回 null");
        var build = builderType.GetMethod("Build",
            BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new CapabilityFailureException("找不到 Builder.Build");
        return build.Invoke(builder, null) ?? throw new CapabilityFailureException("Build 返回 null");
    }

    /// <summary>读历史版本列表（只读）；旧的写入版本应当仍在（原生判断，不解析 JSON）。</summary>
    private static string? CapabilityVersionsJson(Type api, object path, long expectedVersion,
        out bool foundVersion, out int count)
    {
        foundVersion = false;
        count = 0;
        try
        {
            var method = api.GetMethod(
                ClientSymbols.ListFileVersions,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { path.GetType() },
                modifiers: null);
            if (method is null) return null;

            var raw = method.Invoke(null, new[] { path }) as IEnumerable;
            if (raw is null) return null;

            var items = new List<string>();
            foreach (var item in raw)
            {
                if (item is null) continue;
                count++;
                var version = AsLong(Get(item, "Version"));
                if (version == expectedVersion) foundVersion = true;
                items.Add(Json.Obj(
                    ("version", version.ToString()),
                    ("operation", Get(item, "Operation")?.ToString()),
                    ("timeRaw", AsLong(Get(item, "Time")).ToString())));
            }

            return Json.Obj(("ok", true), ("count", count),
                ("versions", new Json.Raw("[" + string.Join(",", items) + "]")));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", CapabilityError(ex)));
        }
    }

    private static string CapabilityModifyFailure(string cloudPath, string reason, bool attempted,
        bool contentUploaded, bool metadataAttempted)
        => Json.Obj(("ok", false), ("attempted", attempted),
            ("contentUploaded", contentUploaded), ("metadataAttempted", metadataAttempted),
            ("metadataSubmitted", false), ("cloudPath", cloudPath),
            ("outcome", attempted ? "unknown" : "refused"), ("reason", reason),
            ("verification", new Json.Raw(Json.Obj(("ok", false), ("performed", false)))));
}
