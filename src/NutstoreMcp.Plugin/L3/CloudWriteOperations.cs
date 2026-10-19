using System.Globalization;
using System.Text;
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
    /// 一次成功创建的结果：交给上层写创建台账。
    /// 删除工具只认台账里登记过、且后续状态完全匹配的对象。
    /// </summary>
    public sealed record CreatedObject(
        string Path, long Version, string? Hash, int Size, string? ContentSource, string Operation);

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
        string? contentSource = null, int maxContentBytes = MaxContentBytes,
        Func<CreatedObject, bool>? recordCreated = null)
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

        RecordCreatedObject(state, recordCreated);

        return Serialize(state);
    }

    /// <summary>
    /// 父目录成员数的自设上限。超过就拒绝创建 —— 写后复查要逐个核对既有成员的
    /// 版本与哈希，成员太多时无法在可接受的时间内完成完整比对。
    /// </summary>
    public const int MaxExistingDirectoryMembers = 100;

    /// <summary>
    /// P1：在**已存在**的云端目录里创建一个新文件。
    ///
    /// 与 <see cref="CreateFile"/> 的区别只是「父目录不是本次新建」，所以安全性不再来自
    /// 「空目录」这一结构，而来自三层约束：
    /// <list type="number">
    /// <item>两端预检：云端父目录全量列举 + 本地绑定文件夹枚举，任一大小写等价即拒绝；
    /// 父路径本身也必须逐字符一致（云端详情回读 + 本地逐段大小写核对）。</item>
    /// <item>提交前复查：默认域钩子上传内容后、提交元数据前再只读确认目标仍不存在。</item>
    /// <item>写后三重复查：父目录恰好多出目标且其余成员版本/哈希不变 → 目标大小/hash/版本
    /// → 把云端字节整文件读回来核对 SHA-256。任何不符只报告，不重试、不回滚。</item>
    /// </list>
    ///
    /// 残余风险：预检与发布之间存在并发窗口（服务端没有大小写原子去重，已实测确认）。
    /// 这一点按复核结论接受。
    /// </summary>
    /// <param name="expectedSha256">返回期望内容的 SHA-256（小写十六进制）；在写后复查时调用。</param>
    /// <param name="readInline">按区间读取云端文件内容，返回钩子的 JSON（含 text/contentBase64）。</param>
    /// <param name="probeLocal">本地侧预检：给定绑定根与云端目录，返回本地枚举结果。</param>
    public static string CreateFileInExistingDirectory(
        bool unlocked, bool confirm, bool dryRun, long id, long magic,
        string cloudDirectory, string fileName, int contentLength,
        Func<string> bindings, Func<string, string> listDirectory, Func<string, string> readDetail,
        Func<string, string, LocalDirectoryProbe.Result> probeLocal,
        Func<string> executeCreateFile,
        Func<string, long, long, string> readInline,
        Func<string> expectedSha256,
        string? contentSource = null, int maxContentBytes = MaxContentBytes,
        Func<CreatedObject, bool>? recordCreated = null)
    {
        var target = (cloudDirectory == "/" ? string.Empty : cloudDirectory) + "/" + fileName;
        var state = new State("create-file-existing", unlocked, dryRun, id, magic, null, target);
        state.Directory = cloudDirectory;
        state.ContentLength = contentLength;
        state.ContentSource = contentSource;
        state.ExistingDirectory = true;

        BasicGuards(state, confirm);
        Add(state.Refusals, CloudObjectPath.Validate(cloudDirectory, allowRoot: true));
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
                // 云端父目录必须是未删除的真实目录；ReadDetail 要求返回路径逐字符一致，
                // 因此父路径本身的大小写别名也会在这里被挡住。
                // 例外：根路径的详情客户端明确拒绝查询（You cannot query the full info of root path），
                // 而根一定存在且是目录，所以只跳过详情这一步，目标冲突仍由列表检查。
                if (!string.Equals(cloudDirectory, "/", StringComparison.Ordinal))
                {
                    state.DirectoryDetail = ReadDetail(readDetail, cloudDirectory, state.Refusals, "父目录详情");
                    ValidateDirectory(state.DirectoryDetail, state.Refusals, "父目录 " + cloudDirectory);
                }

                var entries = ReadDirectory(listDirectory, cloudDirectory, state.Refusals);
                if (entries is not null && state.Refusals.Count == 0)
                {
                    if (ContainsTarget(entries, target))
                    {
                        state.Refusals.Add("目标已存在或有大小写冲突，拒绝创建：" + target);
                    }
                    else if (entries.Count > MaxExistingDirectoryMembers)
                    {
                        state.Refusals.Add("父目录有 " + entries.Count + " 个成员，超过 " + MaxExistingDirectoryMembers
                            + " 项上限，拒绝创建（无法完整比对全部成员）。");
                    }
                    else
                    {
                        state.BeforeMembers = SnapshotMembers(entries, target, readDetail, state.Refusals);
                        state.MemberCountBefore = state.BeforeMembers?.Count;
                    }
                }

                if (state.Refusals.Count == 0)
                {
                    // 本地一侧：客户端的冲突改名/删除分支同样会作用到本地真实文件。
                    var local = probeLocal(state.LocalRoot!, cloudDirectory);
                    state.LocalSnapshotExists = local.PathExists;
                    state.LocalSnapshotCount = local.Entries.Count;
                    if (local.Error is not null)
                    {
                        state.Refusals.Add(local.Error);
                    }
                    else if (local.PathExists)
                    {
                        var collision = LocalDirectoryProbe.FindCaseInsensitiveCollision(local.Entries, fileName);
                        if (collision is not null)
                            state.Refusals.Add("本地绑定文件夹里已有同名或大小写别名：" + collision + "，拒绝创建。");
                    }
                }

                state.PreflightOk = state.Refusals.Count == 0;
            }
        }

        if (state.PreflightOk && !dryRun)
        {
            Execute(state, executeCreateFile);
            state.HookCloudHash = state.Result is { } result && Text(result, "cloudHash") is { } hash ? hash : null;
            // 只有钩子报告成功时才做写后复查：失败/未知时目标状态没有可信前提，
            // 但仍把钩子给的原因原样带出去，不做任何自动重试或回滚。
            if (state.HookOk)
            {
                VerifyCreateFileInExisting(state, listDirectory, readDetail, readInline, expectedSha256);
            }
        }

        RecordCreatedObject(state, recordCreated);

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

    /// <summary>P1 写后三重复查：父目录成员、目标详情、整文件回读字节。</summary>
    private static void VerifyCreateFileInExisting(State state, Func<string, string> list,
        Func<string, string> detail, Func<string, long, long, string> readInline, Func<string> expectedSha256)
    {
        state.VerificationChecked = true;

        // ── 第一重：父目录正好多出目标，其余成员一个不变（路径/类型/版本/哈希/大小）──
        var entries = ReadDirectory(list, state.Directory!, state.VerificationErrors);
        if (entries is not null)
        {
            var exact = entries.Count(path => string.Equals(path, state.Target, StringComparison.Ordinal));
            var aliases = entries.Count(path => string.Equals(path, state.Target, StringComparison.OrdinalIgnoreCase)) - exact;
            state.TargetPresent = exact == 1 && aliases == 0;
            if (state.TargetPresent != true)
                state.VerificationErrors.Add("写后新文件没有唯一、逐字符地出现在父目录里（含大小写别名检查）。");

            var after = SnapshotMembers(entries, state.Target, detail, state.VerificationErrors);
            state.MemberCountAfter = after?.Count;
            if (after is not null && state.BeforeMembers is not null)
            {
                state.MembersUnchanged = SameMembers(state.BeforeMembers, after);
                if (state.MembersUnchanged != true)
                    state.VerificationErrors.Add("写后父目录里既有成员发生了变化（数量、路径、类型、版本或哈希不一致）。");
            }
            else if (state.BeforeMembers is null)
            {
                state.VerificationErrors.Add("没有可用的写前成员快照，无法确认父目录其他成员未变。");
            }
        }

        // ── 第二重：目标自身的大小、云端 hash、版本 ──
        state.TargetDetail = ReadDetail(detail, state.Target, state.VerificationErrors, "写后文件详情");
        ValidateFile(state.TargetDetail, state.VerificationErrors, "写后文件");
        if (state.TargetDetail?.Size is { } size) state.ContentMatches = size == state.ContentLength;
        if (state.HookCloudHash is not null && state.TargetDetail?.Hash is not null)
            state.ContentMatches = state.ContentMatches == true
                && string.Equals(state.HookCloudHash, state.TargetDetail.Hash, StringComparison.Ordinal);
        if (state.ContentMatches != true)
            state.VerificationErrors.Add("写后文件的大小或云端 hash 与本次上传的内容不一致。");

        // ── 第三重：把云端字节整文件读回来，SHA-256 与期望内容一致 ──
        VerifyReadBack(state, readInline, expectedSha256);

        FinishVerification(state);
    }

    /// <summary>按 256 KiB 分段把云端文件读回来（与读取侧上限一致），整文件算一次 SHA-256。</summary>
    private static void VerifyReadBack(State state, Func<string, long, long, string> readInline,
        Func<string> expectedSha256)
    {
        state.ReadBackChecked = true;

        string? expected;
        try
        {
            expected = expectedSha256();
        }
        catch (Exception ex)
        {
            state.VerificationErrors.Add("拿不到期望内容的 SHA-256（" + ex.GetType().Name + "），无法回读校验。");
            return;
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            state.VerificationErrors.Add("期望内容的 SHA-256 为空，无法回读校验。");
            return;
        }

        using var all = new MemoryStream();
        long offset = 0;
        const int chunk = 256 * 1024;
        while (offset < state.ContentLength)
        {
            var want = (int)Math.Min(chunk, state.ContentLength - offset);
            var json = ReadJson(() => readInline(state.Target, offset, want), state.VerificationErrors, "回读分段");
            if (json is null) return;

            var value = json.Value;
            if (!IsTrue(value, "ok")
                || !TryLong(value, "offset", out var returnedOffset) || returnedOffset != offset
                || !TryLong(value, "length", out var returnedLength) || returnedLength != want)
            {
                state.VerificationErrors.Add("回读分段返回的范围与请求不一致。");
                return;
            }

            var segment = ReadSegmentBytes(value, state.VerificationErrors);
            if (segment is null) return;
            if (segment.Length != want)
            {
                state.VerificationErrors.Add("回读分段的字节数与请求不一致。");
                return;
            }

            all.Write(segment, 0, segment.Length);
            offset += want;
        }

        var bytes = all.ToArray();
        if (bytes.Length != state.ContentLength)
        {
            state.VerificationErrors.Add("回读到的总字节数与期望内容不一致。");
            return;
        }

        var actual = CloudContentCodec.Sha256Hex(bytes);
        state.ReadBackSha256 = actual;
        state.ReadBackMatches = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        if (state.ReadBackMatches != true)
            state.VerificationErrors.Add("写后回读的字节与期望内容不一致（SHA-256 不匹配）。");
    }

    /// <summary>从读取侧返回的 JSON 里还原原始字节：二进制走 contentBase64，文本走 text（BOM 补回）。</summary>
    private static byte[]? ReadSegmentBytes(JsonElement value, List<string> errors)
    {
        if (value.TryGetProperty("contentBase64", out var base64) && base64.ValueKind == JsonValueKind.String)
        {
            try
            {
                return Convert.FromBase64String(base64.GetString()!);
            }
            catch (FormatException)
            {
                errors.Add("回读分段不是有效的 base64。");
                return null;
            }
        }

        if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            var bytes = Encoding.UTF8.GetBytes(text.GetString()!);

            // 读取侧会把 UTF-8 BOM 从文本里剥掉并单独标记；校验字节时必须补回来。
            if (ReadBool(value, "bom") == true)
            {
                var withBom = new byte[bytes.Length + 3];
                withBom[0] = 0xEF;
                withBom[1] = 0xBB;
                withBom[2] = 0xBF;
                Buffer.BlockCopy(bytes, 0, withBom, 3, bytes.Length);
                return withBom;
            }

            return bytes;
        }

        errors.Add("回读分段既没有 contentBase64 也没有 text。");
        return null;
    }

    /// <summary>父目录成员快照；目标自身（含大小写别名）不进入成员列表。</summary>
    private static List<DirectoryMember>? SnapshotMembers(List<string> entries, string target,
        Func<string, string> detail, List<string> errors)
    {
        var members = new List<DirectoryMember>();
        foreach (var entry in entries)
        {
            if (string.Equals(entry, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var memberDetail = ReadDetail(detail, entry, errors, "父目录成员详情 " + entry);
            if (memberDetail is null) return null;
            members.Add(new DirectoryMember(entry, memberDetail.Type, memberDetail.Version,
                memberDetail.Hash, memberDetail.Size, memberDetail.Deleted));
        }

        return members;
    }

    /// <summary>两份成员快照是否完全一致（路径逐字符比较，大小写改名也算变化）。</summary>
    private static bool SameMembers(List<DirectoryMember> before, List<DirectoryMember> after)
    {
        if (before.Count != after.Count) return false;

        var index = new Dictionary<string, DirectoryMember>(StringComparer.Ordinal);
        foreach (var member in after)
        {
            index[member.Path] = member;
        }

        foreach (var member in before)
        {
            if (!index.TryGetValue(member.Path, out var other)) return false;
            if (!string.Equals(member.Type, other.Type, StringComparison.Ordinal)
                || member.Version != other.Version
                || !string.Equals(member.Hash, other.Hash, StringComparison.Ordinal)
                || member.Size != other.Size
                || member.Deleted != other.Deleted)
            {
                return false;
            }
        }

        return true;
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

    internal static string? ReadBinding(Func<string> read, long id, long magic, List<string> errors)
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

    internal static List<string>? ReadDirectory(Func<string, string> read, string parent, List<string> errors)
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

    /// <summary>创建成功后写创建台账；台账写失败不影响本次创建结果，但会在返回里说明。</summary>
    private static void RecordCreatedObject(State state, Func<CreatedObject, bool>? record)
    {
        if (!state.Executed) return;
        state.LedgerAttempted = record is not null;
        if (record is null) return;

        var version = state.TargetDetail?.Version ?? 0;
        var hash = state.TargetDetail?.Hash;
        if (version <= 0 || string.IsNullOrWhiteSpace(hash))
        {
            state.LedgerRecorded = false;
            return;
        }

        try
        {
            state.LedgerRecorded = record(new CreatedObject(
                state.Target, version, hash, state.ContentLength, state.ContentSource, state.Operation));
        }
        catch (Exception)
        {
            state.LedgerRecorded = false;
        }
    }

    internal static JsonElement? ReadJson(Func<string> read, List<string> errors, string label)
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

    internal static bool IsTrue(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.True;

    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var member)
        && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    internal static bool TryLong(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var member)
            && (member.ValueKind == JsonValueKind.Number ? member.TryGetInt64(out number)
                : member.ValueKind == JsonValueKind.String && long.TryParse(member.GetString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out number));
    }

    internal static bool? ReadBool(JsonElement value, string name)
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
            state.ExistingDirectory
                ? "已有目录创建：云端父目录与本地绑定文件夹两端预检，任一大小写别名即拒绝；"
                    + "写后确认父目录只多出目标、其余成员版本/哈希未变，并把云端字节整文件回读核对 SHA-256。"
                : state.Operation == "create-file"
                    ? "受限创建只用于本次调用新建、且建后确认为空的目录；父目录非空时不会创建文件。"
                    : "移动与建目录都在真实绑定、父目录和目标冲突核对之后才提交。",
        };
        if (state.Operation == "create-file" && state.ContentLength == 0)
        {
            notes.Add("内容长度为 0 时走客户端空文件分支：跳过内容上传、云端 hash 用 32 个零字节；写后仍核对大小 0 与云端 hash。");
        }

        if (state.ExistingDirectory)
        {
            notes.Add("本模式的残余风险是预检与发布之间的并发窗口：服务端没有大小写原子去重，"
                + "这类窗口由写后复查发现并如实报告，不重试、不回滚。");
        }

        if (state.Executed && state.LedgerAttempted && state.LedgerRecorded == false)
        {
            notes.Add("创建已成功，但创建台账写入失败：该对象以后不能通过 nutstore_l3_delete_created_object 撤销。");
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
            directory = state.Directory,
            existingDirectory = state.ExistingDirectory,
            contentBytes = state.ExistingDirectory || state.Operation == "create-file" ? state.ContentLength : (int?)null,
            emptyContent = state.ExistingDirectory || state.Operation == "create-file" ? state.ContentLength == 0 : (bool?)null,
            contentSource = state.ContentSource,
            directoryCreated = state.Operation == "create-file" ? state.DirectoryCreated : (bool?)null,
            directoryEmpty = state.Operation == "create-file" ? state.DirectoryEmpty : (bool?)null,
            parentMembersBefore = state.MemberCountBefore,
            localSnapshotExists = state.LocalSnapshotExists,
            localSnapshotCount = state.LocalSnapshotCount,
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
            memberCountBefore = state.MemberCountBefore,
            memberCountAfter = state.MemberCountAfter,
            membersUnchanged = state.MembersUnchanged,
            readBackChecked = state.ReadBackChecked ? (bool?)true : null,
            readBackMatches = state.ReadBackMatches,
            readBackSha256 = state.ReadBackSha256,
            ledgerRecorded = state.LedgerRecorded,
            hook = state.HookVerification, errors = state.VerificationErrors,
        },
        notes = notes,
    }, JsonOptions);
    }

    /// <summary>父目录成员快照：写后复查用它对每个既有成员比对路径、类型、版本与哈希。</summary>
    private sealed record DirectoryMember(string Path, string? Type, long? Version, string? Hash, long? Size, bool? Deleted);

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

        /// <summary>本次是否「已存在目录」模式（P1）；该模式下不新建父目录。</summary>
        public bool ExistingDirectory { get; set; }
        public List<DirectoryMember>? BeforeMembers { get; set; }
        public int? MemberCountBefore { get; set; }
        public int? MemberCountAfter { get; set; }
        public bool? MembersUnchanged { get; set; }
        public bool ReadBackChecked { get; set; }
        public bool? ReadBackMatches { get; set; }
        public string? ReadBackSha256 { get; set; }
        public bool? LocalSnapshotExists { get; set; }
        public int? LocalSnapshotCount { get; set; }

        /// <summary>是否尝试写创建台账；台账写入结果（false 表示以后不能通过删除工具撤销）。</summary>
        public bool LedgerAttempted { get; set; }
        public bool? LedgerRecorded { get; set; }
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
