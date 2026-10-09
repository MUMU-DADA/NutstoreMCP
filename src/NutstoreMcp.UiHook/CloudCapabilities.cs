// 执行云端目录创建与权限查询。
using NutstoreMcp.ClientBindings;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NutstoreMcp.UiHook;

// 云端建目录与子路径权限查询。
public sealed partial class UiHook
{
    private const int CapabilityMemberLimit = 200;

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
