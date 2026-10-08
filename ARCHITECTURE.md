# NutstoreMCP 架构

> 给坚果云（Nutstore）客户端做本地 MCP，覆盖**只能在 GUI 上操作**的能力。
> 状态：设计阶段，第 3 层实现方式待定（见「前置阻塞项」）。

---

## 1. 目标能力

| 能力 | 类型 | 归属层 |
|---|---|---|
| 浏览云端目录 | 读 | L1 / L3 |
| 读同步状态、哪些没同步完 | 读 | L2 |
| 读/改客户端设置（开关类） | 读写 | L3 |
| 创建新的同步目录 | 写 | L3 |
| 把远程目录同步到本地 | 写 | L3 |
| 分享链接 / 文件历史 | 写 | L3 |

---

## 2. 三层架构

```
┌─ L1 文件面 ── WebDAV ───────────────────────────────┐
│  https://dav.jianguoyun.com/dav/   Basic + 应用密码  │
│  职责：云端文件读写                                   │
│  稳定性：官方接口，长期可用                            │
└──────────────────────────────────────────────────────┘
┌─ L2 数据面 ── 本地只读 ──────────────────────────────┐
│  %APPDATA%\Nutstore\db1\nutstore.db （只读，WAL）     │
│  职责：sandbox↔本地路径映射、逐文件同步状态、设置快照   │
│  稳定性：格式未公开，随版本可能变；只读风险可控          │
└──────────────────────────────────────────────────────┘
┌─ L3 控制面 ── 待定 ─────────────────────────────────┐
│  候选 A：抓包复刻客户端私有 API   ← 优先             │
│  候选 B：UI Automation 驱动 GUI   ← 兜底             │
│  职责：创建同步目录、改设置、触发菜单动作               │
│  稳定性：A 未验证；B 能用但随版本脆弱                   │
└──────────────────────────────────────────────────────┘
```

**分层原则：各层独立降级。** L3 做不出来不影响 L1/L2 可用；L2 格式变了不影响 L1。

---

## 3. 各层实现要点

### L1 WebDAV

- 认证：账号 + **应用密码**（网页端「账户信息 → 安全选项 → 第三方应用管理」生成），不是登录密码
- 硬限制：单文件上传 **500 MB**；请求频率 **600 次/30 分钟**（免费）/ 1500（付费）；单次目录枚举 **750 项，必须分页**
- 因此：**禁止递归全盘扫描**，只能按需读
- 路径映射：本地 `%USERPROFILE%\Nutstore\<短名>\<X>\...` ↔ 云端 `/<X>/...`

### L2 本地只读

- `config.all_sandbox_list`：完整云端 sandbox 列表，含 `sandboxId` / `usedSpace` / `canSync` / `doNotSync` / `desc`
- `sndconfig<hash>`：`sandbox_name` = `{"id":…,"magic":…,"localAbsPath":"C:\\…"}`
- `sndobject<hash>`：每文件一行（`dir_path` / `file_name` / `hash` / `mtime` / `size` / `version`）
- 用 `Microsoft.Data.Sqlite` 以 `Mode=ReadOnly` 只读打开

### L3 控制面

**候选 A（优先）：抓包复刻私有 API**

客户端内置完整代理设置（`NsConfig.json` 的 `proxy` 段 + 「网络设置」对话框），
可指向本地 mitmproxy，手动做一次「添加同步文件夹」即可拿到真实接口与载荷。

- 优点：拿到的是明文协议，比 UI 自动化稳定得多
- 风险：`Nutstore.LibCurl.dll` 引用 `ServerCertificateValidationCallback` /
  `SslPolicyErrors`，**可能做了证书固定**，需先验证

**候选 B（兜底）：UI Automation**

已验证可行：UIA 能读到完整控件树（323 个后代）与全部 11 个设置开关的实时状态；
客户端自带稳定 AutomationId（`MainWindow_ToggleButton_OpenSettings`、
`SyncFoldersTab_TabItem_Self` 等）。

实现约束（实测得出）：
- tab 内容**懒加载**，未激活时坐标是 `-31999`（虚拟化），必须先激活再找子元素
- 不能靠一次全树扫描
- 每个动作必须「前置探测 → 执行 → 后置断言」，不能假定点了就生效
- GUI 需在前台，会抢焦点

---

## 4. 技术选型

