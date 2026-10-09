using System.Globalization;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>云端新增和移动的守卫与复查；读写入口由调用方注入，方便离线验证。</summary>
public static class CloudWriteOperations
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Move(
        bool unlocked, bool confirm, bool dryRun, long id, long magic, string src, string dest,
        Func<string> bindings, Func<string, string> listDirectory, Func<string, string> readDetail,
        Func<string> executeMove)
    {
        var state = new State("move", unlocked, dryRun, id, magic, src, dest);
        BasicGuards(state, confirm);
        if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            state.Refusals.Add("源和目标相同，或仅大小写不同；拒绝可能覆盖的重命名。");

        if (state.Refusals.Count == 0)
        {
            state.LocalRoot = ReadBinding(bindings, id, magic, state.Refusals);
            if (state.Refusals.Count == 0)
            {
                var before = ReadParents(state, listDirectory, readDetail, verifyParentTypes: true);
                CheckBefore(state, before);
                state.SourceDetail = ReadDetail(readDetail, src, state.Refusals, "源对象详情");
                ValidateFile(state.SourceDetail, state.Refusals, "源对象");
                state.PreflightOk = state.Refusals.Count == 0;
            }
        }

        if (state.PreflightOk && !dryRun)
        {
            Execute(state, executeMove);
            VerifyMove(state, listDirectory, readDetail);
        }

        return Serialize(state);
    }

    public static string Mkdir(
        bool unlocked, bool confirm, bool dryRun, long id, long magic, string path,
        Func<string> bindings, Func<string, string> listDirectory, Func<string, string> readDetail,
        Func<string> executeCreate)
    {
        var state = new State("mkdir", unlocked, dryRun, id, magic, null, path);
        BasicGuards(state, confirm);
        if (state.Refusals.Count == 0)
        {
            state.LocalRoot = ReadBinding(bindings, id, magic, state.Refusals);
            if (state.Refusals.Count == 0)
            {
                var before = ReadParents(state, listDirectory, readDetail, verifyParentTypes: true);
                CheckBefore(state, before);
                state.PreflightOk = state.Refusals.Count == 0;
            }
        }

        if (state.PreflightOk && !dryRun)
        {
            Execute(state, executeCreate);
            VerifyMkdir(state, listDirectory, readDetail);
        }

        return Serialize(state);
    }

    private static void BasicGuards(State state, bool confirm)
    {
        if (!state.Unlocked) state.Refusals.Add("L3 写入未解锁，需要内容为 yes 的 l3-write.enabled 令牌。");
        if (!confirm) state.Refusals.Add("调用方没有传 confirm=true。");
        if (state.Id <= 0 || state.Magic <= 0) state.Refusals.Add("sandboxId / magic 必须为正数。");
        if (state.Operation == "move")
        {
            var sourceError = CloudObjectPath.Validate(state.Source);
            if (sourceError is not null) state.Refusals.Add("源路径：" + sourceError);
        }
        var targetError = CloudObjectPath.Validate(state.Target);
        if (targetError is not null) state.Refusals.Add("目标路径：" + targetError);
        if (state.Refusals.Count == 0)
        {
            state.SourceParent = state.Source is null ? null : CloudObjectPath.Parent(state.Source);
            state.TargetParent = CloudObjectPath.Parent(state.Target);
        }
    }

    private static string? ReadBinding(Func<string> read, long id, long magic, List<string> errors)
    {
        var json = ReadJson(read, errors, "同步绑定");
        if (json is null) return null;
        var value = json.Value;
        if (!IsTrue(value, "ok") || !value.TryGetProperty("sandboxes", out var boxes)
            || boxes.ValueKind != JsonValueKind.Array)
        {
            errors.Add("读不到有效的同步绑定列表，拒绝执行。");
            return null;
        }

        var matching = boxes.EnumerateArray().Where(box =>
            box.ValueKind == JsonValueKind.Object && TryLong(box, "id", out var boxId) && boxId == id).ToList();
        if (matching.Count != 1)
        {
            errors.Add("沙箱 ID 未唯一匹配到客户端真实绑定，拒绝执行。");
            return null;
        }

        var match = matching[0];
        if (!TryLong(match, "magic", out var boxMagic) || boxMagic != magic)
        {
            errors.Add("沙箱 magic 与客户端绑定不匹配，拒绝执行。");
            return null;
        }

        var path = Text(match, "localPath");
        if (string.IsNullOrWhiteSpace(path) || !IsAbsoluteLocalPath(path!) || !IsTrue(match, "localExists"))
        {
            errors.Add("沙箱没有真实存在的本地绝对路径，拒绝执行。");
            return null;
        }

        return path;
    }

    private static bool IsAbsoluteLocalPath(string path)
    {
        try
        {
            // IsPathRooted("\\folder") 也为真，但那依赖进程当前盘符，并非绝对绑定。
            var root = Path.GetPathRoot(path);
            return Path.IsPathRooted(path) && root is not null
                && ((root.Length >= 3 && root[1] == ':' && (root[2] == '\\' || root[2] == '/'))
                    || root.StartsWith("\\\\", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, List<string>> ReadParents(
        State state, Func<string, string> list, Func<string, string> detail, bool verifyParentTypes,
        List<string>? errors = null)
    {
        var problems = errors ?? state.Refusals;
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in new[] { state.SourceParent, state.TargetParent }
                     .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (verifyParentTypes && parent != "/")
            {
                var parentDetail = ReadDetail(detail, parent, problems, "父目录详情 " + parent);
                ValidateDirectory(parentDetail, problems, "父目录 " + parent);
            }

            var entries = ReadDirectory(list, parent, problems);
            if (entries is not null) result.Add(parent, entries);
        }
        return result;
    }

    private static List<string>? ReadDirectory(Func<string, string> read, string parent, List<string> errors)
    {
        var json = ReadJson(() => read(parent), errors, "父目录列表 " + parent);
        if (json is null) return null;
        var value = json.Value;
        if (!IsTrue(value, "ok") || !value.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array
            || (value.TryGetProperty("truncated", out var truncated) && truncated.ValueKind != JsonValueKind.False)
            || (value.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind != JsonValueKind.False)
            || (value.TryGetProperty("complete", out var complete) && complete.ValueKind != JsonValueKind.True))
        {
            errors.Add("父目录列表失败或不完整，无法确认路径状态：" + parent);
            return null;
        }

        var result = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                errors.Add("父目录列表含非字符串条目，拒绝推断路径不存在：" + parent);
                return null;
            }
            var path = entry.GetString()!;
            // 客户端可能在目录条目末尾带一个 /，先折叠这一个目录标记。
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal)
                && !path.EndsWith("//", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 1);
            if (CloudObjectPath.Validate(path, allowRoot: true) is not null
                || (parent != "/" && !string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("父目录列表含无效或不属于该目录的完整路径：" + parent);
                return null;
            }
            result.Add(path);
        }
        return result;
    }

    private static void CheckBefore(State state, Dictionary<string, List<string>> parents)
    {
        if (state.Source is not null && state.SourceParent is not null
            && parents.TryGetValue(state.SourceParent, out var sourceEntries))
        {
            var count = sourceEntries.Count(path => string.Equals(path, state.Source, StringComparison.OrdinalIgnoreCase));
            if (count != 1) state.Refusals.Add("源不存在或有大小写冲突，无法唯一确认：" + state.Source);
            if (sourceEntries.Any(path => path.StartsWith(state.Source + "/", StringComparison.OrdinalIgnoreCase)))
                state.Refusals.Add("源有后代对象，本操作只支持普通文件，拒绝移动目录。");
        }
        if (state.TargetParent is not null && parents.TryGetValue(state.TargetParent, out var targetEntries)
            && ContainsTarget(targetEntries, state.Target))
            state.Refusals.Add("目标已存在或有大小写冲突，拒绝覆盖：" + state.Target);
    }

    private static bool ContainsTarget(IEnumerable<string> entries, string path) => entries.Any(entry =>
        string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)
        || entry.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    private static Detail? ReadDetail(Func<string, string> read, string path, List<string> errors, string label)
    {
        var json = ReadJson(() => read(path), errors, label);
        if (json is null) return null;
        var value = json.Value;
        if (!IsTrue(value, "ok") || !string.Equals(Text(value, "cloudPath"), path, StringComparison.Ordinal))
        {
            errors.Add(label + "失败或返回的云端路径不匹配：" + path);
            return null;
        }
        return new Detail(value);
    }

    private static void ValidateFile(Detail? detail, List<string> errors, string label)
    {
        if (detail is null) return;
        if (!string.Equals(detail.Type, "file", StringComparison.OrdinalIgnoreCase))
            errors.Add(label + "不是普通文件；本操作拒绝目录。");
        if (detail.Deleted != false) errors.Add(label + "已删除或删除状态未知。");
        if (detail.Version is null || detail.Version <= 0 || detail.Size is null || detail.Size < 0
            || string.IsNullOrWhiteSpace(detail.Hash))
            errors.Add(label + "缺少有效 version / size / hash，拒绝执行。");
    }

    private static void ValidateDirectory(Detail? detail, List<string> errors, string label)
    {
        if (detail is null) return;
        if (!string.Equals(detail.Type, "directory", StringComparison.OrdinalIgnoreCase) || detail.Deleted != false)
            errors.Add(label + "不是未删除的云端目录，拒绝执行。");
    }

    private static void Execute(State state, Func<string> execute)
    {
        state.Attempted = true;
        state.Result = ReadJson(execute, state.Refusals, "调用客户端写入实现");
        if (state.Result is null) return;
        state.HookOk = IsTrue(state.Result.Value, "ok");
        if (!state.HookOk)
        {
            state.Refusals.Add("客户端写入失败：" + (Text(state.Result.Value, "reason")
                ?? Text(state.Result.Value, "error") ?? "返回 ok=false 或缺少真实成功标志。"));
        }

        if (state.Result.Value.TryGetProperty("verification", out var verification))
        {
            if (verification.ValueKind == JsonValueKind.String)
                state.HookVerification = ReadJson(() => verification.GetString()!, state.VerificationErrors, "默认域写后复查");
            else if (verification.ValueKind == JsonValueKind.Object)
                state.HookVerification = verification.Clone();
        }
        state.HookVerified = state.HookVerification is not null && IsTrue(state.HookVerification.Value, "ok");
        if (!state.HookVerified) state.Refusals.Add("默认域没有确认写入后的真实状态，不能报告执行成功。");
    }

    private static void VerifyMove(State state, Func<string, string> list, Func<string, string> detail)
    {
        state.VerificationChecked = true;
        var parents = ReadParents(state, list, detail, verifyParentTypes: false, state.VerificationErrors);
        if (parents.TryGetValue(state.SourceParent!, out var sources))
            state.SourcePresent = ContainsTarget(sources, state.Source!);
        if (parents.TryGetValue(state.TargetParent!, out var targets))
            state.TargetPresent = targets.Count(path => string.Equals(path, state.Target, StringComparison.OrdinalIgnoreCase)) == 1;
        if (state.SourcePresent != false) state.VerificationErrors.Add("写后源仍存在或无法确认已经消失。");
        if (state.TargetPresent != true) state.VerificationErrors.Add("写后目标没有唯一出现或无法确认。");
        state.TargetDetail = ReadDetail(detail, state.Target, state.VerificationErrors, "写后目标详情");
        ValidateFile(state.TargetDetail, state.VerificationErrors, "写后目标");
        if (state.SourceDetail is not null && state.TargetDetail is not null)
        {
            state.ContentMatches = state.SourceDetail.Size == state.TargetDetail.Size
                && string.Equals(state.SourceDetail.Hash, state.TargetDetail.Hash, StringComparison.Ordinal);
            if (state.ContentMatches != true) state.VerificationErrors.Add("写后目标的大小或 hash 与原文件不一致。");
        }
        FinishVerification(state);
    }

    private static void VerifyMkdir(State state, Func<string, string> list, Func<string, string> detail)
    {
        state.VerificationChecked = true;
        var parents = ReadParents(state, list, detail, verifyParentTypes: false, state.VerificationErrors);
        if (parents.TryGetValue(state.TargetParent!, out var targets))
            state.TargetPresent = targets.Count(path => string.Equals(path, state.Target, StringComparison.OrdinalIgnoreCase)) == 1;
        if (state.TargetPresent != true) state.VerificationErrors.Add("写后新目录没有唯一出现或无法确认。");
        state.TargetDetail = ReadDetail(detail, state.Target, state.VerificationErrors, "写后目录详情");
        ValidateDirectory(state.TargetDetail, state.VerificationErrors, "写后目标");
        FinishVerification(state);
    }

    private static void FinishVerification(State state)
    {
        state.VerificationOk = state.VerificationErrors.Count == 0 && state.HookVerified;
        state.Executed = state.HookOk && state.VerificationOk;
        foreach (var error in state.VerificationErrors) state.Refusals.Add("写后复查：" + error);
    }

    private static JsonElement? ReadJson(Func<string> read, List<string> errors, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(read());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(label + "没有返回 JSON 对象。");
                return null;
            }
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            errors.Add(label + "失败：" + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static bool IsTrue(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var member)
        && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    private static bool TryLong(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var member)
            && (member.ValueKind == JsonValueKind.Number ? member.TryGetInt64(out number)
                : member.ValueKind == JsonValueKind.String && long.TryParse(member.GetString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out number));
    }

    private static bool? ReadBool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member)) return null;
        if (member.ValueKind == JsonValueKind.True) return true;
        if (member.ValueKind == JsonValueKind.False) return false;
        return member.ValueKind == JsonValueKind.String && bool.TryParse(member.GetString(), out var flag) ? flag : null;
    }

    private static string Serialize(State state) => JsonSerializer.Serialize(new
    {
        allowed = state.Refusals.Count == 0,
        wouldWrite = state.Executed,
        dryRun = state.DryRun,
        unlockVariablePresent = state.Unlocked,
        attempted = state.Attempted,
        executed = state.Executed,
        probe = new
        {
            operation = state.Operation, sandboxId = state.Id, magic = state.Magic,
            srcCloudPath = state.Source, destCloudPath = state.Target, localRoot = state.LocalRoot,
            sourceParent = state.SourceParent, targetParent = state.TargetParent,
            preflightOk = state.PreflightOk, source = state.SourceDetail?.Summary,
        },
        refusals = state.Refusals,
        result = state.Result,
        verification = new
        {
            checkedState = state.VerificationChecked,
            ok = state.VerificationChecked ? (bool?)state.VerificationOk : null,
            sourcePresent = state.SourcePresent, targetPresent = state.TargetPresent,
            contentMatches = state.ContentMatches, target = state.TargetDetail?.Summary,
            hook = state.HookVerification, errors = state.VerificationErrors,
        },
        notes = new[]
        {
            "只调用客户端自己的实现；目标存在或仅大小写冲突均拒绝覆盖。",
            "attempted 表示已尝试调用，executed 仅在默认域和插件复查都确认成功时为 true。",
            "失败后只读复查真实状态，不自动重试，也不自动回滚。",
        },
    }, JsonOptions);

    private sealed class Detail
    {
        public Detail(JsonElement value)
        {
            Type = Text(value, "objectType");
            Hash = Text(value, "hash");
            Deleted = ReadBool(value, "deleted");
            Version = TryLong(value, "version", out var version) ? version : null;
            Size = TryLong(value, "sizeBytes", out var size) || TryLong(value, "size", out size) ? size : null;
        }
        public string? Type { get; }
        public string? Hash { get; }
        public bool? Deleted { get; }
        public long? Version { get; }
        public long? Size { get; }
        public object Summary => new { objectType = Type, version = Version, sizeBytes = Size, hash = Hash, deleted = Deleted };
    }

    private sealed class State
    {
        public State(string operation, bool unlocked, bool dryRun, long id, long magic, string? source, string target)
        {
            Operation = operation; Unlocked = unlocked; DryRun = dryRun;
            Id = id; Magic = magic; Source = source; Target = target;
        }
        public string Operation { get; }
        public bool Unlocked { get; }
        public bool DryRun { get; }
        public long Id { get; }
        public long Magic { get; }
        public string? Source { get; }
        public string Target { get; }
        public string? SourceParent { get; set; }
        public string? TargetParent { get; set; }
        public string? LocalRoot { get; set; }
        public bool PreflightOk { get; set; }
        public bool Attempted { get; set; }
        public bool Executed { get; set; }
        public bool HookOk { get; set; }
        public bool HookVerified { get; set; }
        public bool VerificationChecked { get; set; }
        public bool VerificationOk { get; set; }
        public bool? SourcePresent { get; set; }
        public bool? TargetPresent { get; set; }
        public bool? ContentMatches { get; set; }
        public Detail? SourceDetail { get; set; }
        public Detail? TargetDetail { get; set; }
        public JsonElement? Result { get; set; }
        public JsonElement? HookVerification { get; set; }
        public List<string> Refusals { get; } = new();
        public List<string> VerificationErrors { get; } = new();
    }
}
