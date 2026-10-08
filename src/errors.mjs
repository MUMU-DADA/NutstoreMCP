/**
 * 统一的错误类型。
 *
 * 每个错误都带一个 `code`（机器可读）和 `hint`（给人看的下一步动作）。
 * MCP 工具会把它们渲染成结构化的失败结果 —— 让调用方知道**该改什么**，
 * 而不是只看到一句 "request failed"。
 */

export class NutstoreError extends Error {
  constructor(message, { code = 'NUTSTORE_ERROR', hint = null, cause = null } = {}) {
    super(message);
    this.name = new.target.name;
    this.code = code;
    this.hint = hint;
    if (cause) this.cause = cause;
  }

  toJSON() {
    return { code: this.code, message: this.message, hint: this.hint };
  }
}

/** 配置缺失或非法。 */
export class ConfigError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'CONFIG_ERROR', ...opts });
  }
}

/** 路径越界、包含非法片段，或不在白名单内。 */
export class PathNotAllowedError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'PATH_NOT_ALLOWED', ...opts });
  }
}

/** 只读模式下尝试写操作。 */
export class ReadOnlyError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'READ_ONLY', ...opts });
  }
}

/** WebDAV 认证失败。 */
export class AuthError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'AUTH_FAILED', ...opts });
  }
}

/** 远端资源不存在。 */
export class NotFoundError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'NOT_FOUND', ...opts });
  }
}

/** 本地配额用尽（自建限流器触发）。 */
export class RateLimitError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'RATE_LIMITED', ...opts });
  }
}

/** 本地坚果云数据不可用（未安装、DB 缺失、格式不识别）。 */
export class LocalDataError extends NutstoreError {
  constructor(message, opts = {}) {
    super(message, { code: 'LOCAL_DATA_UNAVAILABLE', ...opts });
  }
}

/** 远端返回了非预期状态。 */
export class RemoteError extends NutstoreError {
  constructor(message, { status = null, ...opts } = {}) {
    super(message, { code: 'REMOTE_ERROR', ...opts });
    this.status = status;
  }
}
