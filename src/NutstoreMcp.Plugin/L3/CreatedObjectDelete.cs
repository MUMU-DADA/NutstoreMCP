// P2：台账限定的删除 —— 只删 MCP 自己创建、且当前状态与创建台账完全一致的对象。
//
// 三层约束：
//   1. 台账：路径必须由本插件创建时登记过（sandboxId + 路径逐字符一致，最后一条记录是 created）；
//   2. 当前状态：对象详情必须仍是普通文件、未删除，版本/大小/云端 hash 与台账完全一致；
//   3. 写后复查：详情不再可用或已标记删除，父目录列表里也没有它。失败只报告，不重试、不恢复。
//
// 台账被篡改能伪造「可删除」，但伪造不出「当前版本与哈希匹配的非用户对象」以外的范围 ——
// 再加上路径必须落在当前已绑定沙箱内、且不允许通配/大小写别名。
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>台账限定的删除。</summary>
public static class CreatedObjectDelete
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 删除一个 MCP 自己创建的云端普通文件。
    /// </summary>
    /// <param name="ledgerPath">创建台账文件路径。</param>
    /// <param name="executeDelete">默认域钩子：<c>(sandboxId, cloudPath, expectedVersion, expectedHash)</c> → JSON。</param>
    /// <param name="appendTombstone">写删除记录到台账（失败不影响本次删除结果，只影响后续审计）。</param>
    public static string Delete(
        string ledgerPath,
        bool unlocked, bool confirm, bool dryRun, long id, long magic, string cloudPath,
        Func<string> bindings,
        Func<string, string> readDetail,
        Func<string, string> listDirectory,
        Func<long, string, long, string, string> executeDelete,
        Func<CreatedObjectLedger.Entry, bool> appendTombstone)
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

        string? localRoot = null;
        string? parentPath = null;
        CreatedObjectLedger.Entry? record = null;
        var preflightOk = false;
        var attempted = false;
        var executed = false;
        var hookOk = false;
        JsonElement? hookResult = null;
        bool? deletedFlag = null;
        bool? targetPresent = null;
        bool? tombstoneWritten = null;
        string? afterDetailStatus = null;

        if (refusals.Count == 0)
        {
            parentPath = CloudObjectPath.Parent(cloudPath);
            localRoot = CloudWriteOperations.ReadBinding(bindings, id, magic, refusals);
        }

        if (refusals.Count == 0)
        {
            var ledgerEntries = CreatedObjectLedger.ReadAll(ledgerPath);
            record = CreatedObjectLedger.FindLatestForPath(ledgerEntries, id, cloudPath);
            if (record is null)
            {
                var alias = CreatedObjectLedger.FindCaseInsensitivePath(ledgerEntries, id, cloudPath);
                refusals.Add(alias is not null
                    ? "台账里有仅大小写不同的记录（" + alias.CloudPath + "），路径必须逐字符一致，拒绝删除（大小写别名）。"
                    : "创建台账里没有这条路径（或不属于该沙箱）的记录，拒绝删除。"
                        + "删除工具只处理 MCP 自己创建并登记过的对象。");
            }
            else if (!string.Equals(record.Action, CreatedObjectLedger.ActionCreated, StringComparison.Ordinal))
            {
                refusals.Add("台账里该路径最后一条记录是 " + record.Action + "，不是创建记录，拒绝删除。");
            }
            else if (!string.Equals(record.CloudPath, cloudPath, StringComparison.Ordinal))
            {
                refusals.Add("台账记录的路径与请求不是逐字符一致（大小写别名），拒绝删除。");
            }
        }

        if (refusals.Count == 0)
        {
            var before = CloudWriteOperations.ReadJson(() => readDetail(cloudPath), refusals, "对象详情");
            if (before is not null)
            {
                var problem = ValidateBefore(before.Value, cloudPath, record!);
                if (problem is not null)
                {
                    refusals.Add(problem);
                }
            }
        }

        preflightOk = refusals.Count == 0;

        if (preflightOk && !dryRun)
        {
            attempted = true;
            hookResult = CloudWriteOperations.ReadJson(
                () => executeDelete(id, cloudPath, record!.Version, record.Hash), refusals, "调用客户端删除实现");
            hookOk = hookResult is not null && CloudWriteOperations.IsTrue(hookResult.Value, "ok");
            if (!hookOk)
            {
                refusals.Add("客户端删除失败：" + (hookResult is null
                    ? "没有返回结构化结果。"
                    : CloudWriteOperations.Text(hookResult.Value, "reason") ?? "返回 ok=false 或缺少真实成功标志。"));
            }

            // 写后只读复查（插件侧）：详情不再可用或已标记删除，父目录列表里也没有它。
            // 已删除对象在客户端可能直接报错，所以详情失败本身不记成验证错误。
            var afterErrors = new List<string>();
            var after = CloudWriteOperations.ReadJson(() => readDetail(cloudPath), afterErrors, "写后对象详情");
            var afterOk = after is not null && CloudWriteOperations.IsTrue(after.Value, "ok");
            deletedFlag = after is not null ? CloudWriteOperations.ReadBool(after.Value, "deleted") : null;
            var detailGone = after is null || !afterOk || deletedFlag == true;
            afterDetailStatus = after is null
                ? "error-or-missing"
                : afterOk ? (deletedFlag == true ? "deleted" : "present") : "not-found";

            var entries = CloudWriteOperations.ReadDirectory(listDirectory, parentPath!, verificationErrors);
            if (entries is not null)
            {
                targetPresent = entries.Any(entry => string.Equals(entry, cloudPath, StringComparison.OrdinalIgnoreCase));
            }

            if (!detailGone) verificationErrors.Add("写后详情仍显示对象存在且未标记删除。");
            if (targetPresent != false) verificationErrors.Add("写后父目录列表里仍能查到该对象，删除未确认。");

            executed = hookOk && verificationErrors.Count == 0;
            if (executed)
            {
                try
                {
                    tombstoneWritten = appendTombstone(record! with
                    {
                        Time = DateTimeOffset.Now,
                        Action = CreatedObjectLedger.ActionDeleted,
                    });
                }
                catch (Exception)
                {
                    tombstoneWritten = false;
                }
            }
        }

        var checkedState = preflightOk && !dryRun;
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
                operation = "delete-created-object",
                sandboxId = id,
                magic,
                cloudPath,
                parentPath,
                localRoot,
                ledger = record is null ? null : new
                {
                    createdAt = record.Time,
                    version = record.Version,
                    size = record.Size,
                    hash = record.Hash,
                    tool = record.Tool,
                    contentSource = record.ContentSource,
                },
            },
            refusals,
            result = hookResult,
            verification = new
            {
                checkedState,
                ok = checkedState ? (bool?)executed : null,
                detailAfter = afterDetailStatus,
                deleted = deletedFlag,
                targetPresent,
                tombstoneWritten,
                errors = verificationErrors,
            },
            notes = new[]
            {
                "删除只作用于 MCP 自己创建、且版本与哈希与创建台账完全一致的对象；不删目录、不删用户已有对象。",
                "客户端会把云端删除同步到本地绑定目录 —— 这正是撤销自己写入结果的预期行为。",
                "失败或结果未知只做只读复查，不重试、不恢复。",
                "通用反射桥仍然禁止删除入口；台账被篡改也绕不过「当前版本/大小/hash 必须与台账匹配」。",
            },
        }, JsonOptions);
    }

    private static string? ValidateBefore(JsonElement value, string cloudPath, CreatedObjectLedger.Entry record)
    {
        if (!CloudWriteOperations.IsTrue(value, "ok"))
        {
            return "对象详情读取失败，拒绝删除。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "cloudPath"), cloudPath, StringComparison.Ordinal))
        {
            return "对象详情返回的路径与请求不是逐字符一致，拒绝删除。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "objectType"), "file", StringComparison.OrdinalIgnoreCase))
        {
            return "目标不是普通文件；本工具只删除普通文件。";
        }

        if (CloudWriteOperations.ReadBool(value, "deleted") != false)
        {
            return "对象已删除或删除状态未知，拒绝删除。";
        }

        if (!CloudWriteOperations.TryLong(value, "version", out var version) || version != record.Version)
        {
            return "当前版本与创建台账不一致（对象已被修改），拒绝删除。";
        }

        if (!CloudWriteOperations.TryLong(value, "sizeBytes", out var size)
            && !CloudWriteOperations.TryLong(value, "size", out size))
        {
            return "对象详情缺少大小，拒绝删除。";
        }

        if (size != record.Size)
        {
            return "当前大小与创建台账不一致（对象已被修改），拒绝删除。";
        }

        if (!string.Equals(CloudWriteOperations.Text(value, "hash"), record.Hash, StringComparison.Ordinal))
        {
            return "当前云端 hash 与创建台账不一致（对象已被修改），拒绝删除。";
        }

        return null;
    }
}
