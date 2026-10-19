# NutstoreMCP v1.1.0 — Windows 发布包

把坚果云桌面客户端的查询与文件管理能力做成 MCP 插件。插件运行在客户端进程内，使用客户端已登录的会话和它自己的实现，**不需要 WebDAV，也不需要任何第三方凭据**。

这个包是给最终用户直接安装的：解压后运行一个脚本即可，不需要 .NET SDK、不需要编译源码。

---

## 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 / 11（x64） |
| 坚果云客户端 | **7.2.12.0**（本包按该版本的混淆符号构建；其他版本会拒绝安装，除非加 `-Force`） |
| PowerShell | 系统自带的 Windows PowerShell 5.1 即可 |
| 权限 | 安装需要一次管理员授权（会弹 UAC）：要写 `C:\Program Files\Nutstore` 并修改客户端配置 |

---

## 快速开始

1. 解压整个 zip（不要只把脚本拖出来，脚本依赖同目录的 `plugin\`、`bridge\`、`tools\`）。
2. 关闭正在运行的坚果云（脚本也会自动停，但先关更稳）。
3. 在这个目录打开 PowerShell，运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-NutstoreMCP.ps1
   ```

   （也可以右键 `Install-NutstoreMCP.ps1` →「使用 PowerShell 运行」）

4. 点掉 UAC 的「是」，等脚本跑完。成功时会打印：

   ```
   ✅ MCP 端点已就绪：http://127.0.0.1:17321/mcp（tools/list = 39 项）
   ```

5. 把这个地址加到你的 MCP 客户端配置里：

   ```yaml
   transport: streamable-http
   url: http://127.0.0.1:17321/mcp
   ```

装好后 MCP 端点只监听 `127.0.0.1`，不对外网开放。

---

## 安装脚本做了什么

1. 停掉坚果云（以及守护进程 `nutstore_watchdog`）；
2. 把 `NutstoreMcp.Bridge.dll`、`NutstoreMcp.Plugin.Contract.dll` 放进客户端目录；
3. 备份并**原子地**修改 `NutstoreClient.exe.config`（加 `appDomainManager` 两项，失败时线上配置不动）；
4. 把插件解到 `%LOCALAPPDATA%\NutstoreMcp\plugin-1.1.0\`，写 `bridge.ini` 指过去；
5. 重新启动客户端，等 MCP 端点就绪并核对工具数量。

出任何问题都可以用同目录的 `Uninstall-NutstoreMCP.ps1` 还原（会从备份恢复客户端配置）。

---

## 这版能做什么（39 个 MCP 工具）

- **只读查询**：同步文件夹、云端目录/对象详情/历史版本/分享状态、联系人、账号与流量、路径 ACL、事件游标与单页事件、文件内容内联读取（≤256 KiB，可分段）
- **文件写入**（默认只预演）：
  - 新建文件：空文件 / 内联 ≤256 KiB / 本地文件 ≤16 MiB（大文件走客户端分块上传）；可在**本次新建的空目录**或**已有目录**里创建（已有目录走云端+本地两端预检与写后三重复查）
  - 移动/重命名、单层目录创建
  - **台账限定的修改**（C4）：只改自己创建且未被改动过的文件，旧版本进历史、网页端可回退
  - **台账限定的删除**：只删自己创建且版本/哈希完全匹配的文件
- **分享**：公开分享链接（E1）；默认预演、必须确认；支持有效期/密码/禁下载；**URL 默认不返回**（访问凭证），台账只存 SHA-256；免费账号的公开分享会被服务端拒绝
- **管理**：云端新建同步文件夹、绑定本地空目录、读取/修改白名单客户端设置
- **诊断**：插件状态/热重载、客户端类型搜索/反射、禁止清单、WPF 控件树探针

完整的参数、返回与门控见仓库里的 [MCP-API](https://github.com/MUMU-DADA/NutstoreMCP/blob/master/docs/MCP-API.md)。

---

## 安全模型（默认拒绝）

- 所有写工具**默认只预演**，必须同时满足：令牌文件 `%LOCALAPPDATA%\NutstoreMcp\l3-write.enabled`（内容 `yes`）+ `confirm=true` + `dryRun=false`；系统级设置另需第二把锁。
- 创建/修改/删除的可操作范围由本地创建台账 `%LOCALAPPDATA%\NutstoreMcp\created-objects.jsonl` 限定；删除和修改要求当前版本/大小/哈希与台账完全一致。
- 不覆盖已有对象、不删除用户已有对象；解绑、迁移同步根、ACL 写、邮件分享、文件锁等危险入口不开放，禁止清单是代码级闸门。
- 不导出登录会话、分享 URL（默认）、分享密码等凭据。

---

## 升级

直接解压新版本的发布包并运行 `Install-NutstoreMCP.ps1`：

- 插件目录按版本号隔离（`plugin-<版本>`），旧版本目录可以手动删；
- 客户端配置补丁是幂等的；
- 如果新版本更新了客户端契约（例如 v1.0.0 → v1.1.0），必须重新跑安装脚本（脚本会替换桥 DLL），单纯覆盖插件目录不够。

---

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-NutstoreMCP.ps1
# 想连本版本的插件目录一起删：
powershell -ExecutionPolicy Bypass -File .\Uninstall-NutstoreMCP.ps1 -Purge
```

卸载会停客户端、从备份还原 `NutstoreClient.exe.config`、移除桥 DLL 与 `bridge.ini`，然后重新启动客户端。日志、台账与插件目录默认保留。

---

## 故障排查

| 现象 | 处理 |
|---|---|
| 安装脚本提示「找不到坚果云客户端」 | 先安装坚果云桌面客户端并至少启动一次 |
| 装完 MCP 连不上 | 看 `%LOCALAPPDATA%\NutstoreMcp\logs\bridge.log`；插件日志在 `%LOCALAPPDATA%\NutstoreMcp\plugin-1.1.0\logs\plugin.log`（`nutstore_plugin_status` 会给出准确 logPath）。重启一次客户端 |
| 提示客户端版本不匹配 | 本包只支持 7.2.12.0；升级客户端后需要新的发布包 |
| 客户端启动不了 | 运行 `Uninstall-NutstoreMCP.ps1` 还原配置（会从启动前的备份恢复） |
| 工具里返回 `available:false` | UI 钩子没加载：确认已重启客户端、`bridge.ini` 存在且 `home` 指向的目录存在 |

---

## 包内容

```
Install-NutstoreMCP.ps1      安装脚本（自提权）
Uninstall-NutstoreMCP.ps1    卸载脚本
README.md                    本文件
VERSION                      版本号
plugin/                      插件与 UI 钩子（含依赖 DLL）
bridge/                      桥与契约 DLL（安装时复制进客户端目录）
tools/                       客户端目录发现与配置补丁逻辑
```

许可证：[MIT](https://github.com/MUMU-DADA/NutstoreMCP/blob/master/LICENSE)。仅供个人数据与授权账号上的自动化使用。
