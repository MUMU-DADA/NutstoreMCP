// 删除云端普通文件 —— 只给专用删除工具用。
//
// 通用反射桥仍然禁止删除入口（ClientSymbols.ForbiddenMethodNames 未改），
// 因为桥是用户可调用的通用通道；这条专用路径是唯一通道，且自带三重核对：
// 目标存在且是普通文件、路径逐字符一致、版本与云端 hash 与创建台账完全一致。
// 删除只做只读复查，失败或结果未知不重试、不恢复。
using NutstoreMcp.ClientBindings;
using System.Reflection;

namespace NutstoreMcp.UiHook;

public sealed partial class UiHook
{
    /// <summary>删除一个云端普通文件；调用方必须已完成创建台账与版本/哈希核对。</summary>
    public string DeleteCloudObject(long sandboxId, long magic, string localRoot, string cloudPath,
        long expectedVersion, string expectedHash)
    {
        var attempted = false;
        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null)
                return CapabilityDeleteFailure(cloudPath, validation, attempted);
            if (expectedVersion <= 0)
                return CapabilityDeleteFailure(cloudPath, "预期版本号必须为正数", attempted);
            if (string.IsNullOrWhiteSpace(expectedHash))
                return CapabilityDeleteFailure(cloudPath, "预期云端 hash 不能为空", attempted);

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

            // 删除前只读核对：目标存在、是普通文件、路径逐字符一致、版本与哈希完全匹配。
            var before = readDetail.Invoke(null, new[] { box, path });
            var beforeProblem = CapabilityValidateDeleteBefore(before, cloudPath, expectedVersion, expectedHash);
            if (beforeProblem is not null)
                return CapabilityDeleteFailure(cloudPath, beforeProblem, attempted);

            var delete = api.GetMethod(
                ClientSymbols.DeleteObject,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { path.GetType(), typeof(bool), typeof(long), typeof(string) },
                modifiers: null)
                ?? throw new CapabilityFailureException("找不到删除方法（混淆名可能变了）");

            attempted = true;
            string? writeError = null;
            try
            {
                // 客户端重载：DeleteObject(NutstorePath, isDirectory, version, extra)
                delete.Invoke(null, new object?[] { path, false, expectedVersion, string.Empty });
            }
            catch (Exception ex)
            {
                writeError = CapabilityError(ex);
            }

            // 写后只读复查：详情不再存在或已标记删除，父目录列表里也没有它。
            var verification = CapabilityVerifyDeleted(readDetail, listDirectory, box, path, parent, cloudPath);
            var ok = writeError is null && verification.Ok;
            return Json.Obj(
                ("ok", ok),
                ("attempted", attempted),
                ("cloudPath", cloudPath),
                ("expectedVersion", expectedVersion),
                ("writeError", writeError),
                ("before", new Json.Raw(CapabilityDeletedBeforeJson(before))),
                ("verification", new Json.Raw(verification.Json)),
                ("outcome", ok ? "deleted" : "unknown"),
                ("reason", ok ? null : "已尝试删除，但没有同时确认调用成功与云端结果；只做只读复查，不重试、不恢复。"));
        }
        catch (Exception ex)
        {
            return CapabilityDeleteFailure(cloudPath, "删除检查失败：" + CapabilityError(ex), attempted);
        }
    }

    /// <summary>删除前的核对：存在、普通文件、路径一致、版本与哈希匹配。返回拒绝说明或 null。</summary>
    private static string? CapabilityValidateDeleteBefore(object? bean, string cloudPath,
        long expectedVersion, string expectedHash)
    {
        if (bean is null)
            return "云端没有这个对象，无法删除。";
        if (CapabilityDeleted(bean))
            return "对象已被标记删除，无需再删。";
        if (CapabilityIsDirectory(bean))
            return "目标是目录；本工具只删除普通文件。";
        if (!string.Equals(CapabilityBeanPath(bean), cloudPath, StringComparison.Ordinal))
            return "对象详情路径与请求不一致，拒绝删除。";

        var version = CapabilityLong(CapabilityMember(bean, "version"));
        if (version != expectedVersion)
            return "当前版本 " + version + " 与创建台账的 " + expectedVersion + " 不一致（对象已被修改），拒绝删除。";

        var hash = CapabilityMember(bean, "hash") as string;
        if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
            return "当前云端 hash 与创建台账不一致（对象已被修改），拒绝删除。";

        return null;
    }

    private static string CapabilityDeletedBeforeJson(object? bean)
    {
        if (bean is null) return Json.Obj(("exists", false));
        return Json.Obj(
            ("exists", true),
            ("cloudPath", CapabilityBeanPath(bean)),
            ("objectType", CapabilityMember(bean, "objectType") as string),
            ("version", CapabilityLong(CapabilityMember(bean, "version"))),
            ("size", CapabilityMember(bean, "size") is { } size ? CapabilityLong(size) : null),
            ("hash", CapabilityMember(bean, "hash") as string),
            ("deleted", CapabilityDeleted(bean)));
    }

    private static (bool Ok, string Json) CapabilityVerifyDeleted(MethodInfo readDetail, MethodInfo listDirectory,
        object box, object path, object parent, string cloudPath)
    {
        string? detailError = null;
        var afterExists = false;
        var afterDeleted = false;
        try
        {
            var after = readDetail.Invoke(null, new[] { box, path });
            if (after is not null)
            {
                afterExists = true;
                afterDeleted = CapabilityDeleted(after);
            }
        }
        catch (Exception ex)
        {
            // 已删除的对象可能直接查不到（客户端报 ObjectNotFound），这不是失败。
            detailError = CapabilityError(ex);
        }

        List<string>? entries = null;
        string? listingError = null;
        try
        {
            entries = CapabilityDirectoryPaths(listDirectory, parent);
        }
        catch (Exception ex)
        {
            listingError = CapabilityError(ex);
        }

        var listed = entries?.Any(p => string.Equals(p, cloudPath, StringComparison.OrdinalIgnoreCase));
        var removedFromDetail = detailError is not null || !afterExists || afterDeleted;
        var ok = listingError is null && listed == false && removedFromDetail;
        return (ok, Json.Obj(
            ("ok", ok), ("performed", true),
            ("detailAfter", detailError is null ? (afterExists ? "present" : "missing") : "error"),
            ("deletedFlag", detailError is null && afterExists ? afterDeleted : null),
            ("listedInParent", listingError is null ? (listed is null ? null : listed) : null),
            ("detailError", detailError), ("listingError", listingError)));
    }

    private static string CapabilityDeleteFailure(string cloudPath, string reason, bool attempted)
        => Json.Obj(("ok", false), ("attempted", attempted), ("cloudPath", cloudPath),
            ("outcome", attempted ? "unknown" : "refused"), ("reason", reason),
            ("verification", new Json.Raw(Json.Obj(("ok", false), ("performed", false)))));
}
