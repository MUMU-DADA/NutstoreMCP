# 架构

## 概览

NutstoreMCP 是**客户端进程内**的 MCP 插件：不使用 WebDAV 应用密码，也不单独起服务进程，而是加载进坚果云桌面客户端，用客户端已登录的会话和它自己的实现完成查询、下载、创建、移动和设置修改。

```
MCP 客户端（DSH 等）
      │  Streamable HTTP  127.0.0.1:17321/mcp
      ▼
┌─ 坚果云客户端进程 ─────────────────────────────────────────┐
│  插件子 AppDomain：NutstoreMcp.Plugin                      │
│    · MCP 服务器与 37 个工具                                │
│    · 全部写入门控、预检与写后复查                          │
│            │  IUiHook 契约（只传普通值与 JSON）             │
│            ▼                                               │
│  默认 AppDomain：NutstoreMcp.UiHook                        │
│    · 反射调用客户端类型/方法（含混淆符号）                  │
│    · 客户端 DAO、云端操作层、WPF 控件树                     │
└────────────────────────────────────────────────────────────┘
```

`NutstoreMcp.Bridge` 由客户端的 `appDomainManagerAssembly` 加载，负责创建插件子域并完成默认域钩子的挂载/热重载。

## 组件

| 项目 | 运行位置 | 职责 |
|---|---|---|
| `src/NutstoreMcp.Plugin` | 插件子域 | MCP 工具、参数校验、写入门控、预检/复查、JSON 结果 |
| `src/NutstoreMcp.Plugin.Contract` | 两域共享 | `IPluginHost` / `IUiHook` 接口，只传基元、数组与 JSON 字符串 |
| `src/NutstoreMcp.UiHook` | 客户端默认域 | 反射绑定客户端实现：DAO、云端操作、设置、WPF |
| `src/NutstoreMcp.Bridge` | 客户端默认域 | 创建/卸载插件子域，转发 HTTP 入口 |
| `src/NutstoreMcp.Host` | 开发宿主 | 脱离客户端时加载插件的宿主（调试用） |
| `src/NutstoreMcp.ClientBindings` | 编译期共享 | 混淆符号、版本基线、绑定检查数据 |
| `tests/NutstoreMcp.Tests` | 测试进程 | L3 纯逻辑测试（安全守卫、DTO、事件解析等） |
| `recon/UiHookProbe` | 独立 net48 进程 | WPF 控件树与安全守卫探针 |

## 关键设计

**只用客户端自己的实现。** 云端读写、设置、事件都走客户端内部方法，不依赖 WebDAV/第三方 API；因此不需要用户申请任何额外凭据，也天然跟随客户端行为。

**默认域 + 插件子域。** 客户端对象只能在默认域访问；插件子域承载 MCP 与业务逻辑。跨域只经 `IUiHook` 传递简单值，避免序列化客户端类型。

**热重载边界。** 插件改动能被桥热重载（`deploy/build.ps1` 后 1.5 秒内生效，MCP 连接断开需重连）；默认域钩子无法卸载，改 `UiHook` 或契约必须走 `deploy/restart-client.ps1`（契约变更会触发一次安装/UAC）。

**先用桥验证，再固化接口。** `nutstore_bridge` 让新客户端能力先在真实客户端上验证语义，再固化为有类型、有守卫的专用工具，多个钩子变更合并为一次重启。

**只读工具与写工具分离。** 只读工具不需要解锁令牌；写工具必须解锁、`confirm=true`、`dryRun=false`，并做真实状态预检和写后复查。

## 安全模型

- **写入门控（三重锁）**：`%LOCALAPPDATA%\NutstoreMcp\l3-write.enabled`（内容 `yes`）+ `confirm=true` + `dryRun=false`；系统级设置另需 `l3-write-system.enabled`。环境变量 `NUTSTORE_L3_WRITE` / `NUTSTORE_L3_WRITE_SYSTEM` 可替代。
- **本地文件白名单**：按本地路径创建云端文件默认关闭，需 `read-roots.txt` 列出可信目录；白名单只挡文件自身的符号链接/目录联接，路径中间的联接仍是理论缺口。
- **创建文件的两条路径**：
  - **新建目录**（`directoryName`）：只允许在**本次调用新建、且建后只读确认为空**的目录里创建。新目录没有既有对象，新文件不可能与任何对象构成大小写别名；这避免触发客户端冲突改名（改名分支可能移动或删除既有对象）。
  - **已有目录**（`directoryPath`）：结构保证不成立，改用三层约束 —— ①两端预检（云端父目录全量列举 + 本地绑定文件夹枚举，任一大小写等价即拒；父路径逐字符核对）；②提交元数据前钩子再只读确认目标仍不存在；③写后三重复查（父目录只多出目标且其余成员版本/哈希不变、目标大小/hash/版本、整文件回读 SHA-256）。父目录成员上限 100；预检与发布之间的并发窗口按复核结论接受，由写后复查发现并如实报告。
