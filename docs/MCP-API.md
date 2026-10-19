# MCP API 参考

NutstoreMCP 把坚果云桌面客户端的查询与管理能力暴露为 MCP 工具。插件运行在客户端进程的独立 AppDomain 中，使用客户端已登录的会话和它自己的实现，**不需要第三方凭据**（不需要 WebDAV 应用密码）。

- 版本：插件 1.0.0.0，对应客户端 **7.2.12.0**
- 传输：Streamable HTTP（只监听本机）
- 端点：`http://127.0.0.1:17321/mcp`
- 工具总数：**36**
- 文档基准：源码 + 在线 `tools/list` + 真实调用返回形状（2026-10-18，`plugin-20261018-114348`）

---

## 1. 接入

```yaml
transport: streamable-http
url: http://127.0.0.1:17321/mcp
```

最小 JSON-RPC 调用示例：

```jsonc
// POST http://127.0.0.1:17321/mcp
{ "jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {} }
{ "jsonrpc": "2.0", "id": 2, "method": "tools/call",
  "params": { "name": "nutstore_l3_sync_folders", "arguments": {} } }
```

工具结果放在 MCP 的 `content[0].text` 里，是一个 **JSON 字符串**；本文档列出的字段就是该 JSON 的顶层字段。业务失败不会变成 MCP 协议错误，而是返回 JSON（通常是 `{"ok":false,"reason":"…"}` 或 `{"available":false,"reason":"…"}`）。

---

## 2. 通用约定

### 2.1 结果信封

只读工具通常返回 `{ "ok": true, … }`；写工具（预演/执行）统一返回：

| 字段 | 含义 |
|---|---|
| `allowed` | 预检是否全部放行 |
| `wouldWrite` | 若真执行是否会写（`allowed && !dryRun && executed`） |
| `dryRun` | 本次是否是预演 |
| `unlockVariablePresent` / `unlocked` | 写入令牌是否在位 |
| `probe` | 预检时看到的真实状态（目标是否存在、父目录、内容长度等） |
| `refusals[]` | 拒绝理由；非空表示不会执行 |
| `notes[]` | 行为说明 |
| `attempted` / `executed` | 是否尝试调用 / 是否确认执行成功 |
| `result` | 默认域钩子返回的原始结构化结果 |
| `verification` | 写后只读复查（类型、大小、哈希、版本、目录成员等） |

写工具的原则：**失败或结果未知只做只读复查，不自动重试、不回滚、不删除**。

### 2.2 沙箱身份

云端操作都要 `sandboxId` + `magic`（沙箱内部标识）：

- `nutstore_l3_sync_folders` 可列出**已绑定**的沙箱及其 id/magic。
- 两者必须成对匹配；插件还会用客户端的真实绑定根校验，身份不明时拒绝。
- `magic` 属敏感值，仓库和文档不落盘。

### 2.3 路径与字节

- 云端路径用 `/` 分隔、以 `/` 开头；部分工具允许 `/` 表示沙箱根（见各工具）。
- 本地路径必须是绝对路径；`localFilePath` 一类工具会按路径匹配到对应同步文件夹。
- 大小/版本等字段可能以 JSON 字符串返回（客户端原样转出），文档不再逐个标注。
- 单次内联读取上限 **256 KiB**；`read_file` 用 `offset`/`length` 分段。
- 本地文件创建上限 **16 MiB**；超过客户端阈值 `393216` 字节走分块上传。

### 2.4 写入门控

所有写工具默认只预演，必须同时满足：

1. `confirm=true`
2. `dryRun=false`
3. 写入令牌存在：`%LOCALAPPDATA%\NutstoreMcp\l3-write.enabled`，内容为 `yes`
   （或环境变量 `NUTSTORE_L3_WRITE=1/true`，进程级、需重启客户端）
4. 系统级设置（文件保护锁 / 高级安全验证 / 暂停同步）额外需要
   `l3-write-system.enabled`（或 `NUTSTORE_L3_WRITE_SYSTEM`）

`nutstore_l3_create_file` 的本地文件模式还要求 `%LOCALAPPDATA%\NutstoreMcp\read-roots.txt` 白名单（一行一个绝对路径，`#` 注释）；默认关闭。

### 2.5 只读边界与隐私

