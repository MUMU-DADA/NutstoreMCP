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

        new(
            CloudOpsType,
            "c7a517b89b338a47fe91fa3ec2a0823c3",
            "在云端按关键词搜索（SearchCloud）",
            Mutates: false,
            Why: "纯读。只发一次搜索请求，把结果映射成路径字符串。"),

        new(
            CloudOpsType,
            "cf22046b2931cec246e3a4f4b994e6c84",
            "读账号容量与流量用量（ReadAccountUsage）",
            Mutates: false,
            Why: "纯读。不带参数，只取账号的容量与流量统计，不写任何东西。"),

        new(
            CloudOpsType,
            "cc244cf7442842e41dd4ffb0830aeb3f3",
            "读账号信息（ReadUserInfo）",
            Mutates: false,
            Why: "纯读。不带参数，只取账号的昵称与类型，不写任何东西。"),

        new(
            CloudOpsType,
            "c083e3da23c3bbf0c707145cd9efea279",
            "读同步文件夹详情（ReadSyncFolderDetail）",
            Mutates: false,
            Why: "纯读。只按 SandboxIdentifier 查询该沙箱的名称、所有者与占用，不写任何东西。"),

        new(
            CloudOpsType,
            "c3c96725a92962d165d900554f645b220",
            "读沙箱权限（ReadSyncFolderDetail）",
            Mutates: false,
            Why: "纯读。只按 SandboxIdentifier 查询各路径的权限位，不写任何东西。"),

        new(
            CloudOpsType,
            "c2654043f830c74ecf5925237786dccea",
            "读文件历史版本（ListFileVersions）",
            Mutates: false,
            Why: "纯读。只按 NutstorePath 查询该文件的版本列表，不恢复、不修改任何版本。"),

        new(
            CloudOpsType,
            "c9a9142ba46c6a4cdb6c70a448962ca9e",
            "读文件分享状态（ReadShareInfo）",
            Mutates: false,
            Why: "纯读。只按 NutstorePath 查询该对象的分享信息，不创建、不修改任何分享。"),

        new(
            CloudOpsType,
            "c915cc8f3fd71da2191b3d1dff9bbda1f",
            "读协作联系人（ReadContacts）",
            Mutates: false,
            Why: "纯读。不带参数，只取联系人列表，不邀请、不写任何东西。"),

        new(
            CloudOpsType,
            "c55ca68332481ffab4014efe97582ac93",
            "读对象详情（ReadObjectDetail）",
            Mutates: false,
            Why: "纯读。只按 NSSandbox + NutstorePath 查询该对象的元数据，不写任何东西。"),

        new(
            CloudOpsType,
            "cbb28d79821068cd4efb5de6f9109afff",
            "读最新事件 ID（ReadLatestEventId）",
            Mutates: false,
            Why: "纯读。只按 NSSandbox 查询当前同步游标，不推进游标、不写任何东西。"),

        new(
            CloudOpsType,
            "c089b7574cb46db5e6d9bba4e0e0a6002",
            "下载云端文件内容（DownloadCloudFile）",
            Mutates: true,
            Why: "云端只读，但**会写本地磁盘** —— 所以标为会改状态。"
               + "本地写入由钩子用 FileMode.CreateNew 完成 —— 目标已存在就拒绝，绝不覆盖。"
               + "实测 remoteVersion 必须是真实版本号（传 0 报 ObjectNotFound、传 -1 报 IllegalArgument），"
               + "所以调用前先查对象详情拿版本。"),

        new(
            "NutstoreClient.Common.NutstorePath",
            "FromNutstorePath",
            "由云端路径构造 NutstorePath（DownloadCloudFile 的前置）",
            Mutates: false,
            Why: "纯读。只把云端路径和沙箱绑成一个对象，不发请求、不写任何东西。"),

        new(
            "（通用）",
            "InvokeBridge",
            "通用反射桥 —— 让插件侧在不改钩子的前提下调用客户端实现",
            Mutates: true,
            Why: "它本身不确定改不改状态，取决于调用方给的脚本 —— 所以标为会改状态。"
               + "桥内部有两道限制：只允许客户端自己的程序集（不能当通用 .NET 执行器）、"
               + "危险方法名一律拒绝。写操作的策略（解锁、dryRun、白名单）仍在插件侧。"),

        new(
            "NutstoreClient.Common.NutstorePath",
            "FromAbsolutePath",
            "由本地绝对路径构造 NutstorePath（ListFileVersions 的前置）",
            Mutates: false,
            Why: "纯读。它只把路径分解成沙箱 + 云端路径两段，不发任何请求、不写任何东西。"),
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
