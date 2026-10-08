/**
 * 滑动窗口限流器。
 *
 * 存在的理由：坚果云 WebDAV 的官方限制是 **600 次 / 30 分钟**（免费版，
 * 付费 1500）。超限会影响账号的正常同步 —— 所以宁可本地先挡住，
 * 也不要等服务端 429。
 *
 * 设计取舍：**快速失败，不排队等待**。一个 MCP 工具调用如果被告知
 * 「配额用尽，请 12 分钟后再试」，模型可以换个策略；如果挂在那里等
 * 12 分钟，调用方只会超时，而且看不出原因。
 */

import { RateLimitError } from './errors.mjs';

export class SlidingWindowLimiter {
  #limit;
  #windowMs;
  #hits = [];

  constructor({ limit = 600, windowMs = 30 * 60 * 1000 } = {}) {
    if (!Number.isFinite(limit) || limit <= 0) {
      throw new TypeError('limit must be a positive number');
    }
    if (!Number.isFinite(windowMs) || windowMs <= 0) {
      throw new TypeError('windowMs must be a positive number');
    }
    this.#limit = Math.floor(limit);
    this.#windowMs = windowMs;
  }

  #prune(now) {
    const cutoff = now - this.#windowMs;
    let drop = 0;
    while (drop < this.#hits.length && this.#hits[drop] <= cutoff) drop++;
    if (drop > 0) this.#hits.splice(0, drop);
  }

  /** 当前窗口内已用次数。 */
  used(now = Date.now()) {
    this.#prune(now);
    return this.#hits.length;
  }

  /** 剩余可用次数。 */
  remaining(now = Date.now()) {
    return Math.max(0, this.#limit - this.used(now));
  }

  /** 还要等多久才能再发一次（毫秒）。配额未用尽时为 0。 */
  retryAfterMs(now = Date.now()) {
    this.#prune(now);
    if (this.#hits.length < this.#limit) return 0;
    const oldest = this.#hits[this.#hits.length - this.#limit];
    return Math.max(0, oldest + this.#windowMs - now);
  }

  /** 快照，用于诊断工具。 */
  snapshot(now = Date.now()) {
    const used = this.used(now);
    return {
      limit: this.#limit,
      windowMs: this.#windowMs,
      used,
      remaining: Math.max(0, this.#limit - used),
      retryAfterMs: this.retryAfterMs(now),
    };
  }

  /**
   * 申请一次配额。配额用尽时抛 `RateLimitError`，**不阻塞**。
   */
  acquire(now = Date.now()) {
    this.#prune(now);
    if (this.#hits.length >= this.#limit) {
      const waitMs = this.retryAfterMs(now);
      const waitSec = Math.ceil(waitMs / 1000);
      throw new RateLimitError(
        `WebDAV 请求配额已用尽（${this.#limit} 次 / ${Math.round(this.#windowMs / 60000)} 分钟）`,
        {
          hint: `请等待约 ${waitSec} 秒后重试。若需更高配额，可调整 NUTSTORE_RATE_LIMIT；`
            + '付费账号官方上限为 1500 次 / 30 分钟。',
        },
      );
    }
    this.#hits.push(now);
    return this.remaining(now);
  }
}