- 不导出分享链接、分享密码、被邀请者/组名单、客户端会话或传输凭据。
- 不读取 `nutstore.db` 中的 `encrypted_token` / `device_token` / `bcrypt_pwd_hash`。
- 删除、解绑、迁移同步根、ACL 写、分享写、文件锁等危险入口不开放（`nutstore_l3_forbidden_methods` 可列出代码级禁止清单）。
- 写操作只允许在本次新建的测试目标上执行；已有用户对象只读。

### 2.6 常见错误

| 现象 | 含义 |
|---|---|
| `{"available":false}` | UI 钩子未加载，通常客户端没重启或桥没装好 |
| `{"ok":false,"reason":"…"}` | 参数校验、身份不匹配或客户端拒绝；照 `reason` 处理 |
| `RequestException` 类文本 | 客户端 HTTP 层错误；插件只保留类型/错误码/HTTP 状态 |
| `note` 字段 | 客户端升级导致钩子方法缺失等提示，常需重新部署/重启 |

---

## 3. 工具总览

| 分组 | 工具 | 读/写 |
|---|---|---|
| 诊断与插件 | `nutstore_plugin_status`、`nutstore_plugin_reload`、`nutstore_l3_env`、`nutstore_l3_forbidden_methods`、`nutstore_l3_verify_call_sites`、`nutstore_client_assemblies`、`nutstore_client_search_types`、`nutstore_client_reflect`、`nutstore_bridge` | 读（reload/bridge 可变） |
| 界面探针 | `nutstore_ui_windows`、`nutstore_ui_tree`、`nutstore_ui_find`、`nutstore_ui_install_toggle`、`nutstore_ui_dump` | 读（install_toggle/dump 写本地界面/文件） |
| 同步文件夹与账号 | `nutstore_l3_sync_folders`、`nutstore_l3_sync_folder_detail`、`nutstore_l3_create_sync_folder`、`nutstore_l3_create_cloud_folder`、`nutstore_l3_usage`、`nutstore_l3_user_info`、`nutstore_l3_contacts`、`nutstore_l3_search` | 读 + 2 个写 |
| 云端文件读取 | `nutstore_l3_list_cloud_dir`、`nutstore_l3_object_detail`、`nutstore_l3_file_versions`、`nutstore_l3_share_info`、`nutstore_l3_path_acl`、`nutstore_l3_read_file`、`nutstore_l3_download` | 读（download 写本地） |
| 云端文件写入 | `nutstore_l3_move`、`nutstore_l3_mkdir`、`nutstore_l3_create_file` | 写 |
| 客户端设置 | `nutstore_l3_read_settings`、`nutstore_l3_write_setting` | 读 / 写 |
| 事件 | `nutstore_l3_latest_event`、`nutstore_l3_remote_events_page` | 读 |

---

## 4. 诊断与插件

### `nutstore_plugin_status`
**用途**：插件自身运行状态。**参数**：无。  
**返回**：`plugin{version, running, port, endpoint, logPath, lastError, domain, uiHookAttached}`、`host{process, pid, startedAt, runtime, is64Bit, workingSetMB}`。

### `nutstore_plugin_reload`
**用途**：热重载插件（桥卸载并重建插件子域）。**参数**：无。  
**返回**：`requested`、`triggerFile`、`note`。重载后 MCP 连接会断开，需要客户端重连。UI 钩子在默认域，钩子改动仍需重启客户端。

### `nutstore_l3_env`
**用途**：诊断插件子域的基础路径与程序集解析。**参数**：无。  
**返回**：`domain`、`isDefaultDomain`、`probes[{name, ok, value}]`、`entryAssemblyExperiment{hypothesis, exePath, loadResult, before/after{entryAssembly, loadedCount}, hypothesisHolds}`。

### `nutstore_l3_forbidden_methods`
**用途**：列出代码级禁止调用的客户端方法及理由。**参数**：无。  
**返回**：`count`、`note`、`entries[{method, reason}]`。

### `nutstore_l3_verify_call_sites`
**用途**：验证依赖的客户端方法（含混淆哈希名）是否仍存在，升级后先跑它。**参数**：无。  
**返回**：`ok`、`checkedCount`、`missing[]`、`callSites[{TypeName, MethodName, Purpose, mutates, present, overloads, detail}]`、`notes[]`。