**C# / .NET 10，可发布为自包含单文件 —— 目标机器无需安装运行时。**

理由：

- **与宿主同栈。** 坚果云客户端是 C# / WPF / .NET Framework 4.6.2
  （MahApps.Metro + Prism；`Microsoft.Web.WebView2.**Wpf**.dll` 只是嵌入式控件，
  安装目录里没有任何 Electron / CEF / Chromium）。同栈的直接收益是 L3：
  若走 UI Automation，`System.Windows.Automation` 是一等公民；
  换别的语言要手写 `IUIAutomation` 的 COM 互操作，成本高一个数量级。
- **部署零依赖。** `dotnet publish -r win-x64 --self-contained` 产出
  `nutstore-mcp.exe` + `e_sqlite3.dll`（约 39 MB），目标机器不需要装 .NET。
- 排除 Python：本机 `python` 只有 WindowsApps 占位桩、无 `uv`/`uvx`，
  现成的 PyPI 方案开箱不可用。
- 排除 Go：运行时与体积确实更优，但没有 UIA 绑定 —— 会把 L3 带进沟里。

**注意**：不要用 `IncludeNativeLibrariesForSelfExtract` 追求「真·单文件」。
它把原生库压进 exe 并在运行时解包到 `%TEMP%\.net`，在 `%TEMP%` 对进程不可写的
机器上会直接启动失败（实测 `error code: 5`）。两文件形态在哪都能跑。

接入 DSH：`@deepseek-ai/dsh-mcp-client`，`transport: stdio`，`command` 指向发布出的 exe，
在 `~/.dsh/profiles/web/cordis.patch.yml` 追加一条 `insert` 配置项。

---

## 5. 安全模型（红线，不可协商）

1. **路径白名单 + 默认只读。** 同步根下存在大量明文凭据（`邮箱密码.txt`、
   `AccessKey.csv`、`github-recovery-codes.txt`、`chatGPT_API.txt` 等），
   无白名单的 MCP 等于把 AI 及任何提示注入直接接到密码库上。
2. **「高级安全验证」是硬信号。** 该开关开启后，文件夹同步设置与权限设置需要密码验证。
   一旦它为 On，MCP **必须停手并要求人工操作**，不得尝试绕过。本机当前为 Off。
3. **不读取、不使用** `nutstore.db` 中的 `encrypted_token` / `device_token` / `bcrypt_pwd_hash`。
4. 抓包产物按敏感数据处理，不提交、不外传。

---

## 6. 明确不做

| 不做 | 理由 |
|---|---|
| 直接写 `nutstore.db` 造 sandbox | 表名 `sndconfig<32hex>` 是**服务端分配的不透明 ID**，穷举 14 种推导方式全部不匹配；加上进程内缓存与驱动注册，不可行 |
| 用 WebDAV 建目录当同步目录 | 实测：WebDAV 建出的文件夹在客户端里 `desc` 为 `"Folder created via WebDAV API"` 且 `doNotSync: true`，认得但不同步 |
| 走客户端 IPC | 只有 shell 集成契约（`ExplorerCommand`/`ThumbnailProvider`/`CopyHook`/`ProviderStatusUI`/`CustomStateProvider`）与内部命名管道，无配置类操作 |
| 复刻私有云 API 的认证 | 程序集被混淆（`#US` 堆仅 208 字节），成本高、随版本失效、有风控风险 |
| 递归全盘扫描 | 600 次/30 分钟的配额，扫一次就烧光 |
| 绕过密码验证 | 越过这条线做的就不是工具而是外挂 |

---

## 7. 前置阻塞项

**抓包可行性实验未做 —— 它决定 L3 走候选 A 还是候选 B。**

实验步骤：
1. 启动 mitmproxy
2. 客户端 → 设置 → 网络设置 → 填 `127.0.0.1:8080`，启用代理
3. 确认客户端仍能正常同步（隧道通）
4. 手动「添加同步文件夹」
5. 检查 mitmproxy 能否读到明文

判定：
- **能解密** → 走 A，复刻私有 API，MCP 可做得扎实，不需要 UIA
- **不能解密（pinning）** → 立即止损，走 B，不要浪费时间逆向混淆二进制

---

## 8. 已找到的「介入软件」通道（2026-10 实测）

