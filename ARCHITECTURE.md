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

## 10. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 客户端升级改 AutomationId / 控件树 | L3-B 失效 | 能力探测 + 优雅降级；失败时报错而非静默 |
| 私有 API 随版本变更 | L3-A 失效 | 同上；把接口访问集中在单一模块 |
| 同步根凭据被 AI 读取 | 严重 | 白名单 + 只读默认 |
| 抓包触及账号凭据 | 严重 | 抓包产物不落盘共享、用完即删 |
| WebDAV 配额耗尽 | 功能不可用 | 限流器 + 禁止递归扫描 |
