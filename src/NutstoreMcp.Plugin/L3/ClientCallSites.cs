namespace NutstoreMcp.Plugin.L3;

/// <summary>
/// 我们**有意调用**的客户端方法清单 —— 包括混淆过的（名字是哈希的）那些。
/// </summary>
/// <remarks>
/// 存在的理由：<see cref="ForbiddenClientMethods"/> 是**按名字**匹配的，
/// 而客户端里真正做事的类被混淆过，方法名是哈希。名字匹配对它们完全无效 ——
/// 也就是说那份禁止清单**挡不住**一个哈希调用。
///
/// 所以改用另一条防线：凡是调用混淆方法，必须在这里登记。
/// 登记不是形式 —— 每一条都要写清楚「调它会不会改动状态」和「为什么安全」。
/// 新增一个哈希调用而没在这里登记，就是一次需要被审查的改动。
/// </remarks>
internal static class ClientCallSites
{
    /// <summary>一个混淆调用点。</summary>
    /// <param name="TypeName">混淆类型全名。</param>
    /// <param name="MethodName">混淆方法名。</param>
    /// <param name="Purpose">我们用它做什么。</param>
    /// <param name="Mutates">是否会改动状态（云端、本地库或客户端配置）。</param>
    /// <param name="Why">为什么认为这样调用是安全的。</param>
    public sealed record CallSite(
        string TypeName,
        string MethodName,
        string Purpose,
        bool Mutates,
        string Why);

    /// <summary>客户端云端操作层的混淆类。多个调用点都在它上面。</summary>
    public const string CloudOpsType = "A.c3795a089d76f103f11c4fe70f38ac8ca";

    public static readonly IReadOnlyList<CallSite> Registered =
    [
        new(
            CloudOpsType,
            "ccdd0d7373903386c87fb2d3077bf8ced",
            "新建云端同步文件夹（CreateCloudSyncFolder）",
            Mutates: true,
            Why: "只在云端**新增**一个沙箱，不删不改任何已有内容；"
               + "名字先过 CloudFolderName 校验；"
               + "与绑定共用解锁令牌。"),

        new(
            CloudOpsType,
            "c05097d346f069f32352068d6ff359701",
            "列出云端目录（ListCloudDirectory）",
            Mutates: false,
            Why: "纯读。目标方法只发一次列表请求并把结果映射成路径字符串。"),

        // 下面两个是**目标方法内部**调用的，我们从不直接调，
        // 登记它们是为了记录「读这些方法的实现时确认过它们做什么」。
        new(
            "NutstoreClient.DAO.NSSandboxDAO",
            "InitSandbox",
            "把沙箱的本地存储初始化到指定目录（BindSandbox 用它）",
            Mutates: true,
            Why: "创建本地数据库表并登记沙箱。失败就抛异常，不会半途留下损坏状态；"
               + "调用前已确认目标目录为空且不与任何现有同步根重叠。"),

        new(
            CloudOpsType,
            "cfd1df8451110086567f3d56edf4697e8",
            "在沙箱内建子目录（**当前未使用**）",
            Mutates: true,
            Why: "已定位但没接出来。登记在这里是为了说明：以后要用它，"
               + "必须重新走一遍「确认它改什么」的流程，不能因为它已在清单里就直接调。"),
    ];

    /// <summary>按方法名查登记项。</summary>
    public static CallSite? Find(string methodName) =>
        Registered.FirstOrDefault(
            c => c.MethodName.Equals(methodName, StringComparison.Ordinal));

    /// <summary>会改动状态的调用点。</summary>
    public static IEnumerable<CallSite> Mutating =>
        Registered.Where(c => c.Mutates);

    /// <summary>只读的调用点。</summary>
    public static IEnumerable<CallSite> ReadOnly =>
        Registered.Where(c => !c.Mutates);
}