### `nutstore_client_assemblies`
**用途**：列出插件子域已加载的程序集（客户端程序集在默认域，这里看不到属正常）。  
**参数**：`filter`（string，可选，名称子串）。  
**返回**：`domain`、`note`、`count`、`filter`、`assemblies[{name, version}]`。

### `nutstore_client_search_types`
**用途**：按类型名/命名空间子串搜索客户端类型。  
**参数**：`pattern`（string，必填）；`limit`（integer，默认 80）。  
**返回**：`ok`、`pattern`、`scannedTypes`、`matched`、`truncated`、`types[]`、`assembliesThatFailedToLoad[]`。

### `nutstore_client_reflect`
**用途**：反射查看某个客户端类型的构造函数/方法/属性/字段签名。  
**参数**：`typeName`（string，必填，完整类型名）；`filter`（string，可选，成员名子串）；`limit`（integer，默认 60）。  
**返回**：`ok`、`type`、`assembly`、`baseType`、`isStatic`、`hasDefaultCtor`、`ctors[]`、`methods[]`、`props[]`、`fields[]`。

### `nutstore_bridge`
**用途**：通用反射桥，按脚本调用客户端实现（研究/调试入口，脚本可能改状态）。  
**参数**：`script`（string，必填）。  
**返回**：`ok`、`domain`、`error`、`errorCode`、`httpStatus`、`stepCount`、`steps[]`（每步 ok/fail）、`slots[]`（按槽位序号的返回结果）、`refUpdates{}`。

脚本每行一个操作，行号即槽位号（从 0 开始），后续用 `#N` 引用：

| 操作 | 语法 | 说明 |
|---|---|---|
| `NEW` | `NEW\|类型全名\|参数...` | 构造客户端类型实例 |
| `CALL` | `CALL\|类型全名\|方法名\|参数...` | 调静态方法 |
| `INSTANCE` | `INSTANCE\|#槽位\|方法名\|参数...` | 调实例方法 |
| `GET` | `GET\|#槽位\|成员名` | 读公开属性/字段 |
| `CHAIN` | `CHAIN\|#槽位\|成员\|成员...` | 沿属性/字段链取值 |
| `ENUM` | `ENUM\|类型全名\|枚举名` | 取枚举值 |
| `BUFFER` | `BUFFER\|base64` | 建只读流，总计 ≤ 8 MiB |
| `VALUE` | `VALUE\|结构体全名\|字段\|值\|字段\|值...` | 仅构建白名单新结构体（如 `CreateFileParam`） |

参数编码：`s:<base64 字符串>`、`l:<long>`、`i:<int>`、`b:true/false`、`#<槽位>`、`-` 表示 null，无前缀按字符串。只允许客户端程序集；危险方法、反射/委托签名会被拒绝；客户端异常不输出原始消息。

---

## 5. 界面探针

### `nutstore_ui_windows`
**用途**：列出客户端当前打开的 WPF 窗口。**参数**：无。  
**返回**：`available`、`count`、`windows[{type, typeName, title, isVisible, isActive, isLoaded, width, height, contentType}]`。

### `nutstore_ui_tree`
**用途**：导出指定窗口的控件树。  
**参数**：`windowType`（string，必填，类型名子串）；`maxDepth`（integer，默认 6，范围 1–40）。  
**返回**：未找到窗口：`available`、`found=false`、`reason`、`openWindows[]`；找到时：`available`、`found=true`、`window`、`title`、`truncated`、`nodeCount`、`nodes[]`（节点含类型/名称/文本/子节点等）。

### `nutstore_ui_find`
**用途**：在窗口里按类型名/控件名/文本查找控件。  
**参数**：`windowType`（string，必填）；`query`（string，必填）；`limit`（integer，默认 50，范围 1–200）。  
**返回**：`available`、`found`；找到时 `query`、`count`、`truncated`、`matches[]`，未找到窗口时 `reason`。

### `nutstore_ui_install_toggle`
**用途**：在客户端设置页注入 MCP 原生开关（幂等，重复调用不会出现两行）。需设置面板处于打开状态。**参数**：无。  
**返回**：`ok`、`removed`（摘掉的同名旧项数）、`hostType`、`childCount`、`stateFile`。

