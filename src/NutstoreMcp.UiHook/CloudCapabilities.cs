// 执行云端目录创建与权限查询。
using NutstoreMcp.ClientBindings;
using NutstoreMcp.Plugin.L3;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NutstoreMcp.UiHook;

// 云端建目录与子路径权限查询。
public sealed partial class UiHook
{
    private const int CapabilityMemberLimit = 200;

    /// <summary>
    /// 首轮创建文件的内容上限（256 KiB）。
    /// 两个客户端阈值都在它之上：393216 字节以下才走全量上传，4 MiB 才分块算云端 hash。
    /// 所以这个范围内「全量上传 + 单块 hash」两条路径都已在线核对过。
    /// </summary>
    private const int CapabilityMaxContentBytes = 262144;

    public string CreateCloudDirectory(long sandboxId, long magic, string localRoot, string cloudPath)
    {
        var attempted = false;
        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null) return CapabilityDirectoryFailure(cloudPath, validation, attempted);

            var api = FindClientType(CloudOpsTypeName)
                ?? throw new CapabilityFailureException("找不到云端操作层");
            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError)
                ?? throw new CapabilityFailureException(boxError);
            var path = CapabilityBuildPath(box, cloudPath);
            var slash = cloudPath.LastIndexOf('/');
            var parentPath = slash == 0 ? "/" : cloudPath.Substring(0, slash);
            var parent = CapabilityBuildPath(box, parentPath);

            var readDetail = CapabilityMethod(api, ClientSymbols.ReadObjectDetail, box.GetType(), path.GetType());
            var listDirectory = CapabilityMethod(api, ClientSymbols.ListDirectory, path.GetType(), typeof(string));
            var createDirectory = CapabilityMethod(api, ClientSymbols.CreateDirectory, path.GetType());

            if (parentPath != "/")
            {
                var parentDetail = readDetail.Invoke(null, new[] { box, parent });
                if (parentDetail is null || CapabilityDeleted(parentDetail) || !CapabilityIsDirectory(parentDetail)
                    || !string.Equals(CapabilityBeanPath(parentDetail), parentPath, StringComparison.Ordinal))
                    return CapabilityDirectoryFailure(cloudPath, "父目录不存在或不是有效目录；不递归创建父目录。", attempted);
            }

            // 全量列目录后再决定，任何同名对象（含大小写差异）都拒绝。
            var entries = CapabilityDirectoryPaths(listDirectory, parent);
            if (entries.Any(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase)))
                return CapabilityDirectoryFailure(cloudPath, "目标目录已有同名对象（忽略大小写），拒绝创建。", attempted);

            object? result = null;
            string? writeError = null;
            long? submittedVersion = null;
            attempted = true;
            try
            {
                result = createDirectory.Invoke(null, new[] { path });
                if (result is null) throw new CapabilityFailureException("创建方法返回 null");
                submittedVersion = CapabilityLong(CapabilityMember(result, "version"));
                if (submittedVersion <= 0) throw new CapabilityFailureException("创建结果没有有效版本号");
                var resultPath = CapabilityMember(result, "path");
                if (resultPath is null || !string.Equals(CapabilityPathText(resultPath), cloudPath, StringComparison.Ordinal))
                    throw new CapabilityFailureException("创建结果路径与请求不一致");
            }
            catch (Exception ex)
            {
                writeError = CapabilityError(ex);
            }

            // 写调用失败或超时也复查；只读取，不重试写入或回滚。
            var verification = CapabilityVerifyDirectory(readDetail, listDirectory, box, path, parent,
                cloudPath, submittedVersion);
            var ok = writeError is null && verification.Ok;
            return Json.Obj(
                ("ok", ok),
                ("attempted", attempted),
                ("cloudPath", cloudPath),
                ("parentPath", parentPath),
                ("version", submittedVersion),
                ("writeError", writeError),
                ("verification", new Json.Raw(verification.Json)),
                ("outcome", ok ? "created" : "unknown"),
                ("reason", ok ? null : "已尝试创建，但未同时确认调用成功与云端目录结果；先复查，再决定后续操作。"));
        }
        catch (Exception ex)
        {
            return CapabilityDirectoryFailure(cloudPath, "建目录检查失败：" + CapabilityError(ex), attempted);
        }
    }

    /// <summary>
    /// 上传内容并发布为云端新文件。
    /// </summary>
    /// <remarks>
    /// 安全性来自**调用方**：<c>cf02</c>（CreateFileMetadata）本体没有目标查重，
    /// 也没有 noReplace，所以本方法假定目标路径已由调用方确认为「全新且父目录为空」。
    /// 这里只额外做一次目标存在性检查当作纵深防御，不做覆盖判断。
    ///
    /// 只支持全量上传的小文件：客户端在 393216 字节以下走全量方案，云端 hash 的
    /// 单块公式也已在 49 字节与 53 字节两个真实对象上核对过；多块与大文件未验证。
    /// 空内容是另一条分支（跳过上传、块列表用 40 个零字节），同样未验证，一并拒绝。
    /// </remarks>
    public string CreateCloudFile(long sandboxId, long magic, string localRoot, string cloudPath, string base64Content)
    {
        var attempted = false;
        var contentUploaded = false;
        var metadataAttempted = false;
        long? submittedVersion = null;
        string? cloudHash = null;
        var size = 0;
        object? box = null;
        object? path = null;
        object? parent = null;
        MethodInfo? readDetail = null;
        MethodInfo? listDirectory = null;

        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null)
                return CapabilityCreateFailure(cloudPath, validation, attempted, contentUploaded, metadataAttempted);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64Content ?? string.Empty);
            }
            catch (FormatException)
            {
                return CapabilityCreateFailure(cloudPath, "内容不是有效的 base64", attempted, contentUploaded, metadataAttempted);
            }

            size = bytes.Length;
            if (size == 0)
                return CapabilityCreateFailure(cloudPath, "空文件走另一条客户端分支，首轮不支持", attempted, contentUploaded, metadataAttempted);
            if (size > CapabilityMaxContentBytes)
                return CapabilityCreateFailure(cloudPath,
                    "首轮只支持不超过 " + CapabilityMaxContentBytes + " 字节的内容（全量上传与单块 hash 都已验证的范围）",
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

            // 纵深防御：父目录必须是有效目录，目标必须不存在（含大小写别名）。
            var parentDetail = readDetail.Invoke(null, new[] { box, parent });
            if (parentDetail is null || CapabilityDeleted(parentDetail) || !CapabilityIsDirectory(parentDetail)
                || !string.Equals(CapabilityBeanPath(parentDetail), parentPath, StringComparison.Ordinal))
                return CapabilityCreateFailure(cloudPath, "父目录不存在或不是有效目录", attempted, contentUploaded, metadataAttempted);
            if (CapabilityDirectoryPaths(listDirectory, parent)
                .Any(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase)))
                return CapabilityCreateFailure(cloudPath, "目标已存在（含大小写别名），拒绝发布。", attempted, contentUploaded, metadataAttempted);

            var identifierType = FindClientType(ClientSymbols.SandboxIdentifierType)
                ?? throw new CapabilityFailureException("找不到 SandboxIdentifier");
            var compressionType = FindClientType(ClientSymbols.CompressionMethodType)
                ?? throw new CapabilityFailureException("找不到 CompressionMethod");
            var paramType = FindClientType(ClientSymbols.CreateFileParamType)
                ?? throw new CapabilityFailureException("找不到 CreateFileParam");

            var upload = CapabilityMethod(api, ClientSymbols.UploadFullContent,
                identifierType, typeof(Stream), typeof(long), typeof(long), compressionType, typeof(string));
            var create = CapabilityMethod(api, ClientSymbols.CreateFileMetadata, paramType);

            cloudHash = CapabilityCloudHash(bytes);
            var md5 = CapabilityUrlSafeMd5(bytes);

            attempted = true;
            var identifier = Activator.CreateInstance(identifierType, sandboxId, magic)
                ?? throw new CapabilityFailureException("SandboxIdentifier 构造失败");
            var compression = Enum.Parse(compressionType, "NONE");
            object? uploaded;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                uploaded = upload.Invoke(null, new[] { identifier, (object)stream, (object)(long)size, (object)(long)size, compression, md5 });
            }

            // 上传入口的返回类型是 string：客户端内部已经把 ObjectNameResponse.name 解出来了，
            // 直接就是内容对象标识，原样填进 CreateFileParam，不自行变换编码。
            var contentName = uploaded as string;
            if (string.IsNullOrWhiteSpace(contentName))
                throw new CapabilityFailureException("上传没有返回内容对象标识");
            contentUploaded = true;

            var param = Activator.CreateInstance(paramType)
                ?? throw new CapabilityFailureException("CreateFileParam 构造失败");
            CapabilitySetMember(param, "path", path);
            CapabilitySetMember(param, "size", (long)size);
            CapabilitySetMember(param, "hashBase64", cloudHash);
            CapabilitySetMember(param, "fullObjectNameBase64", contentName);
            CapabilitySetMember(param, "blockListNameBase64", null);
            CapabilitySetMember(param, "compressible", false);
            CapabilitySetMember(param, "exAttr", null);

            metadataAttempted = true;
            var result = create.Invoke(null, new[] { param })
                ?? throw new CapabilityFailureException("发布方法返回 null");
            var version = CapabilityLong(CapabilityMember(result, "version"));
            if (version <= 0) throw new CapabilityFailureException("发布结果没有有效版本号");
            var resultPath = CapabilityMember(result, "path");
            if (resultPath is null || !string.Equals(CapabilityPathText(resultPath), cloudPath, StringComparison.Ordinal))
                throw new CapabilityFailureException("发布结果路径与请求不一致");
            submittedVersion = version;
        }
        catch (Exception ex)
        {
            return CapabilityCreateFailure(cloudPath, "创建文件失败：" + CapabilityError(ex),
                attempted, contentUploaded, metadataAttempted);
        }

        // 写后只读复查（默认域这一侧）；插件还会再复查一次。
        var verification = CapabilityVerifyFile(readDetail!, listDirectory!, box!, path!, parent!,
            cloudPath, size, cloudHash, submittedVersion);
        var ok = verification.Ok;
        return Json.Obj(
            ("ok", ok),
            ("attempted", attempted),
            ("contentUploaded", contentUploaded),
            ("metadataAttempted", metadataAttempted),
            ("metadataSubmitted", submittedVersion is not null),
            ("cloudPath", cloudPath),
            ("size", size),
            ("cloudHash", cloudHash),
            ("version", submittedVersion),
            ("verification", new Json.Raw(verification.Json)),
            ("outcome", ok ? "created" : "unknown"),
            ("reason", ok ? null : "已尝试发布，但没有同时确认调用成功与云端文件结果；只做只读复查，不重试、不回滚。"));
    }

    /// <summary>
    /// 从本地文件上传并发布为云端新文件。小文件走客户端全量上传，超过
    /// <see cref="CloudContentCodec.FullUploadThresholdBytes"/> 走分块上传。
    /// </summary>
    /// <remarks>
    /// 分块协议已在真实沙箱上逐字节核对过（单块 400 KiB、两块 4.3 MiB）：
    /// 块上传的 name 用 URL-safe Base64(块记录)，块列表的 name 用 URL-safe Base64(文件记录)，
    /// 块列表体是各块 URL-safe Base64(块记录) + CRLF；元数据里 hashBase64 用普通
    /// Base64(文件摘要)，blockListNameBase64 用普通 Base64(文件记录)。
    /// 空文件与超过 <see cref="CloudContentCodec.MaxFileUploadBytes"/> 的文件拒绝。
    /// 目标存在性仍由调用方保证：本方法只做一次纵深防御检查，不做覆盖与回滚。
    /// </remarks>
    public string CreateCloudFileFromLocalFile(long sandboxId, long magic, string localRoot, string cloudPath,
        string localFilePath, long expectedSize)
    {
        var attempted = false;
        var contentUploaded = false;
        var metadataAttempted = false;
        long? submittedVersion = null;
        string? cloudHash = null;
        var size = 0L;
        object? box = null;
        object? path = null;
        object? parent = null;
        MethodInfo? readDetail = null;
        MethodInfo? listDirectory = null;

        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null)
                return CapabilityCreateFailure(cloudPath, validation, attempted, contentUploaded, metadataAttempted);

            if (expectedSize <= 0)
                return CapabilityCreateFailure(cloudPath, "空文件走另一条客户端分支，暂不支持",
                    attempted, contentUploaded, metadataAttempted);
            if (expectedSize > CloudContentCodec.MaxFileUploadBytes)
                return CapabilityCreateFailure(cloudPath,
                    "本地文件超过 " + CloudContentCodec.MaxFileUploadBytes + " 字节上限",
                    attempted, contentUploaded, metadataAttempted);
            if (string.IsNullOrWhiteSpace(localFilePath) || !Path.IsPathRooted(localFilePath))
                return CapabilityCreateFailure(cloudPath, "本地文件路径必须是完整绝对路径",
                    attempted, contentUploaded, metadataAttempted);

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(localFilePath);
            }
            catch (Exception ex)
            {
                return CapabilityCreateFailure(cloudPath, "本地文件路径不合法：" + ex.GetType().Name,
                    attempted, contentUploaded, metadataAttempted);
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception ex)
            {
                return CapabilityCreateFailure(cloudPath, "读不到本地文件信息：" + ex.GetType().Name,
                    attempted, contentUploaded, metadataAttempted);
            }

            if ((attributes & FileAttributes.Directory) != 0)
                return CapabilityCreateFailure(cloudPath, "本地路径是目录，不是文件",
                    attempted, contentUploaded, metadataAttempted);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return CapabilityCreateFailure(cloudPath, "本地文件是符号链接或目录联接，拒绝上传",
                    attempted, contentUploaded, metadataAttempted);

            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return CapabilityCreateFailure(cloudPath, "本地文件不存在",
                    attempted, contentUploaded, metadataAttempted);
            if (info.Length != expectedSize)
                return CapabilityCreateFailure(cloudPath,
                    "本地文件长度在预检后发生变化（期望 " + expectedSize + "，实际 " + info.Length + "）",
                    attempted, contentUploaded, metadataAttempted);

            size = expectedSize;

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

            // 纵深防御：父目录必须是有效目录，目标必须不存在（含大小写别名）。
            var parentDetail = readDetail.Invoke(null, new[] { box, parent });
            if (parentDetail is null || CapabilityDeleted(parentDetail) || !CapabilityIsDirectory(parentDetail)
                || !string.Equals(CapabilityBeanPath(parentDetail), parentPath, StringComparison.Ordinal))
                return CapabilityCreateFailure(cloudPath, "父目录不存在或不是有效目录",
                    attempted, contentUploaded, metadataAttempted);
            if (CapabilityDirectoryPaths(listDirectory, parent)
                .Any(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase)))
                return CapabilityCreateFailure(cloudPath, "目标已存在（含大小写别名），拒绝发布。",
                    attempted, contentUploaded, metadataAttempted);

            var identifierType = FindClientType(ClientSymbols.SandboxIdentifierType)
                ?? throw new CapabilityFailureException("找不到 SandboxIdentifier");
            var compressionType = FindClientType(ClientSymbols.CompressionMethodType)
                ?? throw new CapabilityFailureException("找不到 CompressionMethod");
            var paramType = FindClientType(ClientSymbols.CreateFileParamType)
                ?? throw new CapabilityFailureException("找不到 CreateFileParam");
            var create = CapabilityMethod(api, ClientSymbols.CreateFileMetadata, paramType);

            string? fullObjectName = null;
            string? blockListName = null;

            // 记录、MD5 与上传字节必须来自**同一个只读句柄**：分两次打开文件时，
            // 「算完 hash、上传前文件被改动」会让云端 hash 与实际内容不一致。
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var record = CloudContentCodec.ComputeFileRecord(stream);
                if (record.Size != size)
                    return CapabilityCreateFailure(cloudPath, "本地文件记录长度与预检不一致",
                        attempted, contentUploaded, metadataAttempted);
                cloudHash = record.HashBase64;

                var identifier = Activator.CreateInstance(identifierType, sandboxId, magic)
                    ?? throw new CapabilityFailureException("SandboxIdentifier 构造失败");
                var compression = Enum.Parse(compressionType, "NONE");
                attempted = true;

                if (size <= CloudContentCodec.FullUploadThresholdBytes)
                {
                    // 小文件：客户端全量上传。MD5 用客户端自己的编码 helper，避免猜 URL-safe 规则。
                    stream.Position = 0;
                    var md5 = CapabilityUrlSafeMd5FromStream(stream);
                    var upload = CapabilityMethod(api, ClientSymbols.UploadFullContent,
                        identifierType, typeof(Stream), typeof(long), typeof(long), compressionType, typeof(string));
                    stream.Position = 0;
                    var uploaded = upload.Invoke(null,
                        new[] { identifier, (object)stream, (object)size, (object)size, compression, md5 });

                    fullObjectName = uploaded as string;
                    if (string.IsNullOrWhiteSpace(fullObjectName))
                        throw new CapabilityFailureException("全量上传没有返回内容对象标识");
                }
                else
                {
                    // 大文件：逐块上传 + 块列表，最后用块列表名发布元数据。
                    var uploadBlock = CapabilityMethod(api, ClientSymbols.UploadBlock,
                        typeof(Stream), typeof(long), typeof(string), compressionType);
                    var uploadBlockList = CapabilityMethod(api, ClientSymbols.UploadBlockList,
                        typeof(string), typeof(string));

                    foreach (var block in record.Blocks)
                    {
                        stream.Position = block.Offset;
                        uploadBlock.Invoke(null, new object[]
                        {
                            stream, (long)block.Length, CloudContentCodec.ToUrlSafeBase64(block.Record), compression,
                        });
                    }

                    uploadBlockList.Invoke(null, new object[] { record.BlockListBody, record.UrlSafeBlockListName });
                    blockListName = record.BlockListNameBase64;
                }
            }

            contentUploaded = true;

            var param = Activator.CreateInstance(paramType)
                ?? throw new CapabilityFailureException("CreateFileParam 构造失败");
            CapabilitySetMember(param, "path", path);
            CapabilitySetMember(param, "size", size);
            CapabilitySetMember(param, "hashBase64", cloudHash);
            CapabilitySetMember(param, "fullObjectNameBase64", fullObjectName);
            CapabilitySetMember(param, "blockListNameBase64", blockListName);
            CapabilitySetMember(param, "compressible", false);
            CapabilitySetMember(param, "exAttr", null);

            metadataAttempted = true;
            var result = create.Invoke(null, new[] { param })
                ?? throw new CapabilityFailureException("发布方法返回 null");
            var version = CapabilityLong(CapabilityMember(result, "version"));
            if (version <= 0) throw new CapabilityFailureException("发布结果没有有效版本号");
            var resultPath = CapabilityMember(result, "path");
            if (resultPath is null || !string.Equals(CapabilityPathText(resultPath), cloudPath, StringComparison.Ordinal))
                throw new CapabilityFailureException("发布结果路径与请求不一致");
            submittedVersion = version;
        }
        catch (Exception ex)
        {
            return CapabilityCreateFailure(cloudPath, "创建文件失败：" + CapabilityError(ex),
                attempted, contentUploaded, metadataAttempted);
        }

        // 写后只读复查（默认域这一侧）；插件还会再复查一次。
        var verification = CapabilityVerifyFile(readDetail!, listDirectory!, box!, path!, parent!,
            cloudPath, size, cloudHash, submittedVersion);
        var ok = verification.Ok;
        return Json.Obj(
            ("ok", ok),
            ("attempted", attempted),
            ("contentUploaded", contentUploaded),
            ("metadataAttempted", metadataAttempted),
            ("metadataSubmitted", submittedVersion is not null),
            ("cloudPath", cloudPath),
            ("size", size),
            ("cloudHash", cloudHash),
            ("version", submittedVersion),
            ("verification", new Json.Raw(verification.Json)),
            ("outcome", ok ? "created" : "unknown"),
            ("reason", ok ? null : "已尝试发布，但没有同时确认调用成功与云端文件结果；只做只读复查，不重试、不回滚。"));
    }

    private static string CapabilityCreateFailure(string cloudPath, string reason, bool attempted,
        bool contentUploaded, bool metadataAttempted)
        => Json.Obj(("ok", false), ("attempted", attempted),
            ("contentUploaded", contentUploaded), ("metadataAttempted", metadataAttempted),
            ("metadataSubmitted", false), ("cloudPath", cloudPath),
            ("outcome", attempted ? "unknown" : "refused"), ("reason", reason),
            ("verification", new Json.Raw(Json.Obj(("ok", false), ("performed", false)))));

    private static (bool Ok, string Json) CapabilityVerifyFile(MethodInfo readDetail, MethodInfo listDirectory,
        object box, object path, object parent, string cloudPath, long size, string? cloudHash, long? submittedVersion)
    {
        object? detail = null;
        List<string>? entries = null;
        string? detailError = null;
        string? listingError = null;
        string? objectType = null;
        string? observedHash = null;
        long? observedSize = null;
        long? observedVersion = null;
        var directory = true;
        var deleted = true;
        var pathMatches = false;
        try
        {
            detail = readDetail.Invoke(null, new[] { box, path });
            if (detail is not null)
            {
                directory = CapabilityIsDirectory(detail);
                deleted = CapabilityDeleted(detail);
                objectType = CapabilityMember(detail, "objectType") as string;
                observedHash = CapabilityMember(detail, "hash") as string;
                observedSize = CapabilityMember(detail, "size") is { } rawSize ? CapabilityLong(rawSize) : null;
                observedVersion = CapabilityLong(CapabilityMember(detail, "version"));
                pathMatches = string.Equals(CapabilityBeanPath(detail), cloudPath, StringComparison.Ordinal);
            }
        }
        catch (Exception ex) { detailError = CapabilityError(ex); }

        try
        {
            entries = CapabilityDirectoryPaths(listDirectory, parent);
        }
        catch (Exception ex) { listingError = CapabilityError(ex); }

        var matches = entries?.Where(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase)).ToList();
        bool? listed = listingError is null && matches is not null
            ? matches.Count == 1 && string.Equals(matches[0], cloudPath, StringComparison.Ordinal)
            : null;
        var detailKnown = detailError is null;
        var sizeMatches = detailKnown && detail is not null && observedSize == size;
        var hashMatches = detailKnown && detail is not null && cloudHash is not null
            && string.Equals(observedHash, cloudHash, StringComparison.Ordinal);
        var versionMatches = detailKnown && detail is not null && submittedVersion.HasValue
            && observedVersion == submittedVersion;
        var ok = detailError is null && listingError is null && detail is not null && !directory && !deleted
            && pathMatches && listed == true && observedVersion > 0 && sizeMatches && hashMatches && versionMatches;
        return (ok, Json.Obj(
            ("ok", ok), ("performed", true),
            ("exists", detailKnown ? detail is not null : null),
            ("isDirectory", detailKnown && detail is not null ? directory : null),
            ("deleted", detailKnown && detail is not null ? deleted : null),
            ("pathMatches", detailKnown && detail is not null ? pathMatches : null),
            ("listedInParent", listed), ("sameNameCount", matches?.Count),
            ("objectType", detailKnown ? objectType : null),
            ("size", observedSize), ("sizeMatches", detailKnown && detail is not null ? sizeMatches : null),
            ("hashMatches", detailKnown && detail is not null ? hashMatches : null),
            ("version", observedVersion), ("versionMatches", detailKnown && detail is not null ? versionMatches : null),
            ("detailError", detailError), ("listingError", listingError)));
    }

    /// <summary>客户端的分块云端 hash：4 MiB 一块，单块时即 SHA256(SHA256(内容) || BE64(长度))。</summary>
    private static string CapabilityCloudHash(byte[] bytes) => CloudContentCodec.CloudHash(bytes);

    /// <summary>传输 MD5 用客户端自己的编码 helper，避免猜 URL-safe Base64 的 padding 规则。</summary>
    private static string CapabilityUrlSafeMd5(byte[] bytes)
    {
        var hashType = FindClientType(ClientSymbols.HashAlgorithmsType)
            ?? throw new CapabilityFailureException("找不到 HashAlgorithms");
        var stringUtils = FindClientType(ClientSymbols.StringUtilsType)
            ?? throw new CapabilityFailureException("找不到 StringUtils");
        var digest = CapabilityMethod(hashType, "GetMd5Digest", typeof(Stream)).Invoke(null, new object[] { new MemoryStream(bytes, writable: false) })
            ?? throw new CapabilityFailureException("MD5 计算返回 null");
        return CapabilityMethod(stringUtils, "BytesToURLSafeBase64String", typeof(byte[]))
            .Invoke(null, new[] { digest }) as string
            ?? throw new CapabilityFailureException("MD5 编码返回 null");
    }

    /// <summary>在给定只读流上计算传输 MD5（客户端自己的 hash helper + URL-safe 编码）。</summary>
    private static string CapabilityUrlSafeMd5FromStream(Stream stream)
    {
        var hashType = FindClientType(ClientSymbols.HashAlgorithmsType)
            ?? throw new CapabilityFailureException("找不到 HashAlgorithms");
        var stringUtils = FindClientType(ClientSymbols.StringUtilsType)
            ?? throw new CapabilityFailureException("找不到 StringUtils");
        var digest = CapabilityMethod(hashType, "GetMd5Digest", typeof(Stream)).Invoke(null, new object[] { stream });
        if (digest is null) throw new CapabilityFailureException("MD5 计算返回 null");
        return CapabilityMethod(stringUtils, "BytesToURLSafeBase64String", typeof(byte[]))
            .Invoke(null, new[] { digest }) as string
            ?? throw new CapabilityFailureException("MD5 编码返回 null");
    }

    private static void CapabilitySetMember(object target, string name, object? value)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field is not null) { field.SetValue(target, value); return; }
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null && property.CanWrite && property.GetIndexParameters().Length == 0)
        {
            property.SetValue(target, value);
            return;
        }
        throw new CapabilityFailureException("客户端参数缺少可写成员 " + name);
    }

    public string ReadPathAcl(long sandboxId, long magic, string localRoot, string cloudPath)
    {
        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: true);
            if (validation is not null) return Json.Obj(("ok", false), ("reason", validation));

            var api = FindClientType(CloudOpsTypeName)
                ?? throw new CapabilityFailureException("找不到云端操作层");
            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError)
                ?? throw new CapabilityFailureException(boxError);
            var path = CapabilityBuildPath(box, cloudPath);
            var method = CapabilityMethod(api, ClientSymbols.ReadPathAcl, path.GetType());
            var response = method.Invoke(null, new[] { path })
                ?? throw new CapabilityFailureException("权限方法返回 null");
            var inherit = CapabilityMember(response, "isInherit");
            if (inherit is not bool isInherit) throw new CapabilityFailureException("权限响应缺少继承标志");
            var acl = CapabilityMember(response, "acl")
                ?? throw new CapabilityFailureException("权限响应缺少 ACL");
            var signed = CapabilityPermissionJson(CapabilityMember(acl, "signed"));
            var users = CapabilityMember(acl, "users") as IDictionary
                ?? throw new CapabilityFailureException("权限响应缺少用户集合");
            var nicknames = CapabilityMember(acl, "userNicks") as IDictionary;
            var groups = CapabilityMember(acl, "groups") as IEnumerable
                ?? throw new CapabilityFailureException("权限响应缺少组集合");

            var userItems = new List<string>();
            var usersTruncated = false;
            foreach (DictionaryEntry entry in users)
            {
                if (userItems.Count == CapabilityMemberLimit)
                {
                    usersTruncated = true;
                    break;
                }

                if (entry.Key is not string account) throw new CapabilityFailureException("用户账号字段类型不符");
                userItems.Add(Json.Obj(
                    ("account", account),
                    ("nickname", nicknames is not null && nicknames.Contains(account) ? nicknames[account] as string : null),
                    ("permission", new Json.Raw(CapabilityPermissionJson(entry.Value)))));
            }

            var groupItems = new List<string>();
            var groupsTruncated = false;
            foreach (var group in groups)
            {
                if (groupItems.Count == CapabilityMemberLimit)
                {
                    groupsTruncated = true;
                    break;
                }

                if (group is null) throw new CapabilityFailureException("权限响应含空组");
                groupItems.Add(Json.Obj(
                    ("id", CapabilityLong(CapabilityMember(group, "Id"))),
                    ("name", CapabilityMember(group, "Name") as string),
                    ("permission", new Json.Raw(CapabilityPermissionJson(CapabilityMember(group, "Permission"))))));
            }

            return Json.Obj(
                ("ok", true),
                ("cloudPath", cloudPath),
                ("isInherit", isInherit),
                ("signed", new Json.Raw(signed)),
                ("users", Json.Array(userItems)),
                ("groups", Json.Array(groupItems)),
                ("userCount", users.Count),
                ("groupCount", (groups as ICollection)?.Count),
                ("usersTruncated", usersTruncated),
                ("groupsTruncated", groupsTruncated),
                ("truncated", usersTruncated || groupsTruncated),
                ("memberLimit", CapabilityMemberLimit));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", "子路径权限查询失败：" + CapabilityError(ex)));
        }
    }

    private static string CapabilityDirectoryFailure(string cloudPath, string reason, bool attempted)
        => Json.Obj(("ok", false), ("attempted", attempted), ("cloudPath", cloudPath),
            ("outcome", attempted ? "unknown" : "refused"), ("reason", reason),
            ("verification", new Json.Raw(Json.Obj(("ok", false), ("performed", false)))));

    private static (bool Ok, string Json) CapabilityVerifyDirectory(MethodInfo readDetail, MethodInfo listDirectory,
        object box, object path, object parent, string cloudPath, long? submittedVersion)
    {
        object? detail = null;
        List<string>? entries = null;
        string? detailError = null;
        string? listingError = null;
        string? objectType = null;
        long? observedVersion = null;
        var directory = false;
        var deleted = true;
        var pathMatches = false;
        try
        {
            detail = readDetail.Invoke(null, new[] { box, path });
            if (detail is not null)
            {
                directory = CapabilityIsDirectory(detail);
                deleted = CapabilityDeleted(detail);
                objectType = CapabilityMember(detail, "objectType") as string;
                observedVersion = CapabilityLong(CapabilityMember(detail, "version"));
                pathMatches = string.Equals(CapabilityBeanPath(detail), cloudPath, StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            detailError = CapabilityError(ex);
        }

        try
        {
            entries = CapabilityDirectoryPaths(listDirectory, parent);
        }
        catch (Exception ex)
        {
            listingError = CapabilityError(ex);
        }

        var matches = entries?.Where(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase)).ToList();
        bool? listed = listingError is null && matches is not null
            ? matches.Count == 1 && string.Equals(matches[0], cloudPath, StringComparison.Ordinal)
            : null;
        var detailKnown = detailError is null;
        bool? exists = detailKnown ? detail is not null : null;
        bool? directoryObserved = detailKnown && detail is not null ? directory : null;
        bool? deletedObserved = detailKnown && detail is not null ? deleted : null;
        bool? pathMatchesObserved = detailKnown && detail is not null ? pathMatches : null;
        bool? versionMatches = detailKnown && detail is not null && submittedVersion.HasValue && observedVersion.HasValue
            ? observedVersion == submittedVersion
            : null;
        var ok = detailError is null && listingError is null && detail is not null && directory && !deleted
            && pathMatches && listed == true && observedVersion > 0 && versionMatches == true;
        return (ok, Json.Obj(
            ("ok", ok), ("performed", true), ("exists", exists),
            ("isDirectory", directoryObserved), ("deleted", deletedObserved),
            ("pathMatches", pathMatchesObserved), ("listedInParent", listed), ("sameNameCount", matches?.Count),
            ("objectType", detailKnown ? objectType : null), ("version", detailKnown ? observedVersion : null),
            ("versionMatches", versionMatches),
            ("detailError", detailError), ("listingError", listingError)));
    }

    private static object CapabilityBuildPath(object box, string cloudPath)
    {
        var type = FindClientType(ClientSymbols.NutstorePathType)
            ?? throw new CapabilityFailureException("找不到 NutstorePath");
        var method = CapabilityMethod(type, "FromNutstorePath", box.GetType(), typeof(string));
        var path = method.Invoke(null, new[] { box, cloudPath })
            ?? throw new CapabilityFailureException("NutstorePath 构造返回 null");
        if (!string.Equals(CapabilityPathText(path), cloudPath, StringComparison.Ordinal))
            throw new CapabilityFailureException("客户端改写了云端路径，拒绝调用");
        return path;
    }

    private static List<string> CapabilityDirectoryPaths(MethodInfo method, object path)
    {
        var raw = method.Invoke(null, new object[] { path, null! }) as IEnumerable
            ?? throw new CapabilityFailureException("列目录返回非集合");
        var paths = new List<string>();
        foreach (var item in raw)
        {
            if (item is null) throw new CapabilityFailureException("目录列表含空项");
            var child = CapabilityMember(item, "NutstorePath")
                ?? throw new CapabilityFailureException("目录项缺少路径");
            paths.Add(CapabilityPathText(child).TrimEnd('/'));
        }
        return paths;
    }

    private static string CapabilityPathText(object path)
        => path.GetType().GetMethod("ToNutstorePath", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null)?.Invoke(path, null) as string
            ?? throw new CapabilityFailureException("对象缺少有效云端路径");

    private static string CapabilityBeanPath(object bean)
        => CapabilityMember(bean, "path") is byte[] bytes
            ? new UTF8Encoding(false, true).GetString(bytes)
            : throw new CapabilityFailureException("对象详情缺少路径字节");

    private static bool CapabilityDeleted(object bean)
        => CapabilityMember(bean, "deleted") is bool deleted
            ? deleted
            : throw new CapabilityFailureException("对象详情缺少删除标志");

    private static bool CapabilityIsDirectory(object bean)
    {
        var type = CapabilityMember(bean, "objectType") as string
            ?? throw new CapabilityFailureException("对象详情缺少类型");
        var command = FindClientType(ClientSymbols.DownstreamCommandType)
            ?? throw new CapabilityFailureException("找不到对象类型转换器");
        var parsed = CapabilityMethod(command, "GetObjectTypeFromStr", typeof(string)).Invoke(null, new object[] { type });
        return string.Equals(parsed?.ToString(), "DIRECTORY", StringComparison.Ordinal);
    }

    private static MethodInfo CapabilityMethod(Type type, string name, params Type[] parameters)
        => type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, binder: null,
            types: parameters, modifiers: null) ?? throw new CapabilityFailureException("找不到客户端方法 " + name);

    private static object? CapabilityMember(object target, string name)
    {
        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field is not null) return field.GetValue(target);
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
        throw new CapabilityFailureException("客户端响应缺少成员 " + name);
    }

    private static long CapabilityLong(object? value)
        => value is long number ? number : value is int integer ? integer
            : throw new CapabilityFailureException("客户端响应整数类型不符");

    private static string CapabilityPermissionJson(object? permission)
    {
        if (permission is null || !permission.GetType().IsEnum)
            throw new CapabilityFailureException("客户端响应权限类型不符");
        return Json.Obj(("name", permission.ToString()), ("value", Convert.ToInt32(permission, CultureInfo.InvariantCulture)));
    }

    private static string? CapabilityCheckArguments(long sandboxId, long magic, string localRoot,
        string cloudPath, bool allowRoot)
    {
        if (sandboxId <= 0 || magic <= 0) return "sandboxId / magic 必须为正数";
        if (string.IsNullOrWhiteSpace(localRoot) || !Path.IsPathRooted(localRoot)) return "localRoot 必须是完整绝对路径";
        var fullRoot = Path.GetFullPath(localRoot);
        if (!string.Equals(fullRoot, localRoot.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            return "localRoot 必须是规范化的完整绝对路径";
        return CapabilityValidateCloudPath(cloudPath, allowRoot);
    }

    public static string? CapabilityValidateCloudPath(string? cloudPath, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(cloudPath) || !cloudPath!.StartsWith("/", StringComparison.Ordinal))
            return "云端路径必须以 / 开头";
        if (cloudPath == "/") return allowRoot ? null : "不能创建沙箱根目录";
        if (cloudPath.EndsWith("/", StringComparison.Ordinal)) return "云端路径不能以 / 结尾";
        if (cloudPath.Any(c => char.IsControl(c) || c == '\\' || "<>:\"|?*".IndexOf(c) >= 0))
            return "云端路径包含非法字符";
        foreach (var name in cloudPath.Substring(1).Split('/'))
        {
            if (name.Length == 0 || name == "." || name == "..") return "云端路径不能含空段、. 或 ..";
            if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return "云端路径段首尾不能有空白";
            if (name.Length > 255 || name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                return "云端路径段过长或以空格、点结尾";
            var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && ((stem[3] >= '1' && stem[3] <= '9') || stem[3] is '¹' or '²' or '³')))
                return "云端路径包含 Windows 保留文件名";
        }
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(cloudPath) > ClientSymbols.CloudPathMaxUtf8Bytes)
                return "云端路径超过客户端 " + ClientSymbols.CloudPathMaxUtf8Bytes + " 字节限制";
        }
        catch (EncoderFallbackException)
        {
            return "云端路径包含无效 Unicode";
        }
        return null;
    }

    private static string CapabilityError(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        if (ex is CapabilityFailureException) return ex.Message;
        // 网络异常可能带会话信息，仅导出异常类型和结构化错误码。
        var code = Get(ex, "ErrorCode") as string;
        var status = Get(ex, "ReturnCode");
        return ex.GetType().Name + (string.IsNullOrEmpty(code) ? string.Empty : " (" + code + ")")
            + (status is null ? string.Empty : " [" + status + "]");
    }

    private sealed class CapabilityFailureException(string? message) : InvalidOperationException(message);
}
