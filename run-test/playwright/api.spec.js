// UniFlow WebAdmin API 测试（对冻结副本 publish 产物；服务由 playwright.config.js 的 webServer 启动）
// 密码类期望值与基线配置一致：WebAdmin:AdminPassword=Advia1、Aptio:Database:Password=TestDbPwd、DMS:DbPassword=DmsPwd。
// 重启类用例放最后（POST /api/restart 202 + 等待业务宿主恢复）。绝不带鉴权调用 /api/restart/process（会杀进程）。
const { test, expect } = require('@playwright/test');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const ADMIN_PW = 'Advia1';
const APP_DIR = process.env.UNIFLOW_APP_DIR || '/tmp/uniflow-e2e/app';
const CFG_FILE = path.join(APP_DIR, 'appsettings.json');

const md5Of = (f) => crypto.createHash('md5').update(fs.readFileSync(f)).digest('hex');

async function getNonce(request) {
  const r = await request.get('/api/auth/challenge');
  expect(r.ok()).toBeTruthy();
  const body = await r.json();
  return body.nonce;
}
const hashFor = (nonce, pw = ADMIN_PW) => crypto.createHash('sha256').update(pw + nonce).digest('hex');
const authHeaders = (nonce, hash = hashFor(nonce)) => ({
  'X-Admin-Nonce': nonce,
  'X-Admin-Hash': hash,
  'Content-Type': 'application/json',
});

async function authedPutBatch(request, changes) {
  const nonce = await getNonce(request);
  return request.put('/api/config/batch', { headers: authHeaders(nonce), data: { changes } });
}