### `nutstore_ui_dump`
**用途**：把所有窗口的完整控件树导出成文件（本地诊断，不改客户端状态）。  
**参数**：`directory`（string，可选，导出目录；默认插件旁 `dumps/`）。  
**返回**：`ok`、`file`、`windowCount`、`extraSourceCount`、`totalNodes`。

---

## 6. 同步文件夹与账号

### `nutstore_l3_sync_folders`
**用途**：列出客户端登记的所有同步文件夹绑定。**参数**：无。  
**返回**：`ok`、`domain`、`source`、`count`、`sandboxes[{id, name, localPath, isPlaceholder, magic, localExists}]`。

### `nutstore_l3_sync_folder_detail`
**用途**：读某个同步文件夹的详情与权限。  
**参数**：`sandboxId`（integer，必填）、`magic`（integer，必填）。  
**返回**：`ok`、`domain`、`sandboxId`、`detail{name, desc, owner, isOwner, isDefault, exclusive, doNotSync, isPhotoBucket, permission, usedStorageBytes, usedStorage}`、`permCount`、`permPaths[{path, caps}]`、`detailError`、`permsError`。

### `nutstore_l3_create_sync_folder`
**用途**：把本地空目录绑定为坚果云同步文件夹。**写，默认预演。**  
**参数**：`localPath`（string，必填，已存在且为空）；`sandboxId`（integer，默认 0，必须 >0）；`confirm`（默认 `false`）；`dryRun`（默认 `true`）；`sandboxSyncState`（string，必须是 `NotSynced`）；`existingBindings`（string，分号分隔；不传会因无法确认重叠而拒绝）；`magic`（integer）；`permPaths`（string[]）；`permCaps`（integer[]）。  
**返回**：写工具信封；`probe{localPath, exists, isDirectory, isEmpty, entryCount, sandboxId, magic, permCount, sandboxSyncState, existingBindingCount}`、`executionAvailable`、`executionResult`、`executionUnavailableReason`。  
**注意**：本地目录非空、与现有同步根重叠、沙箱已绑定、身份/权限不明都会被拒。

### `nutstore_l3_create_cloud_folder`
**用途**：在云端新建一个同步文件夹，返回新沙箱身份。**写，默认预演。**  
**参数**：`name`（string，必填）；`description`（string，可选）；`confirm`；`dryRun`。  
**返回**：`allowed`、`wouldWrite`、`dryRun`、`unlockVariablePresent`、`probe{name, description}`、`refusals[]`、`notes[]`、`executed`、`result`。执行成功时 `result` 含新沙箱的 `sandboxId` / `magic` / `pathPerms`，可直接用于绑定工具。

### `nutstore_l3_usage`
**用途**：读账号容量与流量用量。**参数**：无。  
**返回**：`ok`、`domain`、`isPaidUser`、`isInTeam`、`accountExpireLeftTime`、`storage`、`rates`。

### `nutstore_l3_user_info`
**用途**：读账号信息。**参数**：无。  
**返回**：`ok`、`domain`、`nickName`、`isPaidUser`、`isInTeam`、`onlineEdit`、`accountExpireLeftTime`、`storage`。

### `nutstore_l3_contacts`
**用途**：读协作联系人列表。**参数**：无。  
**返回**：`ok`、`domain`、`inTeamCount`、`invitedCount`、`othersCount`、`inTeam[]`、`invited[]`、`others[]`（无内容时对应数组为 `null`）。

### `nutstore_l3_search`
**用途**：按关键词搜索云端文件/文件夹（付费功能，免费账号会收到服务端限制提示）。  
**参数**：`keyword`（string，必填）。  
**返回**：`ok`、`domain`、`keyword`、`groupCount`、`fileCount`、`groups[{sandboxId, resultType, fileCount, files[{path, isDir, modifyTime}]}]`。免费账号：`ok=false`、`reason`、`errorCode=DisabledForFreeUser`、`isFeatureLimit=true`。

---

## 7. 云端文件读取

### `nutstore_l3_list_cloud_dir`
**用途**：列出云端目录内容。  
**参数**：`sandboxId`、`magic`（必填）；`directory`（string，可选，以 `/` 开头，留空为根）。  
**返回**：`ok`、`domain`、`directory`、`count`、`entries[]`（路径字符串列表）。

