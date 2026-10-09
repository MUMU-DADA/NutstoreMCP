# 部署：把桥装进坚果云客户端

## 形态

```
C:\Program Files\Nutstore\bin-7.2.12\
    NutstoreClient.exe.config          ← 打补丁（加两行 appDomainManager 配置）
    NutstoreMcp.Bridge.dll             ← 新增，9.5 KB，零依赖
    NutstoreMcp.Plugin.Contract.dll    ← 新增，5.5 KB，跨 AppDomain 的唯一契约
    NutstoreMcp.UiHook.dll             ← 默认域钩子，执行客户端实现

%LOCALAPPDATA%\NutstoreMcp\
    bridge.ini                         ← 桥读它，指向插件目录（用户可写，改它不用提权）
    plugin-<时间戳>\                  ← 每次构建的插件版本目录
    logs\bridge.log                    ← 桥的日志
    logs\plugin.log                    ← 插件的日志（刻意放共享位置，不跟着版本目录被清掉）
```

插件主体在 `%LOCALAPPDATA%` 下，改代码 → `.\deploy\build.ps1` 即热重载，
不用重启客户端或提权。修改默认域钩子或契约后，先跑 `.\verify.ps1`，
再用 `.\deploy\restart-client.ps1` 合并一次停止、安装与启动。

## 安装

```powershell
# 1. 构建并同步插件（不需要管理员）
.\deploy\build.ps1

# 2. 先预演，看会改什么（不需要管理员，不弹 UAC）
.\deploy\install.ps1 -WhatIf

# 3. 真正安装 —— 会**自动弹 UAC 提权**，在普通 PowerShell 里跑就行
.\deploy\install.ps1
```

第 3 步会自动通过 UAC 重启自己（新开一个管理员窗口继续跑，输出在那个窗口里）。
提权窗口跑完不会立刻消失，会等你按 Enter —— 免得还没看清结果窗口就关了。

然后**重启坚果云客户端**。

## 验证

重启后跑这两条，确认桥和插件都起来了：

```powershell
Get-Content "$env:LOCALAPPDATA\NutstoreMcp\logs\bridge.log" -Tail 20
Get-Content "$env:LOCALAPPDATA\NutstoreMcp\logs\plugin.log" -Tail 20
```

健康时应该看到：

```
# bridge.log
桥已挂载，进程=NutstoreClient.exe，CLR=4.0.30319.42000
插件已启动 v1.0.0.0，端口 17321，日志 …\plugin.log

# plugin.log
插件启动 v1.0.0.0，home=…，宿主=NutstoreClient.exe
MCP 端点已监听 http://127.0.0.1:17321/mcp
插件就绪
```

再直接确认端点活着：

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:17321/mcp -Method Post `
  -ContentType 'application/json' `
  -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' |
  Select-Object -ExpandProperty result | Select-Object -ExpandProperty tools |
  Select-Object name
```

## 接进 DSH

```yaml
- insert:
    - id: mcp-nutstore-plugin
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: nutstore-plugin
        transport: streamable-http
        url: http://127.0.0.1:17321/mcp
```

## 临时停用（不用卸载）

编辑 `%LOCALAPPDATA%\NutstoreMcp\bridge.ini`：

```ini
enabled=false
```

重启客户端即可 —— 桥什么都不做，客户端行为与未安装完全一致。
改回来同样只需重启。

## 卸载还原

```powershell
.\deploy\install.ps1 -Uninstall
```

同样会自动提权。它会清理部署 DLL 并**从备份还原客户端配置**。备份是安装时自动建的
（`NutstoreClient.exe.config.nutstore-mcp-backup`），只备份一次，不会被后续安装覆盖。

## 出问题怎么办

**客户端起不来** —— 这是最需要避免的情况，所以桥里所有代码都包了异常、
配置找不到就什么都不做。万一还是出问题：

1. 跑 `.\deploy\install.ps1 -Uninstall`（会自动弹 UAC）
2. 或者手工把 `NutstoreClient.exe.config.nutstore-mcp-backup` 覆盖回 `NutstoreClient.exe.config`
   （这一步是纯文件复制，如果连 UAC 都来不及走，可以在 PE 里或用能提权的资源管理器完成）
3. 实在不行，手工编辑 `NutstoreClient.exe.config`，删掉这两行：
   `<appDomainManagerAssembly value="NutstoreMcp.Bridge" />` 和
   `<appDomainManagerType value="NutstoreMcp.Bridge.BridgeDomainManager" />`。
   **只要这两行没了，桥绝对不会被加载** —— 这是最后的保底手段。

**客户端能起来但插件没加载** —— 看 `bridge.log`：

| 日志里看到 | 含义 |
|---|---|
| 什么都没有 | `appDomainManager` 没生效 —— 检查配置里的两行是否写进去了 |
| `未找到可用配置` | `bridge.ini` 不存在或 `home` 指向的目录不存在 —— 重跑 `.\deploy\build.ps1` |
| `配置里 enabled=false` | 被停用了，改回 `true` 并重启 |
| `加载插件失败` | 看后面附的异常；多半是插件没构建或缺少 `.dll.config` |
| `插件启动失败` | 看 `plugin.log`，具体原因在插件自己的日志里 |

## 已知限制

- **客户端升级会换目录和私有实现**。先运行 `.\deploy\check-client-bindings.ps1`，
  完整复核符号、签名、枚举与实现差异，更新版本登记和新基线后再构建、安装。
  部署脚本自动定位最新目录，并在写文件或停止客户端前拒绝不匹配的绑定；卸载不受限制。
- 端口 17321 可在 `plugin.json` 里改（放在插件目录下）。
- 桥只绑 `127.0.0.1`，外部访问不到 —— 这个端点能读坚果云的本地状态。

## 为什么桥这么小

桥在 `Main` 之前被 CLR 加载。**那一刻任何依赖解析失败都会让客户端起不来**，
所以它刻意零依赖（只用 mscorlib + System），配置用最简单的 `key=value` 手写解析。

它也刻意只做一件事：读配置 → 建独立子 AppDomain → 加载插件。
所有会变的东西（工具、传输、界面注入）都在插件里，不在桥里。
桥↔插件之间只有一个 `IPluginHost` 接口，**加方法才需要更新桥**。
