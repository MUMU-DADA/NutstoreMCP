// P4 E1：创建公开分享链接（默认预演，必须 confirm）。
//
// 真正的红线不是「不可撤销」（网页端能撤），而是 **URL 本身是访问凭证**：
// 它一旦作为工具返回值进入对话与日志，就等于把访问权散播出去。
// 所以：默认不返回 URL；只有显式 returnUrl=true 才返回，并在 notes 里标注为凭证；
// 台账只存 URL 的 SHA-256，不存原文。
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>E1 公开分享链接（第一版：只创建、不修改已有分享）。</summary>
public static class SharePublish
{
    public const string ToolName = "nutstore_l3_publish_share";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 预演 / 执行一次分享创建。
    /// </summary>
    /// <param name="readShareInfo">只读读当前分享状态（钩子返回 <c>shared</c> / 设置）。</param>
    /// <param name="executePublish">默认域钩子：<c>(sandboxId, cloudPath, expireMills, password, downloadDisabled, enableUpload)</c> → JSON。</param>
    /// <param name="appendShare">写分享台账（成功后调用；URL 只留 SHA-256）。</param>
    public static string Publish(
        string ledgerPath,
        bool unlocked, bool confirm, bool dryRun, long id, long magic, string cloudPath,
        long expireMillsSinceEpoch, string? password, bool downloadDisabled, bool enableUpload, bool returnUrl,
        Func<string> bindings,
        Func<string, string> readDetail,
        Func<string, string> readShareInfo,
        Func<long, string, long, string?, bool, bool, string> executePublish,
        Func<ShareLedger.Entry, bool> appendShare)
    {
        var refusals = new List<string>();
        var verificationErrors = new List<string>();

        if (!unlocked)
            refusals.Add("L3 写入未解锁，需要内容为 yes 的 l3-write.enabled 令牌。");
        if (!confirm)
            refusals.Add("调用方没有传 confirm=true。");
        if (id <= 0 || magic <= 0)
            refusals.Add("sandboxId / magic 必须为正数。");

        var pathError = CloudObjectPath.Validate(cloudPath);
        if (pathError is not null)
            refusals.Add("目标路径：" + pathError);
        if (expireMillsSinceEpoch < 0)
            refusals.Add("有效期必须是 0（永久）或正的毫秒时间戳。");
        if (password is not null && (password.Length == 0 || password.Length > 64))
            refusals.Add("分享密码长度必须在 1–64 字符之间。");

        string? localRoot = null;
        JsonElement? target = null;
        JsonElement? shareBefore = null;
        var preflightOk = false;
        var attempted = false;
        var executed = false;
        var hookOk = false;
        JsonElement? hookResult = null;
        string? url = null;
        string? urlSha256 = null;
        long? publishVersion = null;
        bool? settingsMatch = null;
        bool? shareStateAfter = null;
        bool? ledgerRecorded = null;

        if (refusals.Count == 0)
        {
            localRoot = CloudWriteOperations.ReadBinding(bindings, id, magic, refusals);
        }

        if (refusals.Count == 0)
        {
            target = CloudWriteOperations.ReadJson(() => readDetail(cloudPath), refusals, "目标对象详情");
            if (target is not null)
            {
                var problem = ValidateTarget(target.Value, cloudPath);
                if (problem is not null) refusals.Add(problem);
            }
        }

        if (refusals.Count == 0)
        {
            shareBefore = CloudWriteOperations.ReadJson(() => readShareInfo(cloudPath), refusals, "当前分享状态");
            if (shareBefore is not null && CloudWriteOperations.ReadBool(shareBefore.Value, "shared") == true)
            {
                refusals.Add("目标已有分享；第一版只创建新分享，不覆盖既有分享设置。"
                    + "请先在网页端撤销既有分享后再试。");
            }
        }

        preflightOk = refusals.Count == 0;

        if (preflightOk && !dryRun)
        {
            attempted = true;
            hookResult = CloudWriteOperations.ReadJson(
                () => executePublish(id, cloudPath, expireMillsSinceEpoch, password, downloadDisabled, enableUpload),
                refusals, "调用客户端分享实现");

            hookOk = hookResult is not null
                && CloudWriteOperations.IsTrue(hookResult.Value, "ok")
                && CloudWriteOperations.ReadBool(hookResult.Value, "settingsMatch") == true;

            if (!hookOk)
            {
                refusals.Add("客户端分享失败或没有确认生效设置："
                    + (hookResult is null
                        ? "没有返回结构化结果。"
                        : CloudWriteOperations.Text(hookResult.Value, "reason") ?? "返回 ok=false 或复查失败。"));
            }

            url = hookResult is not null && CloudWriteOperations.Text(hookResult.Value, "url") is { } value ? value : null;
            if (string.IsNullOrWhiteSpace(url)) verificationErrors.Add("客户端没有返回分享 URL。");
            if (url is not null) urlSha256 = Sha256Hex(url);
            publishVersion = hookResult is not null && CloudWriteOperations.TryLong(hookResult.Value, "publishVersion", out var version)
                ? version
                : null;
            settingsMatch = hookResult is not null ? CloudWriteOperations.ReadBool(hookResult.Value, "settingsMatch") : null;

            // 插件侧再读一次分享状态：确认确实变成「已分享」。
            var after = CloudWriteOperations.ReadJson(() => readShareInfo(cloudPath), verificationErrors, "写后分享状态");
            if (after is not null)
            {
                shareStateAfter = CloudWriteOperations.ReadBool(after.Value, "shared");
                if (shareStateAfter != true)
                    verificationErrors.Add("写后分享状态不是「已分享」：分享可能没有真正创建。");
            }

            executed = hookOk && verificationErrors.Count == 0;
            if (executed)
            {
                try
                {
                    ledgerRecorded = appendShare(new ShareLedger.Entry(
                        DateTimeOffset.Now, id, cloudPath, publishVersion ?? 0, expireMillsSinceEpoch,
                        PasswordSet: !string.IsNullOrEmpty(password),
                        downloadDisabled, enableUpload, urlSha256!, ShareLedger.ActionPublished));
                }
                catch (Exception)
                {
                    ledgerRecorded = false;
                }
            }
        }

        var checkedState = preflightOk && !dryRun;
        var revealUrl = returnUrl && executed;
        var notes = new List<string>
        {
            "分享 URL 本身就是访问凭证：默认不返回；只有显式传 returnUrl=true 才返回，并请勿把它写进公开日志或聊天记录。",
            "客户端没有「取消分享」接口；要撤销请在网页端操作。分享台账（%LOCALAPPDATA%\\NutstoreMcp\\shares.jsonl）只存 URL 的 SHA-256，便于事后核对。",
            "第一版只创建新分享：目标已有分享时拒绝，不覆盖既有分享的权限/密码/有效期设置。",
            "创建失败或结果未知只做只读复查，不重试、不自动撤销。",
        };
        if (!returnUrl && executed)
        {
            notes.Add("URL 已省略（returnUrl=false）。需要链接时用 returnUrl=true 重新调用会因「已有分享」被拒绝 —— 请从网页端或客户端界面获取已有链接。");
        }
        if (executed && ledgerRecorded == false)
        {
            notes.Add("分享已创建，但分享台账写入失败：请自行记录以便事后撤销。");
        }

        return JsonSerializer.Serialize(new
        {
            allowed = refusals.Count == 0,
            wouldWrite = executed,
            dryRun,
            unlockVariablePresent = unlocked,
            attempted,
            executed,
            probe = new
            {
                operation = "publish-share",
                sandboxId = id,
                magic,
                cloudPath,
                localRoot,
                target = target is null ? null : new
                {
                    objectType = CloudWriteOperations.Text(target.Value, "objectType"),
                    version = CloudWriteOperations.Text(target.Value, "version"),
                    sizeBytes = CloudWriteOperations.Text(target.Value, "sizeBytes"),
                },
                shareBefore = shareBefore is null ? null : new
                {
                    shared = CloudWriteOperations.ReadBool(shareBefore.Value, "shared"),
                    version = CloudWriteOperations.Text(shareBefore.Value, "version"),
                },
                requested = new
                {
                    expireMillsSinceEpoch,
                    passwordSet = !string.IsNullOrEmpty(password),
                    downloadDisabled,
                    enableUpload,
                    returnUrl,
                },
                urlSha256,
                publishVersion,
            },
            refusals,
            result = hookResult,
            shares = new
            {
                url = revealUrl ? url : null,
                urlSha256,
                urlWithheld = !revealUrl,
                stateAfter = shareStateAfter,
                settingsMatch,
                ledgerRecorded,
            },
            verification = new
            {
                checkedState,
                ok = checkedState ? (bool?)executed : null,
                shareStateAfter,
                settingsMatch,
                ledgerRecorded,
                errors = verificationErrors,
            },
            notes,
        }, JsonOptions);
    }

    private static string? ValidateTarget(JsonElement value, string cloudPath)
    {
        if (!CloudWriteOperations.IsTrue(value, "ok"))
        {
            return "目标对象详情读取失败，拒绝分享。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "cloudPath"), cloudPath, StringComparison.Ordinal))
        {
            return "目标对象详情路径与请求不是逐字符一致，拒绝分享。";
        }

        if (CloudWriteOperations.ReadBool(value, "deleted") != false)
        {
            return "目标已删除或删除状态未知，拒绝分享。";
        }

        return null;
    }

    private static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(digest.Length * 2);
        foreach (var b in digest)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