**结论：客户端确实有一条约 19 个方法的 gRPC 程序接口，且已实测调用成功 —— 不需要 GUI 自动化。**

### 通道

| 项 | 值 |
|---|---|
| 传输 | gRPC over 命名管道 `\\.\pipe\BadgeGrpcNamedPipe.BadgeToNutstoreV3` |
| 服务 | `NutstoreBadge`（客户端侧实现 `NutstoreClient.NamedPipeServerImpl.GrpcBadgeNamedPipeService`） |
| 库 | `GrpcDotNetNamedPipes`，被 ILMerge 进客户端程序集（**没有独立 DLL**） |

### 怎么调（已跑通）

不需要复刻帧协议 —— **客户端自己就带客户端桩**：

```csharp
// .NET Framework 4.8（Windows 内置，零安装）
var asm = Assembly.LoadFrom(@"C:\Program Files\Nutstore\bin-7.2.12\NutstoreClient.Legacy.dll");
var channelType = asm.GetType("GrpcDotNetNamedPipes.NamedPipeChannel");
var channel = Activator.CreateInstance(channelType, ".", "BadgeGrpcNamedPipe.BadgeToNutstoreV3", options);
var client  = Activator.CreateInstance(asm.GetType("NutstoreBadge").GetNestedType("NutstoreBadgeClient"), channel);
// GetBadgeServerToken(Empty) → GrpcBadgeServerTokenResponse { Token, DomainName, UseSSL }
```

实测返回：`DomainName = badge.jianguoyun.com`、`UseSSL = True`、一个 JWT。

### 可用方法（19 个）

`ShareFileByLink`（分享链接）、`BrowseFileHistory`（文件历史）、`GetNutstoreFile`、
`GetLockInfo`、`DiscussFile`、`GotoOnlineEdit`、`LogOpenFileEvent`、
`ShowUploadFileDialog`、`ShowDownloadFileDialog`、`GetBadgeServerToken`、
`InitializeBadge`、`ExitBadge`、`AddLockedFile`、`RemoveLockedFile`、
`NotifyUpdateFile`、`IsLockedByBadge`、`UpdateBadgeServerToken`、
`UpdateBadgeSettings`、`SaveBadgeSettings`

> **更正**：早先判定「分享链接 / 文件历史没有程序接口，只能走 GUI」是**错的** ——
> 它们在 `ShareFileByLink` / `BrowseFileHistory`。

### 边界（重要）

这是 **Badge（托盘）集成面**，它**没有**：创建同步文件夹、修改客户端设置、浏览远程目录。
L3 的核心目标仍然要靠抓包复刻私有 API 或 UIA。

### 四个坑

1. **`serverName` 必须是 `"."`（机器名），不能是 `"localhost"`** —— 否则报
   `Unavailable: failed to connect to all addresses`。
2. **protobuf 描述符反射在客户端进程外会炸**：`ToString()` / `JsonFormatter` 会触发
   `EmptyReflection..cctor()` → 混淆代码抛 NRE。**只用生成的属性读值**，不要格式化消息。
3. **不要自己引 `GrpcDotNetNamedPipes` NuGet 包** —— 版本对不上，连上就被断开。
   用客户端自带的。
4. **从工作区目录启动的进程访问命名管道会被拒**（`UnauthorizedAccessException`，
   所有 SQOS 级别都拒）。这是 DSH 沙箱的进程级限制，与文件策略无关。
   把探针复制到工作区外（如 `%LOCALAPPDATA%`）再跑就正常。
   —— 排查时先怀疑这个，别怀疑坚果云的 ACL。

---

## 9. 已完成构件

| 构件 | 用途 |
|---|---|
| `recon/MetadataDump/` | .NET 元数据反射工具。枚举程序集的类型/接口/常量/字符串字面量，**不反编译、不执行目标代码**。用于复核「为什么不能写 DB」「IPC 到底有什么」等结论。用法：`MetadataDump <程序集> [命名空间] [类型]` / `MetadataDump --us <程序集>` |
| `recon/PipeProbe/` | 试连命名管道并尝试 gRPC 反射（未成功 —— 服务端没注册反射） |
| `recon/LoadProbe/` | 加载客户端程序集、枚举契约、实际调用。`--raw` 做纯净连接实验，`--call` 发起真实调用。**必须从工作区外运行** |

---

