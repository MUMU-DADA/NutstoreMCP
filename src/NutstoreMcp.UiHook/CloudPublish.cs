// P4 E1：为云端对象创建公开分享链接。
//
// 红线：分享 URL 本身就是访问凭证（客户端自家 Outlook 插件会把它写进外发邮件），
// 所以本方法只把 URL 交给调用方，由插件决定是否外传；写后复查不返回 URL。
// 写前确认目标当前没有分享，避免覆盖既有分享设置；失败只做只读复查，不重试、不撤销。
using NutstoreMcp.ClientBindings;
using System.Reflection;

namespace NutstoreMcp.UiHook;

public sealed partial class UiHook
{
    /// <summary>创建公开分享链接（E1）。</summary>
    public string PublishCloudObject(long sandboxId, long magic, string localRoot, string cloudPath,
        long expireMillsSinceEpoch, string? password, bool downloadDisabled, bool enableUpload)
    {
        var attempted = false;
        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null)
                return CapabilityPublishFailure(cloudPath, validation, attempted);

            if (expireMillsSinceEpoch < 0)
                return CapabilityPublishFailure(cloudPath, "有效期必须是 0（永久）或正的毫秒时间戳", attempted);
            if (password is not null && (password.Length == 0 || password.Length > 64))
                return CapabilityPublishFailure(cloudPath, "分享密码长度必须在 1–64 字符之间", attempted);