- **不覆盖、有限删除**：移动/建目录/建文件/下载均拒绝已存在的目标。删除只有一条通道 —— 台账限定的 `nutstore_l3_delete_created_object`：只删 MCP 自己创建、且当前版本/大小/云端 hash 与创建台账完全一致的文件；目录、用户对象、被修改过的对象一律拒绝。解绑、迁移同步根、ACL 写、分享写、文件锁仍不开放。
- **禁止清单**：删除沙箱、注销同步根等入口在代码级禁止（`ForbiddenClientMethods`），插件与桥共用；`nutstore_l3_forbidden_methods` 可列出。删除对象的混淆入口仍在清单里（通用桥/反射工具拿不到它），只有专用删除工具经独立路径调用 —— 清单里标记为 `dedicated-only`，不是被移除。
- **桥的边界**：只允许客户端程序集；拒绝反射/委托/危险方法/参数；BUFFER 总量 ≤ 8 MiB；`VALUE` 只能构造白名单结构体（如 `CreateFileParam`）。
- **隐私**：不导出会话、分享链接、分享密码、被邀请者/组名单；不读取 `nutstore.db` 的 token/密码字段；错误只保留异常类型/错误码/HTTP 状态。

## 上传与哈希

客户端以 **393216 字节**为界选择上传方式，两条路径的云端 hash 公式相同：

1. 按 **4 MiB** 分块，`块记录 = SHA256(块) || BE64(块长度)`（40 字节）。
2. `文件摘要 = SHA256(全部块记录依次拼接)`，`文件记录 = 文件摘要 || BE64(文件总长度)`。
3. 小文件（≤393216）走全量上传 `c6e98b…`，返回内容对象标识填入 `CreateFileParam.fullObjectNameBase64`。
4. 大文件逐块上传 `c4a3b6ee…`，`name` 用 **URL-safe Base64(块记录)**；再发布块列表 `ca34e3c9…`，`name` 用 URL-safe Base64(文件记录)，body 为各块 URL-safe Base64(块记录) + `\r\n`；元数据 `blockListNameBase64` 用**普通 Base64(文件记录)**。
5. 元数据统一用 `cf02e04a…(CreateFileParam)` 发布；`hashBase64` 用普通 Base64(文件摘要)，`compressible=false`。

细节与实测记录见 [归档：上传创建语义](docs/archive/2026-10-18-上传创建语义.md)；工具侧限制见 [MCP-API](docs/MCP-API.md#nutstore_l3_create_file)。

## 事件读取

`nutstore_l3_remote_events_page` 不走会写 `EventDb`、改未读数、发 `UpdateRecentTabEvent` 的客户端入口，而是直接调用 `MakeGetEventsRequest` 并解析单页 gzip/NDJSON（传输与解压各 1 MiB 上限）。游标由服务端返回，工具不保存状态。TLS LibCurl 回退可能把 `ForceDisableLibCurl` 写为 `true`，因此该工具按「可能修改本地传输配置」标注。

## 兼容性与验证

- 当前验证客户端 **7.2.12.0**；所有混淆符号集中在 `src/NutstoreMcp.ClientBindings/ClientSymbols.cs`，基线为 `baseline-7.2.12-events-transport.json`（84 项 / 3 个程序集）。
- 客户端升级后：`.\deploy\check-client-bindings.ps1` 只读比对 PE/IL 指纹，差异必须人工复核；运行时用 `nutstore_l3_verify_call_sites` 再查方法是否还在。
- `.\verify.ps1`：6 个项目构建、343 项主测试、17 项 WPF 探针、11 项配置补丁、绑定登记检查。
- 部署：`deploy/build.ps1`（插件热重载）/ `deploy/restart-client.ps1`（钩子或契约变更）；首次安装见 [deploy/README.md](deploy/README.md)。

## 目录结构

```
src/                   插件、钩子、桥、契约、绑定登记
tests/                 L3 安全逻辑测试
recon/                 一次性调研/探针（不进解决方案）
deploy/                构建、安装、重启、配置补丁与自测
docs/MCP-API.md        MCP 工具参考
docs/开发计划.md       当前状态、验证基线与后续工作
docs/archive/          历史文档（只读）
```

早期 L1 WebDAV / L2 本地只读独立服务器已于 2026-10-18 退役，设计背景见 [归档架构调研](docs/archive/2026-10-18-ARCHITECTURE-调研版.md)。
