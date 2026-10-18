// 在默认域内联读取云端文件内容：只进内存，不写本地文件。
using NutstoreMcp.ClientBindings;
using NutstoreMcp.Plugin.L3;
using System.Reflection;

namespace NutstoreMcp.UiHook;

public sealed partial class UiHook
{
    /// <summary>单次内联读取的原始字节上限（256 KiB）。</summary>
    /// <remarks>
    /// 与受限创建的上限一致：两个客户端阈值（393216 字节全量上传、4 MiB 分块 hash）
    /// 都在它之上，所以整文件读取时单块 hash 公式可直接复用。
    /// 更大的文件由调用方按 offset/length 分页读取，或走会写本地磁盘的下载工具。
    /// </remarks>
    private const int CapabilityMaxReadBytes = CloudContentCodec.MaxInlineReadBytes;

    /// <summary>客户端 GetRange 类型的全名（区间读取用）。</summary>
    private const string CapabilityGetRangeTypeName = "NutstoreClient.Connection.GetRange";

    /// <summary>
    /// 读取云端文件内容并直接返回；不写本地文件、不改云端。
    /// </summary>
    /// <remarks>
    /// 只读：先按真实对象详情确认目标是普通文件、取版本，再发一次 GET 读进内存。
    /// <paramref name="offset"/> 是起始字节；<paramref name="length"/> 为 0 表示读到
    /// 上限或文件末尾，否则最多读 length 字节，两者都受 <see cref="CapabilityMaxReadBytes"/> 限制。
    ///
    /// <para>
    /// 整文件读取（offset=0 且读到的末尾就是文件末尾）用对象详情的云端 hash 校验，
    /// 不一致时 <c>ok=false</c>；分段读取只返回本段的 SHA-256，不冒充整文件校验。
    /// </para>
    /// <para>
    /// 返回内容二选一：严格 UTF-8 解码成功时给 <c>text</c>，否则给 <c>contentBase64</c>，
    /// 不会同时返回两份，避免大响应。部分读取的边界可能切开多字节字符，此时会退成 base64。
    /// </para>
    /// </remarks>
    public string ReadCloudFileInline(
        long sandboxId, long magic, string localRoot, string cloudPath, long offset, long length)
    {
        try
        {
            var validation = CapabilityCheckArguments(sandboxId, magic, localRoot, cloudPath, allowRoot: false);
            if (validation is not null) return Json.Obj(("ok", false), ("reason", validation));

            if (offset < 0) return Json.Obj(("ok", false), ("reason", "offset 不能为负数"));
            if (length < 0) return Json.Obj(("ok", false), ("reason", "length 不能为负数"));
            if (length > CapabilityMaxReadBytes)
                return Json.Obj(("ok", false),
                    ("reason", "单次读取不能超过 " + CapabilityMaxReadBytes + " 字节，请用 offset/length 分段"));

            var api = FindClientType(CloudOpsTypeName);
            if (api is null) return Json.Obj(("ok", false), ("reason", "找不到云端操作层（版本可能变了）"));

            var box = BuildNSSandbox(sandboxId, magic, localRoot, out var boxError);
            if (box is null) return Json.Obj(("ok", false), ("reason", boxError));

            var path = CapabilityBuildPath(box, cloudPath);

            // ── 先读真实元数据：版本、大小、类型、hash ──
            var readDetail = CapabilityMethod(api, ClientSymbols.ReadObjectDetail, box.GetType(), path.GetType());
            var detail = readDetail.Invoke(null, new[] { box, path });
            if (detail is null) return Json.Obj(("ok", false), ("reason", "云端没有这个对象"));
            if (CapabilityDeleted(detail)) return Json.Obj(("ok", false), ("reason", "对象已被标记删除"));
            if (CapabilityIsDirectory(detail)) return Json.Obj(("ok", false), ("reason", "目标是目录，不是普通文件"));
            if (!string.Equals(CapabilityBeanPath(detail), cloudPath, StringComparison.Ordinal))
                return Json.Obj(("ok", false), ("reason", "对象详情路径与请求不一致，拒绝继续"));

            var version = CapabilityLong(CapabilityMember(detail, "version"));
            if (version <= 0) return Json.Obj(("ok", false), ("reason", "拿到的版本号无效"));
            var totalSize = CapabilityLong(CapabilityMember(detail, "size"));
            var expectedHash = CapabilityMember(detail, "hash") as string;

            if (offset > totalSize)
                return Json.Obj(("ok", false), ("reason", "offset 超出文件大小"), ("totalSize", totalSize));

            var remaining = totalSize - offset;
            var wanted = length == 0 ? Math.Min(remaining, (long)CapabilityMaxReadBytes) : length;
            // 请求越过末尾时只读到文件末尾；由 truncated/full 字段说明实际范围。
            if (wanted > remaining) wanted = remaining;
            var end = offset + wanted;
            var full = offset == 0 && end == totalSize;
            var truncated = end < totalSize;

            // 空文件、或 offset 正好在文件末尾：没有正文字节要取。
            // 空文件在客户端是特殊分支（跳过内容上传、hash 为 32 个零字节），
            // 直接返回空结果，不去碰下载入口。
            if (wanted == 0)
            {
                return Json.Obj(
                    ("ok", true),
                    ("cloudPath", cloudPath),
                    ("version", version),
                    ("totalSize", totalSize),
                    ("offset", offset),
                    ("length", 0),
                    ("end", offset),
                    ("full", full),
                    ("truncated", false),
                    ("requestedBytes", 0),
                    ("acceptedDeltaStream", false),
                    ("integrity", full ? "empty" : "empty-range"),
                    ("reason", full ? null : "offset 已在文件末尾"),
                    ("sha256", CloudContentCodec.Sha256Hex(Array.Empty<byte>())),
                    ("cloudHash", (string?)null),
                    ("cloudHashMatches", (bool?)null),
                    ("encoding", "utf-8"),
                    ("bom", false),
                    ("text", string.Empty),
                    ("contentBase64", (string?)null));
            }

            // ── 下载到内存 ──
            // 整文件不需要 GetRange（与已验证的下载路径完全一致）；分段才构造区间。
            // 但方法签名里始终有 GetRange 参数，所以类型必须先解析。
            var rangeType = FindClientType(CapabilityGetRangeTypeName)
                ?? throw new CapabilityFailureException("找不到 GetRange 类型");
            object? range = null;
            if (!full)
            {
                // GetRange 要求 start < end；构造失败就当参数错误处理。
                try
                {
                    range = Activator.CreateInstance(rangeType, new object?[] { offset, end });
                }
                catch (Exception ex)
                {
                    return Json.Obj(("ok", false),
                        ("reason", "区间参数被客户端拒绝：" + CapabilityError(ex)),
                        ("totalSize", totalSize), ("offset", offset), ("end", end));
                }
            }

            var downloadMethod = api.GetMethod(
                ClientSymbols.DownloadContent,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[]
                {
                    path.GetType(), typeof(long), typeof(long), typeof(bool).MakeByRefType(),
                    rangeType, typeof(bool),
                },
                modifiers: null);

            if (downloadMethod is null)
                return Json.Obj(("ok", false), ("reason", "找不到下载方法（混淆名可能变了）"));

            var invocationArgs = new object?[] { path, 0L, version, false, range, false };
            Stream? stream = null;
            byte[]? bytes = null;
            string? readError = null;
            var acceptedDelta = false;

            try
            {
                stream = downloadMethod.Invoke(null, invocationArgs) as Stream
                    ?? throw new CapabilityFailureException("下载返回的不是流");
                acceptedDelta = invocationArgs[3] as bool? ?? false;

                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    // 服务端不按区间返回时，宁可失败也不把错误范围当成功。
                    if (total > wanted)
                        throw new CapabilityFailureException("返回字节数超过请求区间");
                    buffer.Write(chunk, 0, read);
                }

                bytes = buffer.ToArray();
            }
            catch (Exception ex)
            {
                readError = CapabilityError(ex);
            }
            finally
            {
                try { stream?.Dispose(); } catch (Exception) { }
            }

            var sizeNote = "请求 " + wanted + " 字节，实际读 " + (bytes?.LongLength ?? 0) + " 字节";
            if (readError is not null)
                return Json.Obj(("ok", false), ("reason", "读取失败：" + readError),
                    ("totalSize", totalSize), ("offset", offset), ("end", end), ("requestedBytes", wanted),
                    ("acceptedDeltaStream", acceptedDelta));

            if (bytes is null || bytes.LongLength != wanted)
                return Json.Obj(("ok", false), ("reason", "返回长度与请求不一致（" + sizeNote + "）"),
                    ("totalSize", totalSize), ("offset", offset), ("end", end), ("requestedBytes", wanted),
                    ("actualBytes", bytes?.LongLength ?? 0), ("acceptedDeltaStream", acceptedDelta));

            // ── 校验与摘要 ──
            var shaHex = CloudContentCodec.Sha256Hex(bytes);
            string? cloudHash = null;
            bool? hashMatches = null;
            var integrity = "unverified";
            string? reason = null;

            if (full)
            {
                if (totalSize == 0)
                {
                    // 空文件在客户端是特殊分支：hash 为 32 个零字节，不走分块公式。
                    integrity = "empty";
                }
                else if (string.IsNullOrEmpty(expectedHash))
                {
                    integrity = "unverified";
                    reason = "对象详情没有 hash，无法校验整文件内容";
                }
                else
                {
                    // 整文件读取上限 256 KiB，远小于客户端 4 MiB 分块，单块公式成立。
                    cloudHash = CloudContentCodec.CloudHash(bytes);
                    hashMatches = string.Equals(cloudHash, expectedHash, StringComparison.Ordinal);
                    integrity = hashMatches.Value ? "verified" : "hash-mismatch";
                    if (!hashMatches.Value) reason = "内容与云端对象 hash 不一致，返回值不可信";
                }
            }
            else
            {
                reason = "分段读取，未做整文件 hash 校验";
            }

            // ── 文本判定：只有严格 UTF-8 才当文本，否则给 base64 ──
            var hasBom = CloudContentCodec.HasUtf8Bom(bytes);
            var isText = CloudContentCodec.TryDecodeUtf8(bytes, out var text);
            if (isText && hasBom && text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);

            var ok = integrity != "hash-mismatch";
            return Json.Obj(
                ("ok", ok),
                ("cloudPath", cloudPath),
                ("version", version),
                ("totalSize", totalSize),
                ("offset", offset),
                ("length", bytes.LongLength),
                ("end", end),
                ("full", full),
                ("truncated", truncated),
                ("requestedBytes", wanted),
                ("acceptedDeltaStream", acceptedDelta),
                ("integrity", integrity),
                ("reason", reason),
                ("sha256", shaHex),
                ("cloudHash", cloudHash),
                ("cloudHashMatches", hashMatches),
                ("encoding", isText ? "utf-8" : "base64"),
                ("bom", hasBom),
                ("text", isText ? text : null),
                ("contentBase64", isText ? null : Convert.ToBase64String(bytes)));
        }
        catch (Exception ex)
        {
            return Json.Obj(("ok", false), ("reason", "读取检查失败：" + CapabilityError(ex)),
                ("chain", Json.StringArray(Chain(ex))));
        }
    }
}