            var api = FindClientType(CloudOpsTypeName)
                ?? throw new CapabilityFailureException("找不到云端操作层");
            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError)
                ?? throw new CapabilityFailureException(boxError);
            var path = CapabilityBuildPath(box, cloudPath);

            var readDetail = CapabilityMethod(api, ClientSymbols.ReadObjectDetail, box.GetType(), path.GetType());
            var detail = readDetail.Invoke(null, new[] { box, path });
            if (detail is null) return CapabilityPublishFailure(cloudPath, "云端没有这个对象", attempted);
            if (CapabilityDeleted(detail)) return CapabilityPublishFailure(cloudPath, "对象已被标记删除", attempted);
            if (!string.Equals(CapabilityBeanPath(detail), cloudPath, StringComparison.Ordinal))
                return CapabilityPublishFailure(cloudPath, "对象详情路径与请求不一致，拒绝分享", attempted);

            // 已有分享就拒绝：第一版只创建新分享，不覆盖既有分享设置。
            var before = CapabilityReadPublishState(api, path);
            if (before.Shared)
                return CapabilityPublishFailure(cloudPath,
                    "目标已有分享（版本 " + before.Version + "）；本版只创建新分享，请先在网页端撤销后再试。", attempted);

            // 构造 PublishedObjectInfo：公开链接 = WebACL.PublicWebACL，再盖上有效期/密码/禁下载等设置。
            var webAclType = FindClientType(ClientSymbols.WebAclType)
                ?? throw new CapabilityFailureException("找不到 WebACL");
            var publicAcl = webAclType.GetField("PublicWebACL", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null)
                ?? throw new CapabilityFailureException("找不到 WebACL.PublicWebACL");
            var infoType = FindClientType(ClientSymbols.PublishedObjectInfoType)
                ?? throw new CapabilityFailureException("找不到 PublishedObjectInfo");
            var ctor = infoType.GetConstructor(new[] { webAclType })
                ?? throw new CapabilityFailureException("找不到 PublishedObjectInfo(WebACL) 构造函数");
            var info = ctor.Invoke(new[] { publicAcl })
                ?? throw new CapabilityFailureException("PublishedObjectInfo 构造返回 null");

            CapabilitySetMember(info, "DownloadDisabled", downloadDisabled);
            CapabilitySetMember(info, "EnableUpload", enableUpload);
            CapabilitySetMember(info, "ExpireMillsSinceEpoch", expireMillsSinceEpoch);
            if (!string.IsNullOrEmpty(password)) CapabilitySetMember(info, "Password", password);

            var publish = CapabilityMethod(api, ClientSymbols.PublishObject, path.GetType(), infoType);
            attempted = true;
            string? writeError = null;
            object? published = null;
            try
            {
                published = publish.Invoke(null, new[] { path, info });
            }
            catch (Exception ex)
            {
                writeError = CapabilityError(ex);
            }

            var url = published is null ? null : Get(published, "Url") as string;
            var publishVersion = published is null ? (long?)null : AsLong(Get(published, "Version"));

            // 写后只读复查：分享状态存在，且有效期/禁下载/密码与请求一致。
            var after = CapabilityReadPublishState(api, path);
            var settingsMatch = after.Shared
                && after.ExpireMills == expireMillsSinceEpoch
                && after.DownloadDisabled == downloadDisabled
                && after.HasPassword == !string.IsNullOrEmpty(password);
            var ok = writeError is null && !string.IsNullOrWhiteSpace(url) && settingsMatch;
            return Json.Obj(
                ("ok", ok),
                ("attempted", attempted),
                ("cloudPath", cloudPath),
                ("url", url),
                ("publishVersion", publishVersion),
                ("requested", new Json.Raw(Json.Obj(
                    ("expireMillsSinceEpoch", expireMillsSinceEpoch),
                    ("downloadDisabled", downloadDisabled),
                    ("enableUpload", enableUpload),
                    ("passwordSet", !string.IsNullOrEmpty(password))))),
                ("stateAfter", new Json.Raw(after.Json)),
                ("settingsMatch", settingsMatch),
                ("writeError", writeError),
                ("outcome", ok ? "published" : "unknown"),
                ("reason", ok ? null
                    : "已尝试创建分享，但没有同时确认调用成功、URL 与生效设置；只做只读复查，不重试、不自动撤销。"));
        }
        catch (Exception ex)
        {
            return CapabilityPublishFailure(cloudPath, "创建分享失败：" + CapabilityError(ex), attempted);
        }
    }

    /// <summary>一次分享状态快照（URL 与密码值不进入快照）。</summary>
    private sealed class PublishState
    {
        public PublishState(bool shared, long version, long expireMills, bool downloadDisabled,
            bool enableUpload, bool hasPassword, string json)
        {
            Shared = shared;
            Version = version;
            ExpireMills = expireMills;
            DownloadDisabled = downloadDisabled;
            EnableUpload = enableUpload;
            HasPassword = hasPassword;
            Json = json;
        }

        public bool Shared { get; }
        public long Version { get; }
        public long ExpireMills { get; }
        public bool DownloadDisabled { get; }
        public bool EnableUpload { get; }
        public bool HasPassword { get; }
        public string Json { get; }
    }

    /// <summary>只读读分享状态；ObjectNotFound 表示「未分享」而不是失败。</summary>
    private static PublishState CapabilityReadPublishState(Type api, object path)
    {
        var method = api.GetMethod(
            ClientSymbols.ReadPublishInfo,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { path.GetType() },
            modifiers: null)
            ?? throw new CapabilityFailureException("找不到分享状态方法（混淆名可能变了）");

        object? info = null;
        Exception? error = null;
        try
        {
            info = method.Invoke(null, new[] { path });
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is not null)
        {
            var chain = string.Join(" | ", Chain(error));
            if (chain.IndexOf("ObjectNotFound", StringComparison.OrdinalIgnoreCase) >= 0
                || chain.IndexOf("pub object can not be found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new PublishState(false, 0, 0, false, false, false,
                    Json.Obj(("ok", true), ("shared", false)));
            }

            throw new CapabilityFailureException("读取分享状态失败：" + CapabilityError(error));
        }

        if (info is null)
        {
            return new PublishState(false, 0, 0, false, false, false,
                Json.Obj(("ok", true), ("shared", false)));
        }

        var version = AsLong(Get(info, "Version"));
        var expire = AsLong(Get(info, "ExpireMillsSinceEpoch"));
        var downloadDisabled = Get(info, "DownloadDisabled") is bool disabled && disabled;
        var enableUpload = Get(info, "EnableUpload") is bool enabled && enabled;
        var hasPassword = !string.IsNullOrEmpty(Get(info, "Password") as string);
        return new PublishState(true, version, expire, downloadDisabled, enableUpload, hasPassword,
            Json.Obj(
                ("ok", true),
                ("shared", true),
                ("version", version),
                ("expireMillsSinceEpoch", expire),
                ("downloadDisabled", downloadDisabled),
                ("enableUpload", enableUpload),
                ("hasPassword", hasPassword),
                ("acl", Get(info, "ACL")?.ToString())));
    }

    private static string CapabilityPublishFailure(string cloudPath, string reason, bool attempted)
        => Json.Obj(("ok", false), ("attempted", attempted), ("cloudPath", cloudPath),
            ("outcome", attempted ? "unknown" : "refused"), ("reason", reason),
            ("verification", new Json.Raw(Json.Obj(("ok", false), ("performed", false)))));
}
