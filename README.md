# NutstoreMCP

坚果云（Nutstore）的本地 MCP 服务器。目标是让 AI 能用上**原本只能在客户端 GUI 上操作**的能力。

**零运行时依赖** —— 只用 Node 内置模块（`node:sqlite` / `fetch` / `node:test`）。

---

## 现状

| 层 | 状态 | 说明 |
|---|---|---|
| **L1 文件面** | ✅ 已实现 | 经官方 WebDAV 读写云端文件 |
| **L2 数据面** | ✅ 已实现 | 只读本地客户端数据库，提供同步状态 |
| **L3 控制面** | ⛔ 未实现 | 创建同步目录、改客户端设置 —— 见下文 |

L3 是那个「只能在 GUI 上做」的部分。它为什么还没做、以及准备怎么做，
写在 [ARCHITECTURE.md](ARCHITECTURE.md) 第 7 节。**不要用 L1 的文件操作去模拟它** ——
WebDAV 建出来的目录，客户端认得但不会同步到本地（会被标记 `doNotSync`）。

---

## 快速开始

### 1. 拿一个应用密码

登录 [坚果云网页版](https://www.jianguoyun.com/) → 账户信息 → 安全选项 →
第三方应用管理 → 添加应用密码。

⚠️ **这是应用密码，不是登录密码。** 步骤见[官方帮助](https://help.jianguoyun.com/?p=2064)。

### 2. 配置

```bash
cp .env.example .env
```

至少填三项：

```ini
NUTSTORE_USER=you@example.com
NUTSTORE_PASS=应用密码
NUTSTORE_ROOTS=我的坚果云        # 云端白名单，逗号分隔
```

### 3. 自检

```bash
npm run doctor
```

会打印配置、可用的同步文件夹、以及 WebDAV 连通性。**不含任何凭据值。**

### 4. 接进 DSH

在 `~/.dsh/profiles/web/cordis.patch.yml` 追加：

```yaml
- insert:
    - id: mcp-nutstore
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: nutstore
        transport: stdio
        command: node
        args: ['%USERPROFILE%\source\NutstoreMCP\src\server.mjs']
        toolCallTimeoutMs: 120000
        env:
          NUTSTORE_ROOTS: '我的坚果云'
          NUTSTORE_READ_ONLY: 'true'
```

凭据放 `.env`（已被 gitignore），不要写进这个配置文件。

---

## 工具

### L1 文件面

| 工具 | 说明 |
|---|---|
| `nutstore_health` | 连通性、配额、限流余量、本地库状态 |
| `nutstore_list` | 列出云端目录 |
| `nutstore_stat` | 单个路径的元信息 |
| `nutstore_read_text` | 读文本文件（默认上限 1 MB） |
| `nutstore_download` | 下载到本机（白名单目录内） |
| `nutstore_upload` | 上传本机文件（≤ 500 MB，需关闭只读） |
| `nutstore_mkdir` | 建目录，支持递归（需关闭只读） |
| `nutstore_move` | 移动 / 重命名（需关闭只读） |
| `nutstore_delete` | 删除，**必须显式 `confirm: true`**（需关闭只读） |

### L2 数据面（只读）

| 工具 | 说明 |
|---|---|
| `nutstore_sync_folders` | 同步文件夹清单：云端路径 ↔ 本地镜像路径 ↔ 对象统计 |
| `nutstore_sync_status` | 总览：文件夹数、受管对象、云端独有对象、错误数 |
| `nutstore_file_status` | 单个路径的同步状态（版本、大小、修改时间、是否云端占位符） |
| `nutstore_sync_errors` | 同步失败清单 |
| `nutstore_settings` | 客户端设置快照 |

L2 的工具在**没有 WebDAV 凭据**时也能用 —— 它读的是本地库。

---

## 安全模型

三条硬约束，都不是可选项：

1. **云端白名单（`NUTSTORE_ROOTS`）**——空白名单 = **拒绝一切**，不是放行一切（fail-closed）。
2. **本机白名单（`NUTSTORE_LOCAL_ROOTS`）**——上传的来源、下载的目标都必须落在其中。
   没有这层，「上传本地文件」就是一条任意文件外传通道。
3. **默认只读（`NUTSTORE_READ_ONLY=true`）**——所有写操作被拒。

另外：

- L2 **在 SQL 层就排除敏感键**：`encrypted_token` / `device_token` / `bcrypt_pwd_hash`
  根本不会出现在查询语句里，而不是「读了再过滤」。有测试直接拿真实密文比对全部 L2 输出。
- 本地数据库以 `readOnly` 打开，写入被 SQLite 物理拒绝（有测试验证）。
- 凭据只从环境变量 / `.env` 读，不进代码、不进 git。

### 为什么护栏做得这么重

坚果云的同步根里通常躺着大量明文凭据（密码本、API key、恢复码）。
一个没有白名单、默认可写的 MCP，等于把这些直接接到模型和任何提示注入面前。
如果你也在用这个项目，**请先把 `NUTSTORE_ROOTS` 收窄到真正需要的目录**。

---

## 已知限制

- **单次目录枚举 750 项。** 达到上限时 `nutstore_list` 会返回 `truncated: true` 并告警 ——
  不会假装结果完整。分页机制未经官方确认，所以没实现自动翻页。
- **请求频率 600 次 / 30 分钟**（免费版）。内置限流器会在本地先挡住，快速失败而非排队。
  配额用尽时错误里会给出还要等多久。
- **上传 ≤ 500 MB。** 更大的文件请放进本地同步目录，由客户端同步。
- **L2 依赖未公开的数据库格式。** 客户端升级后可能失效；所有查询都写成「读不到就降级」。
- 时间戳是 **.NET DateTime ticks（0001 纪元）**，不是 Windows FILETIME（1601 纪元）——
  两者差 1600 年，用错会算出 3626 年。

---

## 开发

```bash
npm test              # 97 个测试
npm run test:watch
npm run doctor        # 配置与连通性自检
```

测试构成：

| 文件 | 覆盖 |
|---|---|
| `paths.test.mjs` | 路径规范化、穿越防护、两个白名单的边界 |
| `xml.test.mjs` | 手写 XML 解析器（含畸形输入） |
| `ratelimit.test.mjs` | 滑动窗口限流 |
| `webdav.test.mjs` | 对**内存 mock WebDAV 服务器**走真实 HTTP |
| `local.test.mjs` | 对**真实数据库**只读验证 + 脱敏硬保证 |
| `mcp.test.mjs` | JSON-RPC 协议、工具级错误隔离、流式粘包 |
| `tools.test.mjs` | 工具护栏：只读闸门、双层白名单、删除确认 |
| `e2e.test.mjs` | 真的 spawn 服务进程，验证 stdout 只有协议消息 |

`webdav.test.mjs` 用真实 HTTP 而不是 mock 掉 `fetch` —— 后者只能证明代码自洽，
证明不了协议正确。这套测试已经抓到过 `[object Generator]` 被当成文件内容上传、
时间戳纪元用错、响应乱序等真实缺陷。

### `recon/MetadataDump/`

一个 .NET 元数据反射工具，用来枚举坚果云客户端程序集的类型/接口/常量/字符串字面量。
**不反编译、不执行目标代码。** 它是 [ARCHITECTURE.md](ARCHITECTURE.md) 里
「为什么不能直接写数据库」「客户端 IPC 到底有什么」这些结论的可复现依据。

---

## 许可

[MIT](LICENSE)
