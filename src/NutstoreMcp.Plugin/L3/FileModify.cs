// P3 C4：修改已有文件的当前版本（台账内对象，第一版）。
//
// 与「创建」的本质区别：目标已经存在，所以安全性靠四层约束：
//   1. 台账：只改 MCP 自己创建、且台账最后一条仍是 created 的对象（第一版范围）；
//   2. 预演 + 二次确认令牌：执行必须显式带 expectedVersion，且等于预演时看到的当前版本；
//   3. 当前状态：详情版本/大小/云端 hash 必须与台账完全一致（对象没被别人改过）；
//   4. 写后复查：新版本 > 旧版本、大小/hash 与提交内容一致、整文件回读 SHA-256 一致、
//      历史版本里仍有旧版本（网页端可回退）。
// 任何不符只报告，不重试、不回滚。
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>C4 第一版：修改 MCP 自己创建的文件的当前版本。</summary>
public static class FileModify
{
    /// <summary>与内联创建一致的内容上限（256 KiB）。</summary>
    public const int MaxContentBytes = CloudWriteOperations.MaxContentBytes;

    public const string ToolName = "nutstore_l3_modify_file";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 预演 / 执行一次「改内容」。
    /// </summary>
    /// <param name="expectedSha256">新内容的 SHA-256（小写十六进制），用于写后整文件回读校验。</param>
    /// <param name="executeModify">默认域钩子：<c>(sandboxId, cloudPath, expectedVersion, base64)</c> → JSON。</param>
    /// <param name="listVersions">读历史版本（只读），用于确认旧版本仍在。</param>
    /// <param name="appendModified">写台账 modified 记录（成功后调用）。</param>
    public static string Modify(
        string ledgerPath,
        bool unlocked, bool confirm, bool dryRun, long id, long magic, string cloudPath,
        int contentLength, string expectedSha256, string contentBase64, long expectedVersion,
        Func<string> bindings,
        Func<string, string> readDetail,
        Func<string, long, long, string> readInline,
        Func<long, string, long, string, string> executeModify,
        Func<string, string> listVersions,
        Func<CreatedObjectLedger.Entry, bool> appendModified)
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
        if (contentLength <= 0 || contentLength > MaxContentBytes)
            refusals.Add("内容长度必须在 1–" + MaxContentBytes + " 字节之间。");
        if (!dryRun && expectedVersion <= 0)
        {
            refusals.Add("执行修改必须显式传 expectedVersion（来自预演里看到的当前版本）；"
                + "这样调用方必须先用 dryRun 看过当前状态。");
        }

        string? localRoot = null;
        CreatedObjectLedger.Entry? record = null;
        JsonElement? current = null;
        var currentVersion = 0L;
        var currentSize = 0L;
        string? currentHash = null;
        var preflightOk = false;
        var attempted = false;
        var executed = false;
        var hookOk = false;
        JsonElement? hookResult = null;
        long? versionAfter = null;
        bool? contentMatches = null;
        bool? readBackMatches = null;
        string? readBackSha256 = null;
        bool? historyHasPrevious = null;
        bool? ledgerRecorded = null;
        string? hookCloudHash = null;

        if (refusals.Count == 0)
        {
            localRoot = CloudWriteOperations.ReadBinding(bindings, id, magic, refusals);
        }

        if (refusals.Count == 0)
        {
            record = CreatedObjectLedger.FindLatestForPath(ledgerPath, id, cloudPath);
            if (record is null)
            {
                refusals.Add("创建台账里没有这条路径的记录，拒绝修改。第一版只修改 MCP 自己创建的文件。");
            }
            else if (!string.Equals(record.Action, CreatedObjectLedger.ActionCreated, StringComparison.Ordinal))
            {
                refusals.Add("台账里该路径的最后一条记录是 " + record.Action + "，不是创建记录，拒绝修改"
                    + "（第一版只允许修改一次且不允许修改已删除/已修改的对象）。");
            }
        }

        if (refusals.Count == 0)
        {
            current = CloudWriteOperations.ReadJson(() => readDetail(cloudPath), refusals, "当前对象详情");
            if (current is not null)
            {
                var problem = ValidateCurrent(current.Value, cloudPath, record!, out currentVersion, out currentSize, out currentHash);
                if (problem is not null)
                {
                    refusals.Add(problem);
                }
                else if (expectedVersion > 0 && expectedVersion != currentVersion)
                {
                    refusals.Add("当前版本 " + currentVersion + " 与 expectedVersion " + expectedVersion
                        + " 不一致（对象已变化），请重新预演后再执行。");
                }
            }
        }