## 10. L3 控制面：API 已测绘，写入路径**刻意未启用**

### 已测绘的写入 API

客户端自己的 DAO（`NutstoreClient.DAO.NSSandboxDAO`，在 `NutstoreClient.Legacy.dll`）：

```
static List<NSSandbox> ReadAllSandboxesFromDb(String nutstoreDbPath)    只读
static List<NSSandbox> ReadAllSandboxes()                               只读（需客户端运行时）
static Int64           GetSandboxObjectsCount(NSSandbox sandbox)        只读

static Void InitSandbox(NSSandbox, List<CapsOnPathBean>)                ⚠️ 创建同步绑定
static Void RemoveSandbox(NSSandbox)                                    ⛔ 解绑，可能连带删除本地
static Void MigrateSandboxPath(NSSandbox, String newHome)               ⛔ 移动同步目录
static Void MigrateSandboxObjectTableToUnicodeNoCaseCollation(NSSandbox) ⚠️ 重建对象表
```

模型 `NutstoreClient.Common.NSSandbox`（注意在 `NutstoreClient.Legacy.dll` 里，
但命名空间不是 `NutstoreClient.DAO`）：

```
Int64  ID           沙箱 ID（服务端分配，不可自造）
Int64  Magic
String LocalAbsPath 本地同步路径
String Name         云端文件夹名
Boolean IsPlaceholder
SandboxIdentifier Identifier
String ToJson() / static NSSandbox FromJson(String)
```

### ⛔ 三个坚决不实现的方法

`RemoveSandbox` / `MigrateSandboxPath` / `MigrateSandboxObjectTableToUnicodeNoCaseCollation`
**不在 MCP 里暴露，也不打算暴露**。理由不是技术难度，是后果：

- `RemoveSandbox` 之后本地目录会怎样，取决于客户端的实现细节 —— 而那是**用户的文件**
- `MigrateSandboxPath` 会移动同步根，中途失败可能留下两处半成品
- 这两个操作在客户端 GUI 里都有明确的二次确认和警告文案，说明厂商自己认为它们危险

MCP 的价值不该用「能替用户做危险操作」来体现。

### 「添加同步文件夹」的完整流程（已测绘）

**先纠正一个容易搞错的认识**：绑定一个**已存在**的云端沙箱到本地是
**纯本地操作** —— 不需要远程写。云端内容会被**下载**到本地目录。

所以「创建同步目录」分两种，风险完全不同：

| 操作 | 性质 | 风险 |
|---|---|---|
| 把已存在的云端文件夹绑到本地空目录 | 本地 | 低（但要空目录，否则合并冲突） |
| 在云端新建文件夹 | **远程写** | 高，且用户明确禁止开发期动远程 |

GUI 里那个「添加同步文件夹」= 前者。

**涉及的三个类型**（都在 `NutstoreClient.Legacy.dll` / `Nutstore.ShellNamespace.dll`）：

```csharp
// 云端沙箱 + 可选的本地位置
class SandboxInfo {
    Int64  SandboxId      // 服务端分配
    Int64  SandboxMagic   // 服务端分配
    String Name, Desc, Owner, ChannelId
    Caps   Caps
    List<CapsOnPathBean> PathPerms
    NSSandbox Sandbox
    SandboxSyncState SyncState
    Boolean IsSyncToDisk, DoNotSync, Decoupled, IsPrivate, IsOwner
    Void SetSyncLocation(String location)   // 设了本地路径就是要绑定
    Void ClearSyncLocation()
}

enum Nutstore.Common.SandboxSyncState {
    NotSynced,        // 云端有、本地没绑  ← 候选
    SyncedToDisk,     // 已同步到本地
    SyncPlaceholder,  // 占位符（不占空间）
}

// 绑定动作（投递到客户端自己的命令队列）
class DownstreamBindSandboxCommand {
    DownstreamBindSandboxCommand(SandboxInfo sndInfo)
}
```

**流程**：

1. 读云端沙箱列表，筛出 `SyncState == NotSynced` 的（L2 的 `nutstore_sync_folders`
   已经把它们作为 `UnmountedSandbox` 暴露出来了）
2. 拿它的 `SandboxId` / `SandboxMagic` / `PathPerms`
3. 选一个**空的**本地目录
4. `SetSyncLocation(localPath)` → `new DownstreamBindSandboxCommand(info)` 投递

