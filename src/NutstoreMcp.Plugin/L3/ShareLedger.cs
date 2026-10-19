// 分享台账：只记录 MCP 自己创建的分享链接，供事后核对与撤销。
//
// 红线：分享 URL 本身是访问凭证，**绝不写进台账**。台账只存 URL 的 SHA-256，
// 便于核对「这个链接是不是我们创建的那条」，而不让台账本身变成新的泄露点。
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NutstoreMcp.Plugin.L3;

/// <summary>分享台账（jsonl，只追加）。</summary>
public static class ShareLedger
{
    public const string FileName = "shares.jsonl";
    public const long MaxReadBytes = 4 * 1024 * 1024;
    public const int MaxLineLength = 8192;

    public const string ActionPublished = "published";

    public sealed record Entry(
        DateTimeOffset Time,
        long SandboxId,
        string CloudPath,
        long PublishVersion,
        long ExpireMillsSinceEpoch,
        bool PasswordSet,
        bool DownloadDisabled,
        bool EnableUpload,
        string UrlSha256,
        string Action);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
                publishVersion = entry.PublishVersion,
                expireMillsSinceEpoch = entry.ExpireMillsSinceEpoch,
                passwordSet = entry.PasswordSet,
                downloadDisabled = entry.DownloadDisabled,
                enableUpload = entry.EnableUpload,
                urlSha256 = entry.UrlSha256,
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
            return Array.Empty<Entry>();
        }

        return entries;
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
                || !TryReadLong(root, "sandboxId", out var sandboxId) || sandboxId <= 0
                || !TryReadString(root, "cloudPath", out var cloudPath) || cloudPath.Length == 0
                || !TryReadLong(root, "publishVersion", out var publishVersion)
                || !TryReadLong(root, "expireMillsSinceEpoch", out var expire)
                || !TryReadString(root, "urlSha256", out var urlSha) || urlSha.Length == 0
                || !TryReadString(root, "action", out var action))
            {
                return null;
            }

            var passwordSet = TryReadBool(root, "passwordSet");
            var downloadDisabled = TryReadBool(root, "downloadDisabled");
            var enableUpload = TryReadBool(root, "enableUpload");
            return new Entry(time, sandboxId, cloudPath, publishVersion, expire,
                passwordSet, downloadDisabled, enableUpload, urlSha, action);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var member)
            && member.ValueKind == JsonValueKind.String
            && (value = member.GetString() ?? string.Empty) is not null;
    }

    private static bool TryReadLong(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var member)
            && (member.ValueKind == JsonValueKind.Number
                ? member.TryGetInt64(out value)
                : member.ValueKind == JsonValueKind.String
                    && long.TryParse(member.GetString(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out value));
    }

    private static bool TryReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var member)
            && (member.ValueKind == JsonValueKind.True
                || (member.ValueKind == JsonValueKind.String
                    && bool.TryParse(member.GetString(), out var flag) && flag));
}