### `nutstore_l3_object_detail`
**用途**：读某个本地文件对应的云端对象详情。  
**参数**：`localFilePath`（string，必填，绝对路径）。  
**返回**：`ok`、`domain`、`localFilePath`、`id`、`cloudPath`、`objectType`、`version`、`sizeBytes`、`size`、`hash`、`deleted`、`compressible`、`srcMachineName`、`tblId`。

### `nutstore_l3_file_versions`
**用途**：列出某个本地文件在云端的历史版本。  
**参数**：`localFilePath`（string，必填）。  
**返回**：`ok`、`domain`、`localFilePath`、`cloudPath`、`count`、`versions[{version, operation, opType, editor, editorAccount, hasNickname, timeRaw, time, fileSize, altPath}]`。

### `nutstore_l3_share_info`
**用途**：读分享状态；**不导出分享链接、密码值、被邀请者/组名单**。  
**参数**：`localFilePath`（string，必填）。  
**返回**：`ok`、`shared`；已分享时含版本、公开级别、是否有密码、过期时间、是否禁止下载、是否允许上传等字段，未分享时给 `reason` / `errorCode`。  
**注意**：客户端入口只能建立分享、不能取消，取消需走网页端。

### `nutstore_l3_path_acl`
**用途**：查询某路径的权限继承、用户权限与组权限。  
**参数**：`sandboxId`、`magic`（必填）；`cloudPath`（string，默认 `/`）。  
**返回**：`ok`、`cloudPath`、`isInherit`、`signed{name, value}`、`users[]`、`groups[]`、`userCount`、`groupCount`、`usersTruncated`、`groupsTruncated`、`truncated`、`memberLimit`（成员上限各 200，超出会标注截断）。

### `nutstore_l3_read_file`
**用途**：直接读云端文件内容（不写本地、不改云端）。  
**参数**：`sandboxId`、`magic`、`cloudPath`（必填）；`offset`（integer，默认 0）；`length`（integer，默认 0 = 读到 256 KiB 或文件尾）。  
**返回**：`ok`、`cloudPath`、`version`、`totalSize`、`offset`、`length`、`end`、`full`、`truncated`、`requestedBytes`、`acceptedDeltaStream`、`integrity`（`verified`/分段）、`sha256`、`cloudHash`、`cloudHashMatches`、`encoding`（`text`/`base64`）、`bom`、`text` 或 `contentBase64`。  
**注意**：单次 ≤ 256 KiB；严格 UTF-8 才返回 `text`，否则返回 `contentBase64`；整文件读取按云端 hash 校验，分段读取只给本段 SHA-256。

### `nutstore_l3_download`
**用途**：把云端文件下载到本地新路径（能拿未同步到本地的文件）。**写本地，默认预演。**  
**参数**：`sandboxId`、`magic`、`cloudPath`、`destPath`（必填）；`confirm`；`dryRun`。  
**返回**：`allowed`、`wouldWrite`、`dryRun`、`unlockVariablePresent`、`probe{sandboxId, magic, cloudPath, destPath}`、`refusals[]`、`notes[]`、`executed`、`result`。  
**注意**：目标文件已存在一律拒绝，绝不覆盖；上级目录必须已存在。

---

## 8. 云端文件写入

三个工具都遵循 2.4 的门控和 2.1 的写结果信封；执行后会按真实云端状态复查，失败不回滚、不重试。

### `nutstore_l3_move`
**用途**：同一已绑定同步文件夹内移动/重命名普通文件。  
**参数**：`sandboxId`、`magic`、`srcCloudPath`、`destCloudPath`（必填）；`confirm`；`dryRun`。  
**返回**：写工具信封；`probe` 含源/目标父目录、源详情等；`verification` 核对源消失、目标大小/哈希/版本。  
**限制**：只允许普通文件、非根路径、父目录已存在；目标已存在（含大小写别名）、目录、跨沙箱一律拒绝。

### `nutstore_l3_mkdir`
**用途**：在已绑定同步文件夹中创建一个云端子目录。  
**参数**：`sandboxId`、`magic`、`cloudPath`（必填，以 `/` 开头、不能是根）；`confirm`；`dryRun`。  
**返回**：写工具信封；`verification` 核对目录类型、路径、版本和父目录成员。  
**限制**：父目录必须已存在；目标已存在、不递归创建。