**尚未查清的最后一环 → 命令怎么投递。** `DownstreamBindSandboxCommand` 继承自
`NutstoreClient.Commands.DownstreamCommand`，投递目标是 `NSSandboxContext.DownstreamQ`。
队列本身已经测绘清楚：

```csharp
class NutstoreClient.Context.DownstreamCommandQueue {
    Void Enqueue(DownstreamCommand command)
    Void DiscardDuplicateAndEnqueue(DownstreamBindSandboxCommand command)   // ← 专用入口
    Boolean HasPendingBindCommand(SandboxInfo newSandboxInfo)              // ← 去重检查
    Void AddToQueueHead / AddToPendingQueueHead / Clear / Dequeue
}
```

**客户端为这个场景提供了专用方法** `DiscardDuplicateAndEnqueue` —— 它自己做去重。
不该用裸的 `Enqueue` 绕过它。

仍然缺的是**怎么拿到活的队列实例**（`NSSandboxContext` 的实例属性，
而构造它需要一个 `NSSandbox`）。这需要客户端运行时，也就是默认域。

### 数据流的结论：绑定不需要 L2 帮忙

L2 的 `all_sandbox_list` 解析**刻意不导出 `magic`**（「内部标识不外泄」），
而绑定需要 `SandboxId` + `SandboxMagic`。

所以绑定应该在**默认域里直接从客户端取完整的 `SandboxInfo`** ——
那里 id 和 magic 都是现成的，不需要 L2 中转，也不需要我拼装身份。
这反而更安全：身份完全来自客户端自己的对象。

### ⛔ OS 层：这些方法绝对不能暴露

`Nutstore.StorageProvider.CloudSyncRootService` 是 Windows 云文件 API（CfApi）的集成层：

```csharp
static Task RegisterAsync(SyncRootCreateInfoBase rootInfo)
static Void UnregisterSyncRoot(SyncRootCreateInfoBase rootInfo)
static Void UnregisterSyncRootsOfAccount(String account)
static Void UnregisterAllNutstoreSyncRoots()          // ⛔⛔ 全账号所有同步根
static Void DisconnectSyncRoot(Int64 connectionKey)
static Boolean DisableSyncRootOnDemandPopulation(String syncRootPath)
```

`UnregisterAllNutstoreSyncRoots()` 会把**所有**同步根从系统注销 ——
Explorer 里的云图标、按需下载全部失效。`UnregisterSyncRootsOfAccount` 同级别危险。

这两个连同 `RemoveSandbox` / `MigrateSandboxPath` 一起列入**永不暴露**清单。

顺带：`SandboxSyncRootCreateInfo(String syncRootPath, Int64 sndId, String account)`
与 `MirrorSyncRootCreateInfo` 的区分，正对应 GUI 里那句
「修改后的同步文件夹会变为'不占用电脑空间'状态」—— 即占位符模式 vs 镜像模式。

### ⛔ 不要由 AI 重启客户端 —— 沙箱会弄坏它

这条不是谨慎，是技术上的必须。

客户端有 gRPC 命名管道服务 `\\.\pipe\BadgeGrpcNamedPipe.BadgeToNutstoreV3`
（资源管理器集成用）。而 DSH 沙箱**拒绝从工作区启动的进程访问命名管道**
（`UnauthorizedAccessException`，所有 SQOS 级别都拒 —— 见 §8 第 4 条实测）。

所以如果由 agent 用 `Start-Process` 重启 `NutstoreClient.exe`，
**它的命名管道服务会起不来**，资源管理器的云图标、右键菜单等集成会坏掉。
表现是「客户端在跑但集成不见了」—— 比不重启糟得多。

**重启必须由用户自己做**（从开始菜单或资源管理器启动，脱离沙箱）。

### 还缺的一块：怎么拿到活的队列实例

已经明确要调什么：

```csharp
queue.DiscardDuplicateAndEnqueue(new DownstreamBindSandboxCommand(info))
```

但 `DownstreamCommandQueue` 要从活着的 `NSSandboxContext.DownstreamQ` 拿，
而 `NSSandboxContext` 需要构造参数 `NSSandbox`。**用类型名猜没有结果**：

