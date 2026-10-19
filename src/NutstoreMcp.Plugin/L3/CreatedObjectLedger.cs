// MCP 创建台账 —— 删除工具只认这里记录过的对象。
//
// 为什么需要它：删除是唯一会「让对象消失」的写操作，而客户端的删除会同步到本地绑定目录。
// 把可删除范围限定在「MCP 自己刚创建、且当前版本/哈希与创建时完全一致」的对象上，
// 破坏面就从整棵树收缩到我们自己写出去的那几个文件。
//
// 台账被篡改即等于任意删除 —— 所以删除工具在台账之外还会再做一遍
// 「路径在已绑定沙箱内 + 不是通配/大小写别名 + 当前详情版本与哈希完全匹配」的校验。
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace NutstoreMcp.Plugin.L3;

/// <summary>创建台账：一行一条 JSON（jsonl），只追加、不重写。</summary>
public static class CreatedObjectLedger
{
    /// <summary>台账文件名，放在写入令牌旁边。</summary>
    public const string FileName = "created-objects.jsonl";

    /// <summary>读取上限：超过就不解析，避免一份被故意写大的台账吃掉内存。</summary>
    public const long MaxReadBytes = 4 * 1024 * 1024;

    /// <summary>单条记录允许的最大行长（字符）。</summary>
    public const int MaxLineLength = 4096;

    public const string ActionCreated = "created";
    public const string ActionDeleted = "deleted";

    /// <summary>一条台账记录。</summary>
    public sealed record Entry(
        DateTimeOffset Time,
        long SandboxId,
        string CloudPath,
        long Version,
        string Hash,
        long Size,
        string Tool,
        string? ContentSource,
        string Action);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>追加一条记录；失败返回 false（调用方把它当成「撤销能力不可用」报告，而不是让创建失败）。</summary>
    public static bool TryAppend(string path, Entry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(new
            {
                time = entry.Time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                sandboxId = entry.SandboxId,
                cloudPath = entry.CloudPath,
                version = entry.Version,
                hash = entry.Hash,
                size = entry.Size,
                tool = entry.Tool,
                contentSource = entry.ContentSource,
                action = entry.Action,
            }, JsonOptions);

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(line);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读出全部记录；文件不存在、读不动或超过上限时返回空列表。</summary>
    public static IReadOnlyList<Entry> ReadAll(string path)
    {
        var entries = new List<Entry>();
        try
        {
            if (!File.Exists(path))
            {
                return entries;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxReadBytes)
            {
                return entries;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line.Length > MaxLineLength)
                {
                    continue;
                }

                var entry = TryParse(line);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (Exception)
        {
            // 读失败按「没有台账」处理：删除工具会因此拒绝删除，方向是安全的。
            return Array.Empty<Entry>();
        }

        return entries;
    }

    /// <summary>找某条路径最后一条动作记录（created / deleted）；没有则 null。</summary>
    public static Entry? FindLatestForPath(string path, long sandboxId, string cloudPath)
        => FindLatestForPath(ReadAll(path), sandboxId, cloudPath);

    /// <summary>在一批记录里找某条路径最后一条动作记录（created / deleted）；没有则 null。</summary>
    public static Entry? FindLatestForPath(IReadOnlyList<Entry> entries, long sandboxId, string cloudPath)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.SandboxId == sandboxId
                && string.Equals(entry.CloudPath, cloudPath, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>找仅大小写不同的记录（用于把「大小写别名」和「台账里没有」区分开）。</summary>
    public static Entry? FindCaseInsensitivePath(IReadOnlyList<Entry> entries, long sandboxId, string cloudPath)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.SandboxId == sandboxId
                && string.Equals(entry.CloudPath, cloudPath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static Entry? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryReadString(root, "time", out var timeText)
                || !DateTimeOffset.TryParse(timeText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time)
                || !TryReadLong(root, "sandboxId", out var sandboxId)
                || sandboxId <= 0
                || !TryReadString(root, "cloudPath", out var cloudPath)
                || cloudPath.Length == 0
                || !TryReadLong(root, "version", out var version)
                || version <= 0
                || !TryReadString(root, "hash", out var hash)
                || hash.Length == 0
                || hash.Length > 128
                || !TryReadLong(root, "size", out var size)
                || size < 0
                || !TryReadString(root, "tool", out var tool)
                || !TryReadString(root, "action", out var action))
            {
                return null;
            }

            TryReadString(root, "contentSource", out var contentSource);
            return new Entry(time, sandboxId, cloudPath, version, hash, size, tool, contentSource, action);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var member))
        {
            return false;
        }

        if (member.ValueKind == JsonValueKind.String)
        {
            value = member.GetString() ?? string.Empty;
            return true;
        }

        if (member.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return false;
    }

    private static bool TryReadLong(JsonElement root, string name, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var member))
        {
            return false;
        }

        return member.ValueKind == JsonValueKind.Number
            ? member.TryGetInt64(out value)
            : member.ValueKind == JsonValueKind.String
                && long.TryParse(member.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