// 轮询 /api/restart/status 直到业务宿主恢复（restarting=false 且 ready=true）
async function waitRestartDone(request) {
  await new Promise((r) => setTimeout(r, 2500)); // 端点内部延迟 1.5s 后才开始重建
  let sawRestarting = false;
  for (let i = 0; i < 30; i++) {
    const r = await request.get('/api/restart/status');
    if (r.ok()) {
      const st = await r.json();
      if (st.restarting) sawRestarting = true;
      if (st.ready === true && st.restarting === false && (sawRestarting || i >= 3)) {
        expect(st.lastFailure, '上次重启失败: ' + st.lastFailure).toBeFalsy();
        return;
      }
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error('/api/restart/status 30 秒内未恢复 ready');
}

test.describe('1. 基础端点', () => {
  test('GET /api/version 与 /api/health 返回 200', async ({ request }) => {
    const v = await request.get('/api/version');
    expect(v.status()).toBe(200);
    const h = await request.get('/api/health');
    expect(h.status()).toBe(200);
  });
});

test.describe('2/3. 配置读取脱敏', () => {
  test('GET /api/config 全量：三个密码均为 ***，响应不含任何明文密码', async ({ request }) => {
    const r = await request.get('/api/config');
    expect(r.status()).toBe(200);
    const text = await r.text();
    const cfg = JSON.parse(text);
    expect(cfg.Aptio.Database.Password).toBe('***');
    expect(cfg.DMS.DbPassword).toBe('***');
    expect(cfg.WebAdmin.AdminPassword).toBe('***');
    expect(text).not.toContain('TestDbPwd');
    expect(text).not.toContain('DmsPwd');
    expect(text).not.toContain('Advia1');
  });

  for (const [name, q, check] of [
    ['WebAdmin（父节点，历史 P0）', 'WebAdmin', (j) => expect(j.value.AdminPassword).toBe('***')],
    ['Aptio:Database', 'Aptio:Database', (j) => expect(j.value.Password).toBe('***')],
    ['DMS（父节点）', 'DMS', (j) => expect(j.value.DbPassword).toBe('***')],
    ['WebAdmin:AdminPassword（密码键本身）', 'WebAdmin:AdminPassword', (j) => expect(j.value).toBe('***')],
    ['Aptio:Database:Password（密码键本身）', 'Aptio:Database:Password', (j) => expect(j.value).toBe('***')],
  ]) {
    test(`GET /api/config?path=${q} 无明文`, async ({ request }) => {
      const r = await request.get('/api/config?path=' + encodeURIComponent(q));
      expect(r.status()).toBe(200);
      const text = await r.text();
      const j = JSON.parse(text);
      check(j);
      expect(text).not.toContain('Advia1');
      expect(text).not.toContain('TestDbPwd');
      expect(text).not.toContain('DmsPwd');
    });
  }
});

test.describe('4. 鉴权挑战', () => {
  test('GET /api/auth/challenge 返回 64 位 hex nonce', async ({ request }) => {
    const r = await request.get('/api/auth/challenge');
    expect(r.status()).toBe(200);
    const { nonce, expiresInSeconds } = await r.json();
    expect(nonce).toMatch(/^[0-9a-f]{64}$/);
    expect(expiresInSeconds).toBe(60);
    // 客户端可算 sha256(密码+nonce) 小写 hex（服务端匹配性在 6/7 用例中经真实写请求验证）
    const h = hashFor(nonce);
    expect(h).toMatch(/^[0-9a-f]{64}$/);
    expect(h).toBe(hashFor(nonce)); // 确定性
  });
});

test.describe('5. 破坏性端点无鉴权一律 401', () => {
  const cases = [
    ['PUT /api/config', (rq) => rq.put('/api/config', { data: { path: 'Logging:LogLevel:Default', value: 'Warning' } })],
    ['PUT /api/config/batch', (rq) => rq.put('/api/config/batch', { data: { changes: [{ path: 'Logging:LogLevel:Default', value: 'Warning' }] } })],
    ['GET /api/config/download', (rq) => rq.get('/api/config/download')],
    ['POST /api/update/upload', (rq) => rq.post('/api/update/upload', { data: Buffer.from('not-a-zip') })],
    ['POST /api/update/apply', (rq) => rq.post('/api/update/apply')],
    ['POST /api/restart', (rq) => rq.post('/api/restart')],
    ['POST /api/restart/process（无鉴权必须被拦截，绝不实际执行）', (rq) => rq.post('/api/restart/process')],
    ['DELETE /api/errors', (rq) => rq.delete('/api/errors')],
    ['POST /api/dispose/pending/reset', (rq) => rq.post('/api/dispose/pending/reset', { data: { barcodes: ['E2E-NOT-AUTH'] } })],
    ['POST /api/dispose/pending/delete', (rq) => rq.post('/api/dispose/pending/delete', { data: { barcodes: ['E2E-NOT-AUTH'], confirm: true } })],
  ];
  for (const [name, fn] of cases) {
    test(name + ' → 401', async ({ request }) => {
      const r = await fn(request);
      expect(r.status(), name + ' 应 401').toBe(401);
    });
  }
});

test.describe('6. nonce 一次性与哈希校验', () => {
  test('正确 hash 200；同一 nonce 复用 401；错误 hash 401', async ({ request }) => {
    const nonce = await getNonce(request);
    const h = hashFor(nonce);
    const r1 = await request.put('/api/config/batch', {
      headers: authHeaders(nonce, h),
      data: { changes: [{ path: 'Aptio:DisposeSample:LoopIntervalSeconds', value: 4 }] },
    });
    expect(r1.status(), '首次鉴权应 200').toBe(200);

    const r2 = await request.put('/api/config/batch', {
      headers: authHeaders(nonce, h), // 同一 nonce 重放
      data: { changes: [{ path: 'Aptio:DisposeSample:LoopIntervalSeconds', value: 4 }] },
    });
    expect(r2.status(), 'nonce 一次性：复用应 401').toBe(401);

    const nonce3 = await getNonce(request);
    const r3 = await request.put('/api/config/batch', {
      headers: authHeaders(nonce3, hashFor(nonce3, 'WrongPassword')),
      data: { changes: [{ path: 'Aptio:DisposeSample:LoopIntervalSeconds', value: 4 }] },
    });
    expect(r3.status(), '错误 hash 应 401').toBe(401);
  });
});

test.describe('7. PUT /api/config/batch 校验语义', () => {
  test('合法多字段 → 200 且 count 正确', async ({ request }) => {
    const r = await authedPutBatch(request, [
      { path: 'Aptio:DisposeSample:LoopIntervalSeconds', value: 4 },
      { path: 'Logging:LogLevel:Default', value: 'Warning' },
    ]);
    expect(r.status()).toBe(200);
    const j = await r.json();
    expect(j.success).toBe(true);
    expect(j.count, '应写入 2 项').toBe(2);
  });

  test('非法字段（Aptio:Port="abc"）→ 400 且文件 md5 不变（原子性）', async ({ request }) => {
    const before = md5Of(CFG_FILE);
    const r = await authedPutBatch(request, [
      { path: 'Aptio:DisposeSample:LoopIntervalSeconds', value: 9 }, // 合法项也在同一批
      { path: 'Aptio:Port', value: 'abc' },                          // 非法项
    ]);
    expect(r.status()).toBe(400);
    expect(md5Of(CFG_FILE), '400 时不得写盘').toBe(before);
    const j = await r.json();
    expect(j.success).toBeFalsy();
  });

  test('密码字段提交 "***" → 200 且文件中真实密码不变', async ({ request }) => {
    const r = await authedPutBatch(request, [
      { path: 'Aptio:Database:Password', value: '***' },
      { path: 'DMS:DbPassword', value: '***' },
    ]);
    expect(r.status()).toBe(200);
    const j = await r.json();
    expect(j.count).toBe(0);
    expect(j.skipped, '两个密码字段应保持原值并计入 skipped').toBe(2);
    const fileText = fs.readFileSync(CFG_FILE, 'utf8');
    expect(fileText).toContain('TestDbPwd');
    expect(fileText).toContain('DmsPwd');
  });

  test('WebAdmin:AdminPassword 不可经 API 修改 → 400', async ({ request }) => {
    const r = await authedPutBatch(request, [
      { path: 'WebAdmin:AdminPassword', value: 'Pwned1' },
    ]);
    expect(r.status()).toBe(400);
    const fileText = fs.readFileSync(CFG_FILE, 'utf8');
    expect(fileText).toContain('Advia1');
    expect(fileText).not.toContain('Pwned1');
  });

  test('空 changes → 400；超过 100 项 → 400', async ({ request }) => {
    const r1 = await authedPutBatch(request, []);
    expect(r1.status()).toBe(400);
    const many = Array.from({ length: 101 }, () => ({ path: 'Logging:LogLevel:Default', value: 'Warning' }));
    const r2 = await authedPutBatch(request, many);
    expect(r2.status()).toBe(400);
  });
});

test.describe('8. 配置原文下载', () => {
  test('无鉴权 401', async ({ request }) => {
    const r = await request.get('/api/config/download');
    expect(r.status()).toBe(401);
  });

  test('正确鉴权 200：完整原文（按设计含明文密码）+ Content-Disposition', async ({ request }) => {
    const nonce = await getNonce(request);
    const r = await request.get('/api/config/download', { headers: { 'X-Admin-Nonce': nonce, 'X-Admin-Hash': hashFor(nonce) } });
    expect(r.status()).toBe(200);
    const cd = r.headers()['content-disposition'] || '';
    expect(cd).toContain('appsettings-');
    expect(cd.toLowerCase()).toContain('attachment');
    const text = await r.text();
    // 按设计：下载是备份用途，返回完整原文（含明文密码）
    expect(text).toContain('TestDbPwd');
    expect(text).toContain('DmsPwd');
    expect(text).toContain('Advia1');
  });
});

test.describe('9. 数据库连通性测试（免鉴权、只测已保存配置）', () => {
  test('POST /api/config/test-db?target=aptio：200/500 可接受，响应不含明文密码', async ({ request }) => {
    const r = await request.post('/api/config/test-db?target=aptio');
    expect([200, 500], '实际状态 ' + r.status()).toContain(r.status());
    const text = await r.text();
    expect(text).not.toContain('TestDbPwd');
  });

  test('POST /api/config/test-db?target=dms：同上，响应不含 DmsPwd', async ({ request }) => {
    const r = await request.post('/api/config/test-db?target=dms');
    expect([200, 500], '实际状态 ' + r.status()).toContain(r.status());
    const text = await r.text();
    expect(text).not.toContain('DmsPwd');
  });

  test('POST /api/config/test-db?target=xxx → 400', async ({ request }) => {
    const r = await request.post('/api/config/test-db?target=xxx');
    expect(r.status()).toBe(400);
  });
});

test.describe('10. 丢弃队列读操作不应要求鉴权', () => {
  test('GET /api/dispose/pending 无鉴权不 401（记录实际状态：内层宿主未就绪预期 503）', async ({ request }, testInfo) => {
    const r = await request.get('/api/dispose/pending?minResend=24&limit=5');
    testInfo.annotations.push({ type: 'actual-status', description: 'GET pending = ' + r.status() });
    expect(r.status(), '读操作不应 401').not.toBe(401);
    expect([200, 500, 503], '实际状态 ' + r.status()).toContain(r.status());
    const text = await r.text();
    expect(text).not.toContain('TestDbPwd');
  });

  test('POST /api/dispose/pending/recheck 无鉴权不 401（记录实际状态）', async ({ request }, testInfo) => {
    const r = await request.post('/api/dispose/pending/recheck', { data: { barcodes: ['E2E-READONLY-CHECK'] } });
    testInfo.annotations.push({ type: 'actual-status', description: 'POST recheck = ' + r.status() });
    expect(r.status(), 'recheck 属只读检查，不应 401').not.toBe(401);
    expect([200, 400, 500, 503], '实际状态 ' + r.status()).toContain(r.status());
  });
});

test.describe('11. 内部重启（放最后执行）', () => {
  test('POST /api/restart 正确鉴权 → 202，业务宿主随后恢复 ready', async ({ request }) => {
    const nonce = await getNonce(request);
    const r = await request.post('/api/restart', { headers: { 'X-Admin-Nonce': nonce, 'X-Admin-Hash': hashFor(nonce) } });
    expect(r.status()).toBe(202);
    await waitRestartDone(request);
    // 重启后基础端点仍可用
    expect((await request.get('/api/version')).status()).toBe(200);
  });
});