        preflightOk = refusals.Count == 0;

        if (preflightOk && !dryRun)
        {
            attempted = true;
            hookResult = CloudWriteOperations.ReadJson(
                () => executeModify(id, cloudPath, expectedVersion, contentBase64),
                refusals, "调用客户端修改实现");

            hookOk = hookResult is not null
                && CloudWriteOperations.IsTrue(hookResult.Value, "ok")
                && hookResult.Value.TryGetProperty("verification", out var hookVerification)
                && hookVerification.ValueKind == JsonValueKind.Object
                && CloudWriteOperations.IsTrue(hookVerification, "ok")
                && CloudWriteOperations.ReadBool(hookResult.Value, "historyOk") == true;

            if (!hookOk)
            {
                refusals.Add("客户端修改失败或没有确认新版本与历史版本："
                    + (hookResult is null
                        ? "没有返回结构化结果。"
                        : CloudWriteOperations.Text(hookResult.Value, "reason") ?? "返回 ok=false、复查失败或历史版本未确认。"));
            }

            hookCloudHash = hookResult is not null && CloudWriteOperations.Text(hookResult.Value, "cloudHash") is { } hash
                ? hash
                : null;

            // 写后只读复查：详情里的版本/大小/hash。
            var after = CloudWriteOperations.ReadJson(() => readDetail(cloudPath), verificationErrors, "写后对象详情");
            if (after is not null && CloudWriteOperations.IsTrue(after.Value, "ok"))
            {
                if (!string.Equals(CloudWriteOperations.Text(after.Value, "cloudPath"), cloudPath, StringComparison.Ordinal))
                    verificationErrors.Add("写后详情路径与请求不是逐字符一致。");
                if (!string.Equals(CloudWriteOperations.Text(after.Value, "objectType"), "file", StringComparison.OrdinalIgnoreCase))
                    verificationErrors.Add("写后详情不是普通文件。");
                if (CloudWriteOperations.ReadBool(after.Value, "deleted") != false)
                    verificationErrors.Add("写后详情显示对象已删除或删除状态未知。");

                if (CloudWriteOperations.TryLong(after.Value, "version", out var observedVersion))
                {
                    versionAfter = observedVersion;
                    if (observedVersion <= currentVersion)
                        verificationErrors.Add("写后版本 " + observedVersion + " 不大于修改前版本 " + currentVersion + "。");
                }
                else
                {
                    verificationErrors.Add("写后详情缺少版本号。");
                }

                if (CloudWriteOperations.TryLong(after.Value, "sizeBytes", out var observedSize)
                    || CloudWriteOperations.TryLong(after.Value, "size", out observedSize))
                {
                    contentMatches = observedSize == contentLength
                        && string.Equals(CloudWriteOperations.Text(after.Value, "hash"), hookCloudHash, StringComparison.Ordinal);
                    if (contentMatches != true)
                        verificationErrors.Add("写后大小或云端 hash 与本次提交的新内容不一致。");
                }
                else
                {
                    verificationErrors.Add("写后详情缺少大小。");
                }
            }

            // 整文件回读：用分段读取把新内容重新拼出来核对 SHA-256。
            var bytes = CloudWriteOperations.ReadBackAll(readInline, cloudPath, contentLength, verificationErrors);
            if (bytes is not null)
            {
                readBackSha256 = CloudContentCodec.Sha256Hex(bytes);
                readBackMatches = string.Equals(readBackSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
                if (readBackMatches != true)
                    verificationErrors.Add("写后回读的字节与本次提交的新内容不一致（SHA-256 不匹配）。");
            }

            // 历史版本：旧版本仍在（网页端可回退）。
            var history = CloudWriteOperations.ReadJson(() => listVersions(cloudPath), verificationErrors, "历史版本列表");
            if (history is not null && CloudWriteOperations.IsTrue(history.Value, "ok")
                && history.Value.TryGetProperty("versions", out var versions)
                && versions.ValueKind == JsonValueKind.Array)
            {
                historyHasPrevious = false;
                foreach (var item in versions.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("version", out var versionField)
                        && string.Equals(versionField.ToString(), currentVersion.ToString(), StringComparison.Ordinal))
                    {
                        historyHasPrevious = true;
                        break;
                    }
                }

                if (historyHasPrevious != true)
                    verificationErrors.Add("历史版本列表里没有修改前的版本 " + currentVersion + "，旧版本可能没有保留。");
            }
            else
            {
                verificationErrors.Add("读不到历史版本列表，无法确认旧版本仍在。");
            }

            executed = hookOk && verificationErrors.Count == 0;
            if (executed)
            {
                try
                {
                    ledgerRecorded = appendModified(record! with
                    {
                        Time = DateTimeOffset.Now,
                        Version = versionAfter ?? currentVersion + 1,
                        Hash = hookCloudHash ?? currentHash ?? record!.Hash,
                        Size = contentLength,
                        Tool = ToolName,
                        Action = "modified",
                    });
                }
                catch (Exception)
                {
                    ledgerRecorded = false;
                }
            }
        }

