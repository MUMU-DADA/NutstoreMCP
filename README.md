# NutstoreMCP

把坚果云桌面客户端的查询与管理能力做成 MCP 插件。插件运行在客户端进程内，使用客户端已登录的会话和它自己的实现，**不需要申请 WebDAV 或任何第三方凭据**。

## 能力

| 类别 | 内容 |
|---|---|
| 查询 | 同步文件夹、云端目录、对象详情、历史版本、分享状态、联系人、账号与流量、路径 ACL、事件游标/单页事件、文件内容（只读，单次 ≤256 KiB，可分段） |
| 文件 | 下载到新本地路径、同沙箱文件移动/重命名、单层目录创建、新建文件（空文件 / 内联 ≤256 KiB / 本地文件 ≤16 MiB，大文件走客户端分块上传；可在本次新建的空目录或已有目录里创建，已有目录走两端预检 + 写后三重复查）、**台账限定的修改**（C4：只改 MCP 自己创建且未被改动过的文件，执行需预演版本作二次确认，旧版本进历史可回退）、**台账限定的删除**（只删 MCP 自己创建且版本/哈希与创建台账完全一致的文件） |
| 分享 | 公开分享链接（E1）：默认预演，必须 confirm；支持有效期/密码/禁下载；目标已有分享时拒绝；**URL 默认不返回**（访问凭证），台账只存 SHA-256；撤销需在网页端；公开分享可能受账号等级限制 |
| 管理 | 云端新建同步文件夹、绑定本地空目录、读取/修改客户端设置（白名单） |
| 诊断 | 插件状态/热重载、客户端类型搜索/反射、通用反射桥、WPF 控件树探针、禁止清单与绑定检查 |

共 **39** 个工具，参数、返回与门控的完整说明见 [MCP-API](docs/MCP-API.md)。

## 安装（推荐：发布包）

发布包不需要 .NET SDK、不需要编译源码，解压后运行一个脚本即可：

1. 到 [Releases](https://github.com/MUMU-DADA/NutstoreMCP/releases) 下载最新的 `NutstoreMCP-v*-win-x64.zip`；
2. 解压整个目录，运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-NutstoreMCP.ps1
   ```

   脚本会自动申请管理员权限（需要一次 UAC）：停客户端 → 装桥 → 原子修改客户端配置 → 部署插件 → 重启客户端 → 等待 MCP 端点就绪；
3. 把 `transport: streamable-http`、`url: http://127.0.0.1:17321/mcp` 加到 MCP 客户端配置。

卸载：同目录 `Uninstall-NutstoreMCP.ps1`（默认保留日志与台账）。发布包只支持坚果云客户端 **7.2.12.0**；从旧版本升级需重新运行安装脚本（客户端契约有更新）。

## 从源码运行

```powershell
.\verify.ps1                     # 构建 + 主测试 + WPF 探针 + 配置补丁 + 绑定检查
.\deploy\build.ps1               # 构建并热重载插件（改插件代码后）
.\deploy\restart-client.ps1      # 重启客户端（改默认域钩子或契约后）
```

首次安装、卸载与故障恢复见 [deploy/README.md](deploy/README.md)。

## 接入 MCP

```yaml
transport: streamable-http
url: http://127.0.0.1:17321/mcp
```

端点只监听本机。JSON-RPC 调用示例和结果约定见 [MCP-API](docs/MCP-API.md)。

## 安全边界

- 写入默认只预演，必须同时满足 `l3-write.enabled`（内容 `yes`）+ `confirm=true` + `dryRun=false`；系统级设置额外需要 `l3-write-system.enabled`。
- 创建文件默认只允许在**本次新建且确认为空**的目录里进行，从结构上避免大小写别名触发客户端改名；在**已存在目录**里创建需同时通过云端父目录与本地绑定文件夹两端预检、提交前复查与写后三重复查；不覆盖、不删除、不回滚。
- 删除只有一条通道：`nutstore_l3_delete_created_object` 只删 **MCP 自己创建、且当前版本/大小/云端 hash 与创建台账完全一致** 的文件；目录、用户已有对象、被修改过的对象一律拒绝。禁止清单没有放松，通用反射桥仍然拒绝删除入口。删除会同步到本地绑定目录（撤销自己写入结果的预期行为）。
- 修改只有一条通道：`nutstore_l3_modify_file` 只改**台账内、且自创建以来未被改动过**的文件；执行必须带预演看到的 `expectedVersion`；旧版本进历史，网页端可回退。
- 分享只有一条通道：`nutstore_l3_publish_share` 默认预演、必须 confirm；**分享 URL 是访问凭证，默认不返回**，只有显式 `returnUrl=true` 才返回；台账只存 URL 的 SHA-256；客户端不能撤销，需在网页端操作；免费账号的公开分享会被服务端拒绝（不会产生分享）。
- 解绑、迁移同步根、ACL 写、分享写、文件锁等其余危险入口不开放；禁止清单是代码级闸门。
- 不导出分享链接/密码、会话凭据；不读取本地数据库里的 token 或密码字段。
- 本地文件来源默认关闭，需在 `%LOCALAPPDATA%\NutstoreMcp\read-roots.txt` 中列可信目录。

## 项目结构

```
src/      插件（AppDomain）、默认域钩子、桥、契约、客户端绑定登记
tests/    L3 安全逻辑测试
recon/    一次性调研/探针（不进解决方案）
deploy/   构建、安装、重启、配置补丁与自测
docs/     MCP-API、开发计划、归档文档
```

## 开发与验证

```powershell
.\verify.ps1
```

`verify.ps1` 会做逐项目构建、主测试（xUnit v3）、net48 WPF/安全探针、配置补丁自测和客户端绑定检查。在线验证需要真实客户端，按 [开发计划](docs/开发计划.md) 中的测试约定操作。

客户端升级后先跑 `.\deploy\check-client-bindings.ps1` 并复核差异；混淆符号与版本基线集中在 [客户端绑定登记](src/NutstoreMcp.ClientBindings/README.md)。

## 文档

- [MCP-API](docs/MCP-API.md) — 39 个工具的完整参考
- [ARCHITECTURE](ARCHITECTURE.md) — 当前架构、安全模型与上传协议
- [开发计划](docs/开发计划.md) — 状态、验证基线、未开放项与后续
- [跨端兼容性评估](docs/跨端兼容性评估.md) — mac/Linux/Android/iOS/Web 客户端技术栈与移植难度
- [归档文档](docs/archive/README.md) — 过期文档与历史结论

早期 L1 WebDAV / L2 本地只读独立服务器已于 2026-10-18 退役，代码和测试已删除，背景见归档。

[MIT](LICENSE)