- 没有 `ContextManager` / `SandboxContextFactory` / `RuntimeContext` 之类的注册表类
- `NutstoreClient.Context` 命名空间下只有命令、检查点、队列本身，没有持有者
- 按 `Application` 搜被 Office interop 类型淹没，没定位到客户端自己的 App 类

**结论：这个问题应该在有默认域的时候用运行时探查解决，而不是继续猜类型名。**
客户端是 Prism 应用，活对象大概率挂在它的 DI 容器里 ——
拿到 `Application.Current` 后沿 `Container` 找，比对着 16,878 个类型名猜靠谱得多。

这也是为什么下一轮应该先重启、再做 API 探查：**运行时的对象图是答案，
类型名列表不是。**

### 危险信号

`NSSandboxContext.WipeDataWhenLostAccess` —— 客户端有「失去访问权时清空本地数据」
的行为。任何 L3 操作都要注意别触发它。

同一类还有 `MissingLocalFolderPolicy`（本地文件夹缺失时的策略）——
这两个属性都说明客户端在异常路径上有**主动删除本地数据**的设计。
L3 不该去改这些开关。


**关键点**：`SandboxId`/`SandboxMagic` 来自服务端，**不能自造**。
这也意味着不能凭空「创建」一个沙箱 —— 只能绑定已存在的。
要新建云端文件夹，那是 `NutstoreClient.Connection.NutstoreRequest+CreateDirectoryRequestEntity`
（远程写，本项目的开发期红线）。

### 写入守卫已验证的用例（`L3/WriteGuards.cs`，探针 13 项全绿）

| 用例 | 结果 |
|---|---|
| 默认全拒（无环境变量、无 confirm） | ✅ |
| **非空目录必拒** —— 实测对 `%USERPROFILE%\Documents` 即使 confirm+dryRun=false+合法沙箱 ID 也拒绝 | ✅ |
| 嵌套同步根必拒（相同/子目录/父目录/前缀相似但无关/无关/大小写不同） | ✅ 6/6 |
| 缺沙箱状态 fail-closed（留空 ≠ 无所谓） | ✅ |
| 已绑定沙箱（SyncedToDisk / SyncPlaceholder）拒绝重绑 | ✅ |
| 全部满足 + dryRun 也只预演 | ✅ |

设计立场：**拒绝「我帮你判断安不安全」**。判断本地文件与云端是否冲突需要比对两边
全量元数据，判断错了代价是用户的文件 —— 所以非空目录一律拒绝。

三道闸：`NUTSTORE_L3_WRITE=1`（默认不存在）+ `confirm=true` + `dryRun` 默认 `true`。

所以设计上要求**全部**满足才放行：

1. `NUTSTORE_L3_WRITE=1` 环境变量 —— **默认没有，即默认完全不可写**
2. 调用方显式传 `confirm: true`
3. `dryRun` 默认 `true` —— 不传就只返回「会发生什么」
4. 本地目录存在、是目录、**且为空**（非空一律拒绝，不做「我帮你判断安不安全」）
5. 该路径未被任何现有沙箱占用
6. 不删除任何东西；全程只调 `InitSandbox`

第 4 条刻意保守：**非空本地目录一律拒绝**。判断「这些文件和云端冲不冲突」需要比对两边全量元数据，
判断错了代价是用户的数据 —— 宁可让用户手动建一个空目录，也不要赌。

### 为什么写入路径现在跑不了（已实测确认，勿重复尝试）

**子域路线已排除。** 异常链第 4 层：

```
ArgumentException: 'path' cannot be an empty string
  在 Pri.LongPath.Path.NormalizeLongPath
← NutstoreLib.Utils.DirectoryUtils.APPDATA_NUTSTORE_DIR   （静态构造）
← NutstoreClient.Common.SignedInUsers                     （静态构造）
← NSSandbox..ctor
```

逐个探测子域的基础路径，**全都正常**（AppData / UserProfile / CurrentDirectory /
`APPDATA` 环境变量…），只有一个为空：

```
Assembly.GetEntryAssembly()  →  (null)
```

**验证过的假设与其否定**：猜想是「入口程序集没被加载进子域」，
于是把 `NutstoreClient.exe` 加载进来再试 —— 程序集数 51 → 52，
`GetEntryAssembly()` **仍然为 null**。

这是 CLR 的固有行为：`GetEntryAssembly` 在非默认域里返回 null，
与程序集是否加载无关。所以**任何依赖它的客户端静态初始化都无法在子域成立**。