### `nutstore_l3_create_file`
**用途**：在云端创建一个新文件；内容可内联、为空，或来自白名单本地文件。  
**参数**：`sandboxId`、`magic`、`directoryName`、`fileName`（必填，各为一个路径段）；`contentBase64` 与 `sourcePath` 二选一（`contentBase64=""` 表示空文件）；`confirm`；`dryRun`。  
**返回**：写工具信封；`probe.directory`/`contentBytes`/`emptyContent`/`contentSource`/`directoryCreated`/`directoryEmpty`；执行后 `result{ok, attempted, contentUploaded, metadataAttempted, metadataSubmitted, cloudPath, size, emptyFile, cloudHash, version, verification, outcome, reason}`。  
**内容上限**：内联 base64 ≤ 256 KiB；本地文件 ≤ 16 MiB（>393216 字节走 4 MiB 分块 + 块列表上传）。本地文件模式需 `read-roots.txt` 白名单。空文件只能走内联空内容（本地 0 字节文件会被本地读取策略拒绝，提示改用 `contentBase64=""`）。  
**空文件分支**：客户端对空内容跳过内容上传，云端 hash 是 `Base64(32 个零字节)`、`blockListNameBase64` 是 `Base64(40 个零字节)`、`fullObjectNameBase64=null`；写后同样核对类型、大小 0、云端 hash、版本与父目录成员。  
**结构安全**：只允许在**本次调用新建且建后确认为空**的目录里创建，因此新文件不可能与既有对象构成大小写别名；目录名/文件名各一个路径段。

---

## 9. 客户端设置

### `nutstore_l3_read_settings`
**用途**：读客户端设置快照。**参数**：无。  
**返回**：`ok`、`domain`、`source`、`count`、`settings{<设置名>: {value, changed}}`。常见设置项：`AutoStart`、`IsShutdownOnIdle`、`IsSyncPaused`、`EnableSafeGuard`、`EnableFileProtectionLock`、`IsNotifyOnFilesUpdated`、`IsDirectOpen`、`IsExperimental`、`EnableLANSync`、`AutoUploadLockedFile`、`SearchHotkey`、`Proxy`、`Storage` 等。

### `nutstore_l3_write_setting`
**用途**：修改白名单内的客户端设置。**写，默认预演。**  
**参数**：`name`（string，必填）；`value`（string，必填，布尔设置只接受 `true`/`false`）；`confirm`；`dryRun`。  
**返回**：`allowed`、`wouldWrite`、`dryRun`、`tier`、`unlocked{normal, system}`、`probe{name, value}`、`refusals[]`、`notes[]`、`executed`、`result`。  
**门控**：普通开关只需 `l3-write.enabled`；`EnableFileProtectionLock` / `EnableSafeGuard` / `IsSyncPaused` 还需要 `l3-write-system.enabled`。

---

## 10. 事件

### `nutstore_l3_latest_event`
**用途**：读某个同步文件夹的最新事件 ID（增量同步游标）。  
**参数**：`sandboxId`、`magic`（必填，沙箱必须已绑定本地）。  
**返回**：`ok`、`domain`、`sandboxId`、`latestEventId`。

### `nutstore_l3_remote_events_page`
**用途**：读一页云端增量事件；不调用会写 `EventDb`/未读数的客户端下行入口，不返回会话头。  
**参数**：`sandboxId`、`magic`（必填，已绑定）；`cursor`（integer，默认 0，后续用上一页 `cursorOut`）。  
**返回**：`ok`、`sandboxId`、`cursorIn`、`cursorOut`、`hasMore`、`complete`、`eventCount`、`payloadBytes`、`events[]`（原始 NDJSON 字符串）。  
**注意**：传输与解压正文各 1 MiB 上限；`hasMore=true` 时用 `cursorOut` 继续；TLS LibCurl 回退时客户端可能把 `ForceDisableLibCurl` 设为 `true`，因此该工具可能修改本地传输配置。

---

## 11. 兼容性

- 工具绑定依赖客户端 **7.2.12.0** 的内部混淆符号；客户端升级后先跑 `nutstore_l3_verify_call_sites`，再跑 `.\deploy\check-client-bindings.ps1`。
- `tools/list` 当前 36 项。新增/删除工具必须同步更新本文档。
- 本文件是 API 契约，不是安全边界本身：真正的门控在插件与默认域钩子代码里，文档描述不构成绕过理由。
