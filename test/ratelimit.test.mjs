import test from 'node:test';
import assert from 'node:assert/strict';

import { RateLimitError } from '../src/errors.mjs';
import { SlidingWindowLimiter } from '../src/ratelimit.mjs';

test('允许到上限，之后快速失败而非阻塞', () => {
  const t0 = 1_000_000;
  const limiter = new SlidingWindowLimiter({ limit: 3, windowMs: 1000 });

  assert.equal(limiter.remaining(t0), 3);
  limiter.acquire(t0);
  limiter.acquire(t0 + 10);
  limiter.acquire(t0 + 20);
  assert.equal(limiter.remaining(t0 + 30), 0);

  const started = Date.now();
  assert.throws(() => limiter.acquire(t0 + 30), RateLimitError);
  assert.ok(Date.now() - started < 100, '必须立即失败，不能等待');
});

test('窗口滑出后配额恢复', () => {
  const t0 = 5_000_000;
  const limiter = new SlidingWindowLimiter({ limit: 2, windowMs: 1000 });
  limiter.acquire(t0);
  limiter.acquire(t0 + 100);
  assert.throws(() => limiter.acquire(t0 + 200), RateLimitError);

  // 第一次请求滑出窗口
  assert.equal(limiter.remaining(t0 + 1001), 1);
  limiter.acquire(t0 + 1001);
  assert.equal(limiter.remaining(t0 + 1002), 0);
});

test('retryAfterMs 指向最早那次请求的过期时间', () => {
  const t0 = 9_000_000;
  const limiter = new SlidingWindowLimiter({ limit: 1, windowMs: 60_000 });
  limiter.acquire(t0);
  assert.equal(limiter.retryAfterMs(t0), 60_000);
  assert.equal(limiter.retryAfterMs(t0 + 59_000), 1_000);
  assert.equal(limiter.retryAfterMs(t0 + 60_001), 0);
});

test('错误信息里给出可操作的等待秒数与调整建议', () => {
  const t0 = 12_000_000;
  const limiter = new SlidingWindowLimiter({ limit: 1, windowMs: 30 * 60_000 });
  limiter.acquire(t0);
  try {
    limiter.acquire(t0 + 60_000);
    assert.fail('应当抛出');
  } catch (err) {
    assert.equal(err.code, 'RATE_LIMITED');
    assert.match(err.message, /1 次 \/ 30 分钟/);
    assert.match(err.hint, /1740 秒/);
    assert.match(err.hint, /NUTSTORE_RATE_LIMIT/);
  }
});

test('snapshot 反映当前状态', () => {
  const limiter = new SlidingWindowLimiter({ limit: 10, windowMs: 1000 });
  limiter.acquire();
  const snap = limiter.snapshot();
  assert.equal(snap.limit, 10);
  assert.equal(snap.used, 1);
  assert.equal(snap.remaining, 9);
});

test('构造参数校验', () => {
  assert.throws(() => new SlidingWindowLimiter({ limit: 0 }), TypeError);
  assert.throws(() => new SlidingWindowLimiter({ limit: -1 }), TypeError);
  assert.throws(() => new SlidingWindowLimiter({ limit: 1, windowMs: 0 }), TypeError);
});