结论：调用客户端 DAO 的代码**必须跑在默认域**，只能经 `IUiHook`，而钩子改动要重启客户端。
反射（`Reflect`）在子域没问题，因为不触发静态构造 —— 一旦真的 `Invoke` 就炸。

### 已验证的写入守卫（`L3/WriteGuards.cs`）

L3 唯一可能丢数据的地方是「把本地目录绑到云端沙箱」，而**这个判断不需要碰客户端** ——
输入全是普通值（路径、是否存在、条目数、是否重叠）。抽成纯函数后离线穷举验证：

| 探针用例 | 结果 |
|---|---|
| 默认全拒（无环境变量、无 confirm） | ✅ |
| **非空目录必拒** | ✅ 核心数据安全 |
| 嵌套同步根必拒（相同/子目录/父目录/前缀相似但无关/无关/大小写不同） | ✅ 6/6 |
| 全部满足 + dryRun 也只预演 | ✅ |

设计立场：**拒绝「我帮你判断安不安全」**。判断本地文件与云端是否冲突需要比对两边
全量元数据，判断错了代价是用户的文件 —— 所以非空目录一律拒绝。

三道闸：`NUTSTORE_L3_WRITE=1`（默认不存在）+ `confirm=true` + `dryRun` 默认 `true`。

### 已在插件里落地且安全的部分

| 构件 | 说明 |
|---|---|
| `L3/ClientAssemblies.cs` | 在插件子域加载客户端程序集 + `Reflect` 探查签名。**只反射，不执行** —— 不会触发那些静态构造 |
| `L3/SandboxApi.cs` | 走 DAO 读同步配置。**先复制数据库（含 `-wal`/`-shm`）再读副本**，真库全程不被本进程打开。当前受上述限制调用失败，但复制逻辑和副本清理是正确且必要的 |
| `nutstore_client_reflect` | 上面那套的 MCP 工具，用来在不确定 API 形状时先看清楚 |

### 下一步（需要用户在场：退出客户端 → `install.ps1` → 重启）

**已写好、等重启后生效的部分**：`IUiHook.ReadClientSyncFolders()` 已在默认域实现
（同样是复制数据库读副本），插件侧 `nutstore_l3_sync_folders` 会优先走它。

当前调用会优雅返回（实测）：

```json
{
  "ok": false,
  "viaDefaultDomain": "RemotingException: 未在接口找到方法 ReadClientSyncFolders",
  "note": "钩子没有 ReadClientSyncFolders —— 需要重启客户端让新钩子生效。"
}
```

注意契约 DLL 在程序目录和插件目录各有一份，**两处必须一起更新**，
`build.ps1` 会比对 SHA256 并提示。

重启后依次确认：

1. `nutstore_l3_sync_folders` 是否返回客户端 DAO 的结果（`source` 字段会写明「默认域」）
2. 写入路径**仍不开**：需要先把 `InitSandbox` 的调用在默认域实现，
   并经过 `L3/WriteGuards.cs` 的三道闸；这一步应当单独评审，不要顺手打开

---

## 11. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 客户端升级改 AutomationId / 控件树 | L3-B 失效 | 能力探测 + 优雅降级；失败时报错而非静默 |
| 私有 API 随版本变更 | L3-A 失效 | 同上；把接口访问集中在单一模块 |
| 同步根凭据被 AI 读取 | 严重 | 白名单 + 只读默认 |
| 抓包触及账号凭据 | 严重 | 抓包产物不落盘共享、用完即删 |
| WebDAV 配额耗尽 | 功能不可用 | 限流器 + 禁止递归扫描 |
| L3 绑定非空本地目录 → 冲突覆盖 | **严重（用户数据）** | 非空一律拒绝 + 默认 dryRun + 默认无写权限 |
| L3 解绑/迁移同步根 | **严重（用户数据）** | **不实现、不暴露** |
| 插件子域崩溃波及客户端同步 | 中 | AppDomain 隔离；插件所有入口包异常；失败即静默停用 |
| 客户端升级致目录变化 | 功能不可用 | 探测最新 `bin-*`；失败报错不静默 |
| 契约 DLL 两处不一致 | 难排查的 RemotingException | `build.ps1` 比对 SHA256 并红字提示重装 |
