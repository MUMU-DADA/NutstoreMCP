/**
 * 极简 MCP stdio 服务端 —— 零依赖。
 *
 * 传输：**换行分隔的 JSON-RPC 2.0**（不是 LSP 的 Content-Length 帧）。
 *
 * ⚠️ 铁律：`stdout` 只能出现协议消息。任何日志都必须走 `stderr` ——
 * 一个混进 stdout 的 `console.log` 会直接破坏整个会话。
 */

const JSONRPC_VERSION = '2.0';

export const PARSE_ERROR = -32700;
export const INVALID_REQUEST = -32600;
export const METHOD_NOT_FOUND = -32601;
export const INVALID_PARAMS = -32602;
export const INTERNAL_ERROR = -32603;

/** 我们声明支持的协议版本，按新到旧。 */
export const SUPPORTED_PROTOCOL_VERSIONS = Object.freeze([
  '2026-07-28',
  '2025-11-25',
  '2025-06-18',
  '2025-03-26',
  '2024-11-05',
]);

export const DEFAULT_PROTOCOL_VERSION = '2025-06-18';

export function negotiateProtocolVersion(requested) {
  if (typeof requested === 'string' && SUPPORTED_PROTOCOL_VERSIONS.includes(requested)) {
    return requested;
  }
  return DEFAULT_PROTOCOL_VERSION;
}

class RpcError extends Error {
  constructor(code, message, data) {
    super(message);
    this.code = code;
    this.data = data;
  }
}

/**
 * 工具注册表 + 分发。
 *
 * 每个工具是 `{ name, title, description, inputSchema, handler }`。
 * handler 收到 `(args, ctx)`，返回任意可序列化值；抛出的异常会被转成
 * **工具级失败**（`isError: true`）而不是协议级错误 —— 这样模型能看到
 * 失败原因并自行调整，而不是整个会话断掉。
 */
export class McpServer {
  #name;
  #version;
  #instructions;
  #tools = new Map();
  #log;

  constructor({ name, version, instructions = '', log = () => {} }) {
    this.#name = name;
    this.#version = version;
    this.#instructions = instructions;
    this.#log = log;
  }

  tool(def) {
    if (!def?.name || typeof def.handler !== 'function') {
      throw new TypeError('tool() 需要 { name, handler }');
    }
    if (this.#tools.has(def.name)) {
      throw new Error(`工具重名：${def.name}`);
    }
    this.#tools.set(def.name, def);
    return this;
  }

  get toolNames() {
    return [...this.#tools.keys()];
  }

  listTools() {
    return [...this.#tools.values()].map((t) => ({
      name: t.name,
      ...(t.title ? { title: t.title } : {}),
      description: t.description ?? '',
      inputSchema: t.inputSchema ?? { type: 'object', properties: {} },
      ...(t.annotations ? { annotations: t.annotations } : {}),
    }));
  }

  async #callTool({ name, arguments: args }) {
    const tool = this.#tools.get(name);
    if (!tool) {
      throw new RpcError(INVALID_PARAMS, `未知工具：${name}`, {
        available: this.toolNames,
      });
    }

    const started = Date.now();
    try {
      const result = await tool.handler(args ?? {});
      const elapsedMs = Date.now() - started;
      this.#log(`tool ${name} ok (${elapsedMs}ms)`);
      return {
        content: [{ type: 'text', text: renderResult(result) }],
        ...(result && typeof result === 'object' && !Array.isArray(result)
          ? { structuredContent: result }
          : {}),
      };
    } catch (err) {
      const elapsedMs = Date.now() - started;
      this.#log(`tool ${name} failed (${elapsedMs}ms): ${err?.message}`);
      return {
        content: [{ type: 'text', text: renderError(err) }],
        isError: true,
      };
    }
  }

  async handle(message) {
    if (message === null || typeof message !== 'object' || Array.isArray(message)) {
      return errorResponse(null, INVALID_REQUEST, '请求必须是 JSON 对象');
    }

    const { id, method, params } = message;
    const isNotification = id === undefined || id === null;

    try {
      switch (method) {
        case 'initialize':
          return respond(id, {
            protocolVersion: negotiateProtocolVersion(params?.protocolVersion),
            capabilities: { tools: { listChanged: false } },
            serverInfo: { name: this.#name, version: this.#version },
            ...(this.#instructions ? { instructions: this.#instructions } : {}),
          });

        case 'notifications/initialized':
        case 'initialized':
          return null;

        case 'ping':
          return respond(id, {});

        case 'tools/list':
          return respond(id, { tools: this.listTools() });

        case 'tools/call':
          return respond(id, await this.#callTool(params ?? {}));

        case 'resources/list':
          return respond(id, { resources: [] });

        case 'prompts/list':
          return respond(id, { prompts: [] });

        case 'notifications/cancelled':
          return null;

        default:
          if (isNotification) return null;
          return errorResponse(id, METHOD_NOT_FOUND, `不支持的方法：${method}`);
      }
    } catch (err) {
      if (isNotification) return null;
      if (err instanceof RpcError) return errorResponse(id, err.code, err.message, err.data);
      return errorResponse(id, INTERNAL_ERROR, err?.message ?? String(err));
    }
  }

  /**
   * 跑 stdio 循环。返回一个在 stdin 关闭时 resolve 的 Promise。
   */
  run({ input = process.stdin, output = process.stdout } = {}) {
    input.setEncoding?.('utf8');

    return new Promise((resolve) => {
      let buffer = '';
      let chain = Promise.resolve();

      const write = (obj) => {
        output.write(`${JSON.stringify(obj)}\n`);
      };

      input.on('data', (chunk) => {
        buffer += chunk;
        let idx;
        while ((idx = buffer.indexOf('\n')) !== -1) {
          const line = buffer.slice(0, idx).trim();
          buffer = buffer.slice(idx + 1);
          if (!line) continue;

          // 解析也放进串行链里。曾经把解析错误直接写出 stdout，
          // 结果它插到了前面排队中的响应之前 —— 响应顺序与请求顺序不一致。
          // 全部输出走同一条链，顺序才可预测。
          chain = chain
            .then(() => {
              let message;
              try {
                message = JSON.parse(line);
              } catch {
                return errorResponse(null, PARSE_ERROR, 'JSON 解析失败');
              }
              return this.handle(message);
            })
            .then((response) => {
              if (response) write(response);
            })
            .catch((err) => {
              this.#log(`内部错误：${err?.stack ?? err}`);
            });
        }
      });

      const finish = () => {
        chain.finally(() => resolve());
      };
      input.on('end', finish);
      input.on('close', finish);
    });
  }
}

function respond(id, result) {
  return { jsonrpc: JSONRPC_VERSION, id, result };
}

function errorResponse(id, code, message, data) {
  return {
    jsonrpc: JSONRPC_VERSION,
    id: id ?? null,
    error: { code, message, ...(data !== undefined ? { data } : {}) },
  };
}

/** 把工具返回值渲染成人类可读文本（同时保留 structuredContent）。 */
export function renderResult(result) {
  if (result === undefined || result === null) return 'ok';
  if (typeof result === 'string') return result;
  return JSON.stringify(result, null, 2);
}

/** 把异常渲染成**可操作**的失败信息，而不是堆栈。 */
export function renderError(err) {
  const parts = [`❌ ${err?.message ?? String(err)}`];
  if (err?.code) parts.push(`code: ${err.code}`);
  if (err?.hint) parts.push(`hint: ${err.hint}`);
  if (err?.cause?.message) parts.push(`cause: ${err.cause.message}`);
  return parts.join('\n');
}
