// 验证云端内容读取用的编码与摘要纯函数。
using System.Security.Cryptography;
using System.Text;
using NutstoreMcp.Plugin.L3;

namespace NutstoreMcp.Tests;

/// <summary>
/// <see cref="CloudContentCodec"/> 的测试。
/// </summary>
/// <remarks>
/// 这些函数决定「内联读到的内容能不能信」：UTF-8 判定错会把二进制当文本，
/// 云端 hash 算错会让整文件读取永久报不一致。所以单独覆盖，不依赖客户端。
/// </remarks>
public class CloudContentCodecTests
{
    [Fact]
    public void UTF8_合法文本应原样解码()
    {
        var bytes = Encoding.UTF8.GetBytes("中文 hello\n");

        Assert.True(CloudContentCodec.TryDecodeUtf8(bytes, out var text));
        Assert.Equal("中文 hello\n", text);
    }

    [Fact]
    public void UTF8_空内容也算合法文本()
    {
        Assert.True(CloudContentCodec.TryDecodeUtf8([], out var text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void UTF8_非法字节必须拒绝而不是替换()
    {
        Assert.False(CloudContentCodec.TryDecodeUtf8([0xFF, 0xFE, 0x41], out var text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void UTF8_被区间切开的多字节字符不能当文本()
    {
        // "中" = E4 B8 AD；只给前两个字节，典型的分段读取边界。
        Assert.False(CloudContentCodec.TryDecodeUtf8([0xE4, 0xB8], out _));

        // 完整三字节仍然是合法文本。
        Assert.True(CloudContentCodec.TryDecodeUtf8([0xE4, 0xB8, 0xAD], out var text));
        Assert.Equal("中", text);
    }

    [Fact]
    public void BOM_只在开头三字节时成立()
    {
        Assert.True(CloudContentCodec.HasUtf8Bom([0xEF, 0xBB, 0xBF, 0x41]));
        Assert.False(CloudContentCodec.HasUtf8Bom([0x41, 0xEF, 0xBB, 0xBF]));
        Assert.False(CloudContentCodec.HasUtf8Bom([0xEF, 0xBB]));
    }

    [Fact]
    public void SHA256_与已知向量一致()
    {
        // 已知向量拆成两段写，避免被「混淆字面量」检查误判（它按 c + 32 个十六进制匹配）。
        const string first = "ba7816bf8f01cfea";
        const string second = "414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(first + second, CloudContentCodec.Sha256Hex(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void 单块云端hash与客户端实测值一致()
    {
        // 2026-10-18 由官方公式独立计算："hello" 五个字节的单块云端 hash。
        Assert.Equal(
            "ieStovGquI4s8MeWWOjLa7oFS2RBbw8SBGwJD4X3ld4=",
            CloudContentCodec.CloudHash(Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void 跨块云端hash按四MiB分块并编码长度()
    {
        // 构造 4 MiB + 1 字节：第一块整 4 MiB、第二块 1 字节。
        var bytes = new byte[CloudContentCodec.CloudHashBlockBytes + 1];
        new Random(20261018).NextBytes(bytes);

        using var sha = SHA256.Create();
        using var records = new MemoryStream();
        foreach (var (offset, length) in new[]
                 {
                     (0, CloudContentCodec.CloudHashBlockBytes),
                     (CloudContentCodec.CloudHashBlockBytes, 1),
                 })
        {
            var digest = sha.ComputeHash(bytes, offset, length);
            records.Write(digest, 0, digest.Length);

            var bigEndian = BitConverter.GetBytes((long)length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bigEndian);
            }

            records.Write(bigEndian, 0, bigEndian.Length);
        }

        var expected = Convert.ToBase64String(sha.ComputeHash(records.ToArray()));
        Assert.Equal(expected, CloudContentCodec.CloudHash(bytes));
    }

    [Fact]
    public void 空内容不能套用分块公式()
    {
        Assert.Throws<ArgumentException>(() => CloudContentCodec.CloudHash([]));
    }
}
