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

    /// <summary>
    /// 内联 base64 内容的上限（256 KiB）。本地文件来源不受这个限制：
    /// 它走 <c>CreateCloudFileFromLocalFile</c>，上限是
    /// <see cref="CloudContentCodec.MaxFileUploadBytes"/>（16 MiB）。
    /// </summary>
    /// <remarks>
    /// 256 KiB 与单次内联读取上限一致；客户端在 393216 字节以下走全量上传、
    /// 4 MiB 才分块算云端 hash，所以内联范围内「全量上传 + 单块 hash」两条路径都已核对过。
    /// </remarks>
    public const int MaxContentBytes = 262144;

    /// <summary>上界对应的 base64 长度（含 padding）。</summary>
    public const int MaxEncodedLength = ((MaxContentBytes + 2) / 3) * 4;

    /// <summary>
    /// 先按编码长度挡一次，再去解码。
    /// 目的是**不为超大输入先解出一大块内存** —— base64 解码发生在守卫之前，
    /// 没有这道检查的话，一个远超上限的请求会先被完整解码进内存才被拒绝。
    /// 带换行的编码会偏大而被拒；调用方应传紧凑 base64。
    /// </summary>
    public static string? ValidateEncodedLength(int encodedLength)
        => encodedLength > MaxEncodedLength
            ? "内容超过 " + MaxContentBytes + " 字节上限（base64 长度 " + encodedLength + " > " + MaxEncodedLength + "）。"
            : null;

    /// <summary>
    /// B2/C3 受限创建：在**本次调用新建、且建后确认为空**的目录里创建一个新文件。
    ///
    /// 安全性来自空的父目录，而不是名字长什么样：新目录里没有任何既有对象，所以
    /// 随后创建的文件不可能与任何对象构成大小写别名（上一轮实测的别名冲突会被
    /// 客户端改名，而改名的同内容/目录分支可能移动甚至删除既有对象）。
    /// 客户端 <c>cf02</c> 本体没有查重也没有 noReplace，防覆写完全靠这里的检查。
    ///
    /// <paramref name="contentLength"/> 为 0 表示创建空文件：客户端对空内容走特殊分支
    /// （跳过内容上传、云端 hash 为 32 个零字节、块列表名用 40 个零字节的文件记录），
    /// 这里只负责把「空」如实传下去，写后仍按类型、大小 0、hash、版本与目录成员核对。
    /// </summary>
    public static string CreateFile(
        bool unlocked, bool confirm, bool dryRun, long id, long magic,
        string directoryName, string fileName, int contentLength,
        Func<string> bindings, Func<string, string> listDirectory, Func<string, string> readDetail,
        Func<string> executeCreateDirectory, Func<string> executeCreateFile,
        string? contentSource = null, int maxContentBytes = MaxContentBytes)
    {
        var directory = "/" + directoryName;
        var target = directory + "/" + fileName;
        var state = new State("create-file", unlocked, dryRun, id, magic, null, target);
        state.Directory = directory;
        state.ContentLength = contentLength;
        state.ContentSource = contentSource;

        BasicGuards(state, confirm);
        Add(state.Refusals, ValidateSegment(directoryName, "目录名"));
        Add(state.Refusals, ValidateSegment(fileName, "文件名"));
        if (contentLength < 0)
            state.Refusals.Add("内容长度不能为负。");
        if (contentLength > maxContentBytes)
            state.Refusals.Add("内容超过 " + maxContentBytes + " 字节上限。");

        if (state.Refusals.Count == 0)
        {
            state.LocalRoot = ReadBinding(bindings, id, magic, state.Refusals);
            if (state.Refusals.Count == 0)
            {
                // 沙箱根下不得有同名或大小写别名，否则会把新文件放进一个已有目录。
                var rootEntries = ReadDirectory(listDirectory, "/", state.Refusals);
                if (rootEntries is not null && ContainsTarget(rootEntries, directory))
                    state.Refusals.Add("沙箱根下已有同名或大小写别名，拒绝创建：" + directory);
                state.PreflightOk = state.Refusals.Count == 0;
            }
        }

        if (state.PreflightOk && !dryRun)
        {
            Execute(state, executeCreateDirectory);
            // 默认域必须确认新目录真实存在，否则连试都不试文件创建。
            if (state.HookOk && state.HookVerified)
            {
                state.DirectoryCreated = true;
                state.DirectoryVerification = state.HookVerification;

                // 结构保证：新目录必须确认为空，否则绝不在里面创建文件。
                var entries = ReadDirectory(listDirectory, directory, state.VerificationErrors);
                if (entries is null)
                    state.VerificationErrors.Add("无法确认新目录为空，拒绝在其中创建文件。");
                else
                {
                    state.DirectoryEmpty = entries.Count == 0;
                    state.RootEntryCount = entries.Count;
                    if (state.DirectoryEmpty != true)
                        state.VerificationErrors.Add("新目录不是空的（" + entries.Count + " 项），拒绝创建文件。");
                }

                if (state.DirectoryEmpty == true)
                {
                    Execute(state, executeCreateFile);
                    state.HookCloudHash = state.Result is { } result && Text(result, "cloudHash") is { } hash ? hash : null;
                    VerifyCreateFile(state, listDirectory, readDetail);
                }
            }
        }

        return Serialize(state);
    }

    /// <summary>单个路径段的命名规则；工具不接受调用方给出的多段路径。</summary>
    private static string? ValidateSegment(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name)) return label + "不能为空。";
        if (name.Length > 255) return label + "超过 255 字符。";
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return label + "首尾不能有空白。";
        if (name.EndsWith(".", StringComparison.Ordinal)) return label + "不能以点结尾。";
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0) return label + "不能包含路径分隔符。";
        if (name.Any(c => char.IsControl(c) || "<>:\"|?*".IndexOf(c) >= 0)) return label + "包含非法字符。";
        if (name is "." or "..") return label + "不能是 . 或 ..。";
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] >= '1' && stem[3] <= '9')))
            return label + "是 Windows 保留设备名。";
        return null;
    }

    private static void Add(List<string> errors, string? error)
    {
        if (error is not null) errors.Add(error);
    }

    private static void VerifyCreateFile(State state, Func<string, string> list, Func<string, string> detail)
    {
        state.VerificationChecked = true;
        var entries = ReadDirectory(list, state.Directory!, state.VerificationErrors);
        if (entries is not null)
            state.TargetPresent = entries.Count(path => string.Equals(path, state.Target, StringComparison.OrdinalIgnoreCase)) == 1;
        if (state.TargetPresent != true) state.VerificationErrors.Add("写后新文件没有在目录里唯一出现或无法确认。");

        state.TargetDetail = ReadDetail(detail, state.Target, state.VerificationErrors, "写后文件详情");
        ValidateFile(state.TargetDetail, state.VerificationErrors, "写后文件");
        if (state.TargetDetail?.Size is { } size) state.ContentMatches = size == state.ContentLength;
        if (state.HookCloudHash is not null && state.TargetDetail?.Hash is not null)
            state.ContentMatches = state.ContentMatches == true
                && string.Equals(state.HookCloudHash, state.TargetDetail.Hash, StringComparison.Ordinal);
        if (state.ContentMatches != true)
            state.VerificationErrors.Add("写后文件的大小或云端 hash 与本次上传的内容不一致。");
        FinishVerification(state);
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
        // 每次调用前先清空上一次的结果：受限创建会连着调两次（建目录、建文件），
        // 不清空的话第二次失败会继承第一次的 ok / verified，直接误报成功。
        state.Result = null;
        state.HookOk = false;
        state.HookVerified = false;
        state.HookVerification = null;

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

    private static string Serialize(State state)
    {
        var notes = new List<string>
        {
            "只调用客户端自己的实现；目标存在或仅大小写冲突均拒绝覆盖。",
            "attempted 表示已尝试调用，executed 仅在默认域和插件复查都确认成功时为 true。",
            "失败后只读复查真实状态，不自动重试，也不自动回滚。",
            state.Operation == "create-file"
                ? "受限创建只用于本次调用新建、且建后确认为空的目录；父目录非空时不会创建文件。"
                : "移动与建目录都在真实绑定、父目录和目标冲突核对之后才提交。",
        };
        if (state.Operation == "create-file" && state.ContentLength == 0)
        {
            notes.Add("内容长度为 0 时走客户端空文件分支：跳过内容上传、云端 hash 用 32 个零字节；写后仍核对大小 0 与云端 hash。");
        }

        return JsonSerializer.Serialize(new
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
            directory = state.Directory, contentBytes = state.Operation == "create-file" ? state.ContentLength : (int?)null,
            emptyContent = state.Operation == "create-file" ? state.ContentLength == 0 : (bool?)null,
            contentSource = state.ContentSource,
            directoryCreated = state.Operation == "create-file" ? state.DirectoryCreated : (bool?)null,
            directoryEmpty = state.Operation == "create-file" ? state.DirectoryEmpty : (bool?)null,
        },
        refusals = state.Refusals,
        result = state.Result,
        verification = new
        {
            checkedState = state.VerificationChecked,
            ok = state.VerificationChecked ? (bool?)state.VerificationOk : null,
            sourcePresent = state.SourcePresent, targetPresent = state.TargetPresent,
            contentMatches = state.ContentMatches, target = state.TargetDetail?.Summary,
            directory = state.DirectoryDetail?.Summary,
            directoryVerification = state.DirectoryVerification,
            hook = state.HookVerification, errors = state.VerificationErrors,
        },
        notes = notes,
    }, JsonOptions);
    }

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
        public string? Directory { get; set; }
        public int ContentLength { get; set; }

        /// <summary>内容来源（内联 base64 或本地文件），只用于报告，不参与判断。</summary>
        public string? ContentSource { get; set; }
        public bool DirectoryCreated { get; set; }
        public bool? DirectoryEmpty { get; set; }
        public int? RootEntryCount { get; set; }
        public string? HookCloudHash { get; set; }
        public Detail? DirectoryDetail { get; set; }
        public JsonElement? DirectoryVerification { get; set; }
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