        var checkedState = preflightOk && !dryRun;
        var notes = new List<string>
        {
            "第一版只允许修改 MCP 自己创建、且台账最后一条仍是 created 的文件；目标由调用方显式给出。",
            "执行必须带 expectedVersion（来自预演）；它是「我看过当前版本」的二次确认令牌，版本变了就拒绝。",
            "修改产生新版本，旧版本进入历史 —— 网页端可以回退到旧版本。",
            "失败或结果未知只做只读复查，不重试、不回滚。",
        };
        if (executed && ledgerRecorded == false)
        {
            notes.Add("修改已成功，但台账写入失败：该对象以后不能通过台账工具删除，删除工具会拒绝它。");
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
                operation = "modify-file",
                sandboxId = id,
                magic,
                cloudPath,
                localRoot,
                expectedVersion,
                current = new { version = currentVersion, sizeBytes = currentSize, hash = currentHash },
                ledger = record is null ? null : new
                {
                    createdAt = record.Time,
                    version = record.Version,
                    size = record.Size,
                    hash = record.Hash,
                    tool = record.Tool,
                },
                newContent = new { bytes = contentLength, sha256 = expectedSha256 },
                versionAfter,
            },
            refusals,
            result = hookResult,
            verification = new
            {
                checkedState,
                ok = checkedState ? (bool?)executed : null,
                versionAfter,
                contentMatches,
                readBackChecked = checkedState,
                readBackMatches,
                readBackSha256,
                historyHasPrevious,
                ledgerRecorded,
                errors = verificationErrors,
            },
            notes,
        }, JsonOptions);
    }

    private static string? ValidateCurrent(JsonElement value, string cloudPath, CreatedObjectLedger.Entry record,
        out long version, out long size, out string? hash)
    {
        version = 0;
        size = 0;
        hash = null;

        if (!CloudWriteOperations.IsTrue(value, "ok"))
        {
            return "当前对象详情读取失败，拒绝修改。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "cloudPath"), cloudPath, StringComparison.Ordinal))
        {
            return "当前对象详情路径与请求不是逐字符一致，拒绝修改。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "objectType"), "file", StringComparison.OrdinalIgnoreCase))
        {
            return "目标不是普通文件；C4 第一版只修改文件。";
        }

        if (CloudWriteOperations.ReadBool(value, "deleted") != false)
        {
            return "对象已删除或删除状态未知，拒绝修改。";
        }

        if (!CloudWriteOperations.TryLong(value, "version", out version) || version <= 0)
        {
            return "当前对象缺少有效版本号，拒绝修改。";
        }

        if (!CloudWriteOperations.TryLong(value, "sizeBytes", out size)
            && !CloudWriteOperations.TryLong(value, "size", out size))
        {
            return "当前对象缺少大小，拒绝修改。";
        }

        hash = CloudWriteOperations.Text(value, "hash");
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "当前对象缺少云端 hash，拒绝修改。";
        }

        // 第一版要求对象自创建以来没被改过：台账与当前状态必须完全一致。
        if (version != record.Version || size != record.Size
            || !string.Equals(hash, record.Hash, StringComparison.Ordinal))
        {
            return "当前版本/大小/云端 hash 与创建台账不一致（对象已被修改），第一版拒绝继续修改。";
        }

        return null;
    }
}
