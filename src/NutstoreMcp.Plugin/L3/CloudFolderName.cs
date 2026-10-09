using System.Text;

namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 云端文件夹名的校验。
/// </summary>
/// <remarks>
/// 这个名字会被写进用户的云盘。校验放在两道：插件侧先挡住并给出清楚的原因，
/// 钩子侧再兜一道（它跑在客户端进程里，不该把明显非法的参数交给客户端代码）。
/// 这里是插件侧那份，抽出来是为了能离线测 —— 内联在工具方法里测不到。
/// </remarks>
internal static class CloudFolderName
{
    /// <summary>Windows 与常见云盘都不接受的字符。</summary>
    private static readonly char[] Invalid =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>名称长度上限。客户端与 Windows 都远低于此，取一个保守值。</summary>
    public const int MaxLength = 100;

    /// <summary>校验。返回 null 表示合法，否则返回拒绝原因。</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "文件夹名为空。";
        }

        // 上面的检查之后编译器仍认为可能为 null（IsNullOrWhiteSpace 没有
        // NotNullWhen 标注），这里显式收窄一次
        var actual = name!;

        if (actual.Length > MaxLength)
        {
            return $"文件夹名长度超出上限（{actual.Length} 字符，上限 {MaxLength}）。";
        }

        if (actual == "." || actual == "..")
        {
            return "文件夹名不能是 . 或 ..";
        }

        if (actual.IndexOfAny(Invalid) >= 0)
        {
            return $"文件夹名含非法字符: {actual}";
        }

        // 控制字符：肉眼看不出来，但会让云端出现诡异的名字
        foreach (var c in actual)
        {
            if (char.IsControl(c))
            {
                return "文件夹名含控制字符。";
            }
        }

        // 首尾空白：容易在云端造成「看起来一样其实不同」的两个文件夹
        if (!string.Equals(actual, actual.Trim(), StringComparison.Ordinal))
        {
            return "文件夹名首尾不能有空白。";
        }

        return null;
    }
}
