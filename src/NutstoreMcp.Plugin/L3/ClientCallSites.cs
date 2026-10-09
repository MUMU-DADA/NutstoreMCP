// 登记客户端调用目的及副作用。
using NutstoreMcp.ClientBindings;
namespace NutstoreMcp.Plugin.L3;

/// <summary>登记客户端调用点及其副作用。</summary>
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
    public const string CloudOpsType = ClientSymbols.CloudOpsType;

    public static readonly IReadOnlyList<CallSite> Registered =
    [
        new(
            CloudOpsType,
            ClientSymbols.CreateSandbox,
            "新建云端同步文件夹（CreateCloudSyncFolder）",
            Mutates: true,
            Why: "只在云端**新增**一个沙箱，不删不改任何已有内容；"
               + "名字先过 CloudFolderName 校验；"
               + "与绑定共用解锁令牌。"),

        new(
            CloudOpsType,
            ClientSymbols.ListDirectory,
            "列出云端目录（ListCloudDirectory）",
            Mutates: false,
            Why: "纯读。目标方法只发一次列表请求并把结果映射成路径字符串。"),

        // 下面两个是**目标方法内部**调用的，我们从不直接调，
        // 登记它们是为了记录「读这些方法的实现时确认过它们做什么」。
        new(
            ClientSymbols.SandboxDaoType,
            "InitSandbox",
            "把沙箱的本地存储初始化到指定目录（BindSandbox 用它）",
            Mutates: true,
            Why: "创建本地数据库表并登记沙箱。失败就抛异常，不会半途留下损坏状态；"
               + "调用前已确认目标目录为空且不与任何现有同步根重叠。"),

        new(
            CloudOpsType,
            ClientSymbols.CreateDirectory,
            "在沙箱内建子目录（CreateCloudDirectory）",
            Mutates: true,
            Why: "只新建一个云端目录，不递归、不覆盖已有对象。"
               + "解锁令牌 + confirm + dryRun 默认真；实际查父目录和目标，失败也复查。"
               + "本轮新靶子验证已有文件会返回 DuplicateName 且文件大小、哈希和版本不变。"),

        new(
            CloudOpsType,
            ClientSymbols.UploadFullContent,
            "全新文件的全量内容上传（CreateCloudFile 的第一阶段）",
            Mutates: true,
            Why: "上传请求没有目标路径，只把内容存进对象存储并返回内容对象标识 —— "
               + "它本身不创建文件、不覆盖任何已有对象；只有随后成功发布元数据才会被引用。"
               + "仅用于内容非空且不超过 256 KiB 的全量分支（客户端 393216 字节以下才走这条）。"),
        new(
            CloudOpsType,
            ClientSymbols.CreateFileMetadata,
            "把已上传内容发布成云端文件（CreateCloudFile 的第二阶段）",
            Mutates: true,
            Why: "**这是唯一真正落地文件的一步**：方法本体没有目标查重、没有 noReplace，"
               + "所以只允许在本次调用新建、且建后确认为空的目录里发布，"
               + "发布前后都按真实状态核对；目标存在或仅大小写冲突一律拒绝，"
               + "失败或结果未知只做只读复查，不重试、不回滚、不删除。"),

        new(
            CloudOpsType,
            ClientSymbols.Search,
            "在云端按关键词搜索（SearchCloud）",
            Mutates: false,
            Why: "纯读。只发一次搜索请求，把结果映射成路径字符串。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadUsage,
            "读账号容量与流量用量（ReadAccountUsage）",
            Mutates: false,
            Why: "纯读。不带参数，只取账号的容量与流量统计，不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadUserInfo,
            "读账号信息（ReadUserInfo）",
            Mutates: false,
            Why: "纯读。不带参数，只取账号的昵称与类型，不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadSandboxDetail,
            "读同步文件夹详情（ReadSyncFolderDetail）",
            Mutates: false,
            Why: "纯读。只按 SandboxIdentifier 查询该沙箱的名称、所有者与占用，不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadSandboxPermissions,
            "读沙箱权限（ReadSyncFolderDetail）",
            Mutates: false,
            Why: "纯读。只按 SandboxIdentifier 查询各路径的权限位，不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ListFileVersions,
            "读文件历史版本（ListFileVersions）",
            Mutates: false,
            Why: "纯读。只按 NutstorePath 查询该文件的版本列表，不恢复、不修改任何版本。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadPublishInfo,
            "读文件分享状态（ReadShareInfo）",
            Mutates: false,
            Why: "纯读。只按 NutstorePath 查询该对象的分享信息，不创建、不修改任何分享。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadContacts,
            "读协作联系人（ReadContacts）",
            Mutates: false,
            Why: "纯读。不带参数，只取联系人列表，不邀请、不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadObjectDetail,
            "读对象详情（ReadObjectDetail）",
            Mutates: false,
            Why: "纯读。只按 NSSandbox + NutstorePath 查询该对象的元数据，不写任何东西。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadLatestEventId,
            "读最新事件 ID（ReadLatestEventId）",
            Mutates: false,
            Why: "纯读。只按 NSSandbox 查询当前同步游标，不推进游标、不写任何东西。"),

        new(
            ClientSymbols.NutstoreRequestType,
            ClientSymbols.MakeGetEventsRequest,
            "构造 GET /cmd/getEventsV4 事件请求",
            Mutates: false,
            Why: "纯读。只创建客户端请求对象，不发请求、不写 EventDb；会话头只在默认域局部传递，绝不经桥返回。"),

        new(
            ClientSymbols.NutstoreNetManagerType,
            ClientSymbols.SendHttpRequest,
            "发送 GET /cmd/getEventsV4 事件请求",
            Mutates: true,
            Why: "远端只读，但 TLS LibCurl 错误回退可能把 NsConfig.ForceDisableLibCurl 设为 true，属于客户端配置副作用；专用调用不调用 EventDAO、不推进本地游标，也不导出响应对象或会话头。"),

        new(
            ClientSymbols.HttpWebResponseShimType,
            ClientSymbols.GetResponseStream,
            "读取事件响应正文流",
            Mutates: false,
            Why: "纯读。只读取已返回的 HTTP 正文流；钩子在解析后关闭响应，不写客户端本地状态。"),

        new(
            CloudOpsType,
            ClientSymbols.DownloadContent,
            "下载云端文件内容（DownloadCloudFile）",
            Mutates: true,
            Why: "云端只读，但**会写本地磁盘** —— 所以标为会改状态。"
               + "本地写入由钩子用 FileMode.CreateNew 完成 —— 目标已存在就拒绝，绝不覆盖。"
               + "实测 remoteVersion 必须是真实版本号（传 0 报 ObjectNotFound、传 -1 报 IllegalArgument），"
               + "所以调用前先查对象详情拿版本。"),

        new(
            CloudOpsType,
            ClientSymbols.MoveFile,
            "在云端移动 / 重命名普通文件（MoveCloudObject）",
            Mutates: true,
            Why: "会改云端路径，不改文件内容；仅同沙箱普通文件。"
               + "三重守卫，实际核对两父目录、源对象版本和目标不存在，执行后核对大小、哈希。"
               + "本轮新靶子验证已有目标返回 DuplicateName/Conflict，两对象不变；"
               + "失败和未知结果不自动重试，不删除或覆盖冲突目标。"
               + "原 ca22 方法是目录批量接口，空集合违反 Any() 前置条件，已停止调用。"),

        new(
            CloudOpsType,
            ClientSymbols.ReadPathAcl,
            "读子路径权限和继承状态（ReadPathAcl）",
            Mutates: false,
            Why: "纯读。只发 MakeGetPathAclRequest，不调用 ACL 修改方法；"
               + "显式输出权限、用户与组字段并限制数量，不序列化会话或整个对象。"),

        new(
            ClientSymbols.NutstorePathType,
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
            ClientSymbols.NutstorePathType,
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
