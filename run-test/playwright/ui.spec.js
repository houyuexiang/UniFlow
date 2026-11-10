// UniFlow WebAdmin UI（Playwright + chromium）测试。
// 约定：dialog 口径 —— prompt=管理密码、confirm=二次确认、alert=提示；全部由测试代答并计数。
// 破坏性 UI 动作只验「弹密码 + 取消后不发请求」，唯一例外：保存全部/下载（接受密码）与最后的重启。
// 明文密码检查：基线配置含 Advia1/TestDbPwd/DmsPwd；除「下载配置原文」（按设计返回原文）外
// 任何 UI 响应体出现明文 → 判产品问题。
const { test, expect } = require('@playwright/test');
const fs = require('fs');
const crypto = require('crypto');

const ADMIN_PW = 'Advia1';
const APP_DIR = process.env.UNIFLOW_APP_DIR || '/tmp/uniflow-e2e/app';

// 统一监听：dialog 计数与代答、pageerror、console error（过滤 favicon 404 噪音并单独记录）、
// 4xx/5xx 响应（过滤 favicon）、指定 pattern 的请求计数。
function watch(page, opts = {}) {
  const st = {
    dialogs: [],          // {type, message}
    prompts: 0, confirms: 0, alerts: 0,
    pageErrors: [],
    consoleErrors: [],    // 已过滤 favicon
    faviconNoise: 0,
    badResponses: [],     // {url, status}（已过滤 favicon）
    reqCounts: {},        // 按 opts.countMethods 统计 {METHOD urlSubstring}
    bodyHits: [],         // 明文密码命中（排除 allowSecret 的 URL 片段）
  };
  const SECRET_RE = /TestDbPwd|DmsPwd|Advia1/;
  const ALLOW_SECRET = ['/config/download'];
  page.on('dialog', async (d) => {
    st.dialogs.push({ type: d.type(), message: d.message() });
    if (d.type() === 'prompt') st.prompts++;
    if (d.type() === 'confirm') st.confirms++;
    if (d.type() === 'alert') st.alerts++;
    try {
      if (d.type() === 'prompt') {
        if (opts.prompt === 'accept') {
          // password 支持数组（按弹窗顺序依次作答，用尽后重复最后一个），
          // 用于「第一次输错 → 401 → 重输正确」类用例
          let pw = opts.password || ADMIN_PW;
          if (Array.isArray(pw)) {
            if (!st.pwQueue) st.pwQueue = pw.slice();
            const next = st.pwQueue.shift();
            pw = next !== undefined ? next : pw[pw.length - 1];
          }
          await d.accept(pw);
        }
        else await d.dismiss();
      } else if (d.type() === 'confirm') {
        if (opts.confirm === 'accept') await d.accept();
        else await d.dismiss();
      } else {
        await d.accept(); // alert
      }
    } catch (e) { /* dialog 可能已自动关闭 */ }
  });
  page.on('pageerror', (e) => {
    // page.route mock 的 4xx 响应没有 CORS 头（fetch 跨源判定仅看 Access-Control-Allow-Origin、
    // 与同源 URL 无关），读 body 会抛 TypeError("Failed to fetch") → pageerror。
    // 属测试基建产物而非产品缺陷：仅当本用例启用了 route mock 时过滤。
    const msg = String(e && e.stack || e);
    if ((opts.route401 || opts.route403 || opts.routePendingMock) && /Failed to fetch|Load failed|TypeError/.test(msg)) return;
    st.pageErrors.push(msg);
  });
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const loc = (m.location() && m.location().url) || '';
    if (/favicon/i.test(m.text()) || /favicon/i.test(loc)) { st.faviconNoise++; return; }
    // 带上来源 URL，失败时能定位是哪个端点产生 console error
    st.consoleErrors.push(m.text() + (loc ? ' @ ' + loc : ''));
  });
  page.on('response', async (r) => {
    const url = r.url();
    if (url.includes('/favicon.ico')) { return; }
    if (r.status() >= 400) st.badResponses.push({ url, status: r.status() });
    for (const pat of (opts.countMethods || [])) {
      // pat 形如 "PUT /api/config/batch"（按 pathname 精确匹配，
      // 否则 '/api/config' 会误配 '/api/config/batch'）
      const [m0, sub] = pat.split(' ');
      let pn = '';
      try { pn = new URL(url).pathname; } catch (e) { pn = url; }
      if (r.request().method() === m0 && pn === sub) st.reqCounts[pat] = (st.reqCounts[pat] || 0) + 1;
    }
    if (!ALLOW_SECRET.some((a) => url.includes(a))) {
      try {
        const ct = r.headers()['content-type'] || '';
        if (ct.includes('json') || ct.includes('text')) {
          const t = await r.text().catch(() => '');
          if (SECRET_RE.test(t)) st.bodyHits.push({ url, status: r.status() });
        }
      } catch (e) { /* body 不可读 */ }
    }
  });
  st.expectClean = (allowBad = []) => {
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
    // console error（浏览器对任何 4xx/5xx 都会记一条 "Failed to load resource"）：
    // 允许名单按来源 URL 过滤（如 dispose/pending 在测试环境 DB 不可达时按设计回 500+错误信息）
    const badConsole = st.consoleErrors.filter((e) => !allowBad.some((a) => e.includes(a)));
    expect(badConsole, 'console error: ' + badConsole.join('\n')).toEqual([]);
    const unexpected = st.badResponses.filter((b) => !allowBad.some((a) => b.url.includes(a)));
    expect(unexpected, '未预期 4xx/5xx: ' + JSON.stringify(unexpected)).toEqual([]);
    expect(st.bodyHits, '响应出现明文密码: ' + JSON.stringify(st.bodyHits)).toEqual([]);
  };
  return st;
}

async function openTab(page, label) {
  await page.click(`.tab[data-tab="${label}"]`);
}

test.describe('UI-1 首页加载', () => {
  test('版本/状态可见，无 console error / pageerror', async ({ page }) => {
    const st = watch(page);
    await page.goto('/');
    await expect(page.locator('#appVersion')).not.toHaveText('v?', { timeout: 10_000 });
    const status = page.locator('#systemStatus');
    await expect(status).toBeVisible();
    await expect(status).not.toHaveText('检查中...');
    // 仪表盘健康卡片渲染
    await expect(page.locator('#healthCards').locator('.card').first()).toBeVisible({ timeout: 10_000 });
    // P3 修复后仪表盘不再预取丢弃队列（/api/dispose/pending）：不应出现任何 5xx/console 噪音
    st.expectClean();
  });
});

test.describe('UI-2 只读页面不弹密码', () => {
  test('错误日志 / 日志文件 / API 说明 tab 加载均无 dialog', async ({ page }) => {
    const st = watch(page);
    await page.goto('/');
    for (const tab of ['errors', 'logs', 'api']) {
      await openTab(page, tab);
      await page.waitForTimeout(800); // 等各自加载请求完成
    }
    expect(st.dialogs, '只读页面不应有任何 dialog: ' + JSON.stringify(st.dialogs)).toEqual([]);
    // 各 tab 内容有渲染（非白屏）
    await expect(page.locator('#apiBody tr').first()).toBeVisible();
    st.expectClean();
  });
});

test.describe('UI-3 丢弃队列 tab（宿主未就绪场景）', () => {
  test('加载 + 刷新：503 显示错误行而非白屏；无 JS error、无 dialog', async ({ page }) => {
    const st = watch(page, { countMethods: ['GET /api/dispose/pending'] });
    await page.goto('/');
    await openTab(page, 'dispose');
    // 503 → disposeError 显示；若无 DB（200）也应有行或空态
    await page.locator('#tab-dispose button:has-text("刷新")').click();
    await expect(page.locator('#disposeError')).toBeVisible({ timeout: 15_000 });
    const errText = await page.locator('#disposeError').textContent();
    expect(errText && errText.length, '应显示错误信息（如 业务宿主未就绪 / not ready）').toBeTruthy();
    expect(st.prompts, '队列加载/刷新不得弹密码').toBe(0);
    st.expectClean(['/api/dispose/pending']); // 503/500 属预期（无业务 DB，错误文案已显示），500 来源见 README 说明
    test.info().annotations.push({ type: 'dispose-error', description: (errText || '').trim() });
  });
});

test.describe('UI-4 功能配置', () => {
  test('测试连接按钮：不弹密码、显示结果+耗时、有「已保存配置」提示', async ({ page }) => {
    const st = watch(page, { countMethods: ['POST /api/config/test-db'] });
    await page.goto('/');
    await openTab(page, 'forms');
    await expect(page.locator('#formsBody .form-row').first()).toBeVisible();
    // 提示文案存在
    await expect(page.locator('.db-test-note', { hasText: '测试的是已保存配置' }).first()).toBeVisible();
    const btn = page.locator('#btnTestDb_aptio');
    await expect(btn).toBeVisible();
    await btn.click();
    await expect(page.locator('#dbTestOut_aptio')).toContainText(/连接成功|连接失败/, { timeout: 25_000 });
    await expect(page.locator('#dbTestOut_aptio')).toContainText('ms');
    expect(st.prompts, '测试连接（免鉴权）不得弹密码').toBe(0);
    expect(st.reqCounts['POST /api/config/test-db'] || 0).toBe(1);
    st.expectClean();
  });

  test('修改无害字段保存全部：恰弹一次密码、恰一次 batch 请求、成功提示、文件实际变更', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept', confirm: 'accept',
      countMethods: ['PUT /api/config/batch', 'PUT /api/config'],
    });
    await page.goto('/');
    await openTab(page, 'forms');
    const input = page.locator('.form-row', { has: page.locator('label[data-path="Aptio:DisposeSample:LoopIntervalSeconds"]') }).locator('input').first();
    await expect(input).toBeVisible();
    const current = await input.inputValue();
    const next = current === '7' ? '8' : '7';
    await input.fill(next);
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    await expect(page.locator('#formsHint')).toContainText('✅');
    expect(st.prompts, '一次保存全部应恰弹一次密码').toBe(1);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '应只发一次批量保存请求').toBe(1);
    expect(st.reqCounts['PUT /api/config'] || 0, '不应再走单字段保存').toBe(0);
    st.expectClean();
    // 服务端落盘验证（md5 变化 + 新值出现）
    const fileText = fs.readFileSync(APP_DIR + '/appsettings.json', 'utf8');
    expect(fileText).toMatch(new RegExp('"LoopIntervalSeconds":\\s*' + next));
  });

  // 快速填窗（2026-11-07 新增 3 项）：EnableFastFill（switch，代码默认 false）、
  // FastFillIntervalMs（number，max 5000）、FastFillConfirmIntervalSeconds（number，max 60）。
  // 默认值说明：基线 /tmp/uniflow-e2e/appsettings.test.json 不含这三个键，且 GET /api/config
  // （ConfigService.ReadAll）原样返回文件 JSON、不注入 C# 模型默认（ConfigShared.cs:93-98 的
  // =false/1000/5 只在配置绑定对象时生效）→ 表单实际渲染：checkbox 未勾选、两个 number 为空串。
  // 故按「实际渲染」断言空串 + max 属性 + hint 文案中的「默认 1000 / 默认 5」；若日后基线补入
  // 这三个键，本用例的值断言需随实际渲染调整。
  // 保存不落盘：page.route 按 pathname 精确拦截 PUT /api/config/batch（保留真实
  // GET /api/auth/challenge，密码弹窗真实发生），捕获请求体后直接 fulfill 200。
  test('快速填窗 3 个配置项可渲染/可保存：恰一次密码、batch 含新键（EnableFastFill）', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept',
      countMethods: ['PUT /api/config/batch', 'PUT /api/config'],
    });
    const batchBodies = [];
    await page.route((u) => u.pathname === '/api/config/batch', (route) => {
      if (route.request().method() !== 'PUT') return route.fallback();
      batchBodies.push(route.request().postData() || '');
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{"success":true}' });
    });
    await page.goto('/');
    await openTab(page, 'forms');
    await expect(page.locator('#formsBody .form-row').first()).toBeVisible();

    const row = (p) => page.locator('.form-row', { has: page.locator('label[data-path="' + p + '"]') });
    // ① EnableFastFill：switch 渲染为隐藏 checkbox + .switch-ui 外观（renderControl type==='switch'）。
    //    checkbox 本身 display:none → 外观断言 .switch-wrap/.switch-ui 可见、checkbox 存在且未勾选。
    const swRow = row('Aptio:DisposeSample:EnableFastFill');
    await expect(swRow.locator('label.switch-wrap')).toBeVisible();
    await expect(swRow.locator('.switch-ui')).toBeVisible();
    const sw = swRow.locator('input[type="checkbox"]');
    await expect(sw).toHaveCount(1);
    await expect(sw).not.toBeChecked(); // 默认关
    // ② FastFillIntervalMs：number + max 5000；③ FastFillConfirmIntervalSeconds：number + max 60
    const msInput = row('Aptio:DisposeSample:FastFillIntervalMs').locator('input[type="number"]');
    await expect(msInput).toBeVisible();
    await expect(msInput).toHaveAttribute('max', '5000');
    await expect(msInput).toHaveValue(''); // 基线无该键 → 空串渲染（见上方说明）
    await expect(row('Aptio:DisposeSample:FastFillIntervalMs').locator('.tip-icon'))
      .toHaveAttribute('data-tip', /默认 1000/); // hint 标明代码默认值
    const cfInput = row('Aptio:DisposeSample:FastFillConfirmIntervalSeconds').locator('input[type="number"]');
    await expect(cfInput).toBeVisible();
    await expect(cfInput).toHaveAttribute('max', '60');
    await expect(cfInput).toHaveValue('');
    await expect(row('Aptio:DisposeSample:FastFillConfirmIntervalSeconds').locator('.tip-icon'))
      .toHaveAttribute('data-tip', /默认 5/);

    // 修改：勾选开关 + 间隔 1500 + 批量确认 10，然后保存全部
    await swRow.locator('.switch-ui').click(); // label.switch-wrap 包裹 checkbox，点外观即切换
    await expect(sw).toBeChecked();
    await msInput.fill('1500');
    await cfInput.fill('10');
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    await expect(page.locator('#formsHint')).toContainText('✅');

    expect(st.prompts, '一次保存全部应恰弹一次密码').toBe(1);
    expect(batchBodies.length, '应恰一次 PUT /api/config/batch（mock 捕获）').toBe(1);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '应只发一次批量保存请求').toBe(1);
    expect(st.reqCounts['PUT /api/config'] || 0, '不应再走单字段保存').toBe(0);
    // 请求体：changes 含三个新键且值正确（开关=true；number 经 parseInt 为数字）
    const changes = JSON.parse(batchBodies[0]).changes;
    const valOf = (p) => (changes.find((c) => c.path === p) || {}).value;
    expect(valOf('Aptio:DisposeSample:EnableFastFill'), 'EnableFastFill 应为 true').toBe(true);
    expect(valOf('Aptio:DisposeSample:FastFillIntervalMs'), 'FastFillIntervalMs 应为 1500').toBe(1500);
    expect(valOf('Aptio:DisposeSample:FastFillConfirmIntervalSeconds'), 'ConfirmIntervalSeconds 应为 10').toBe(10);
    st.expectClean();
  });

  test('WebAdmin:AdminPassword 不可在表单编辑（无输入控件），仅有后台修改说明', async ({ page }) => {
    const st = watch(page);
    await page.goto('/');
    await openTab(page, 'forms');
    await expect(page.locator('#formsBody .form-row').first()).toBeVisible();
    expect(await page.locator('label[data-path="WebAdmin:AdminPassword"]').count()).toBe(0);
    expect(await page.locator('#formsBody label[data-path="WebAdmin:AdminPassword"] ~ input, label[data-path="WebAdmin:AdminPassword"] input').count(),
      '管理密码不应有任何输入控件').toBe(0);
    await expect(page.locator('#formsBody')).toContainText('管理密码（WebAdmin:AdminPassword）在后台 appsettings.json 修改');
    st.expectClean();
  });

  test('上传升级包：确认+弹一次密码；取消密码则不发任何上传请求', async ({ page }) => {
    const st = watch(page, { confirm: 'accept', countMethods: ['POST /api/update/upload'] });
    await page.goto('/');
    await openTab(page, 'forms');
    await page.setInputFiles('#updateFile', {
      name: 'e2e-dummy.zip',
      mimeType: 'application/zip',
      buffer: Buffer.from('PK\x05\x06' + '\0'.repeat(18)), // 空 zip EOCD
    });
    await expect.poll(() => st.prompts, { timeout: 8000 }).toBe(1);
    // 上面 watch 默认 dismiss prompt（=取消密码）→ 不应发出上传
    await page.waitForTimeout(1500);
    expect(st.reqCounts['POST /api/update/upload'] || 0, '取消密码后不得发出上传请求').toBe(0);
  });

  test('清除所有错误：confirm+弹一次密码；取消密码则不发 DELETE', async ({ page }) => {
    const st = watch(page, { confirm: 'accept', countMethods: ['DELETE /api/errors'] });
    await page.goto('/');
    await openTab(page, 'errors');
    await page.locator('#tab-errors button:has-text("清除所有错误")').click();
    await expect.poll(() => st.prompts, { timeout: 8000 }).toBe(1);
    await page.waitForTimeout(1000);
    expect(st.confirms).toBe(1);
    expect(st.reqCounts['DELETE /api/errors'] || 0, '取消密码后不得发出 DELETE').toBe(0);
  });
});

test.describe('UI-5 下载配置原文', () => {
  test('恰弹一次密码 → 触发下载 appsettings-*.json，内容为含明文密码的原文（按设计）', async ({ page }) => {
    const st = watch(page, { prompt: 'accept' });
    await page.goto('/');
    await openTab(page, 'forms');
    const dlPromise = page.waitForEvent('download', { timeout: 20_000 });
    await page.locator('button:has-text("下载配置文件")').click();
    const dl = await dlPromise;
    expect(dl.suggestedFilename()).toMatch(/^appsettings-\d{14}\.json$/);
    const out = '/tmp/uniflow-e2e/dl-' + Date.now() + '.json';
    await dl.saveAs(out);
    const text = fs.readFileSync(out, 'utf8');
    expect(text).toContain('AdminPassword');
    expect(text).toContain('Advia1'); // 按设计：下载为完整原文
    expect(st.prompts, '一次下载应恰弹一次密码').toBe(1);
    await expect(page.locator('#formsHint')).toContainText('已下载');
    st.expectClean();
  });
});

test.describe('UI-7 队列批量动作无选择时不弹密码（不发请求）', () => {
  test('批量重置/清空/重新检查：未勾选 → alert 引导，prompt=0、请求=0', async ({ page }) => {
    const st = watch(page, { confirm: 'accept', countMethods: [
      'POST /api/dispose/pending/reset',
      'POST /api/dispose/pending/delete',
      'POST /api/dispose/pending/recheck',
    ] });
    await page.goto('/');
    await openTab(page, 'dispose');
    for (const label of ['批量重置', '批量清空', '批量重新检查']) {
      await page.locator('#tab-dispose button:has-text("' + label + '")').click();
      await page.waitForTimeout(600);
    }
    expect(st.alerts, '未勾选记录应 alert 引导').toBeGreaterThanOrEqual(3);
    expect(st.prompts, '无数据动作不应弹密码').toBe(0);
    expect(st.confirms, '未勾选时不应走到 confirm').toBe(0);
    expect(st.reqCounts['POST /api/dispose/pending/reset'] || 0).toBe(0);
    expect(st.reqCounts['POST /api/dispose/pending/delete'] || 0).toBe(0);
    expect(st.reqCounts['POST /api/dispose/pending/recheck'] || 0).toBe(0);
    st.expectClean(['/api/dispose/pending']);
  });
  // 说明：勾选真实记录后的 reset/delete/recheck 全流程需要 MySQL sam_dispose_status 数据，
  // 本机无 Aptio 业务 DB → SKIP（见 README「已知限制」）。
});

// UI-6 重启服务：放最后，避免影响其它 UI 用例。
test.describe('UI-6 重启服务（最后执行）', () => {
  test('confirm+恰弹一次密码 → POST /api/restart 202，页面显示重建完成', async ({ page }) => {
    const st = watch(page, { prompt: 'accept', confirm: 'accept' });
    await page.goto('/');
    await openTab(page, 'forms');
    const respPromise = page.waitForResponse(
      (r) => r.url().endsWith('/api/restart') && r.request().method() === 'POST', { timeout: 15_000 });
    await page.locator('#restartBtn').click();
    const resp = await respPromise;
    expect(resp.status(), '重启请求不应 401/4xx').toBe(202);
    expect(st.confirms, '恰一次二次确认').toBe(1);
    expect(st.prompts, '恰弹一次密码').toBe(1);
    await expect(page.locator('#formsHint')).toContainText('已重建', { timeout: 60_000 });
    // 重启后 Web 未中断
    expect((await page.request.get('/api/version')).status()).toBe(200);
    st.expectClean();
  });
});

// UI-8 P2/P3 回归（XSS 消毒 / 下载 401 重试与 403 文案 / 上传 403 不重传 / 取消密码文案 / favicon）
// 队列数据用 page.route mock：业务 DB 不可达环境下也可稳定回归渲染与消毒逻辑。
test.describe('UI-8 XSS 消毒与鉴权交互回归', () => {
  // 1. 存储型 XSS：恶意 barcode/location 必须按文本显示、不执行脚本、行内无 onclick 属性
  test('dispose 队列恶意条码按文本显示，不执行脚本（P2 XSS）', async ({ page }) => {
    const XSS = '"><img src=x onerror=alert(1)>';
    const st = watch(page, { routePendingMock: true, confirm: 'accept' });
    let alertFired = 0;
    page.on('dialog', async (d) => { if (d.message().includes('XSS')) alertFired++; await d.dismiss().catch(() => {}); });
    await page.route('**/api/dispose/pending**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        minResend: 24, count: 1, pendingSend: 1, pendingConfirm: 0, failedTotal: 0,
        maxResendCount: 24, resendIntervalMinutes: 5, windowSize: 10, windowFull: false,
        items: [{
          barcode: XSS, location: "';alert(2);'", send: 3, resendCount: 24, checkcount: 1,
          sendTime: '2026-11-07 00:00:00',
        }],
      }),
    }));
    await page.goto('/');
    await openTab(page, 'dispose');
    const firstRow = page.locator('#disposeBody tr').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    // 条码/位置按文本显示（textContent 含原始字面量）
    await expect(page.locator('#disposeBody td').nth(1)).toHaveText(XSS);
    await expect(page.locator('#disposeBody td').nth(2)).toContainText("';alert(2);'");
    // 恶意内容未生成 DOM 元素（img 注入不存在）
    expect(await page.locator('#disposeBody img').count()).toBe(0);
    // 行内操作按钮不得再有内联 onclick（事件委托 + data-act）
    expect(await page.locator('#disposeBody [onclick]').count()).toBe(0);
    expect(await page.locator('#disposeBody button.dispose-act[data-act]').count()).toBeGreaterThanOrEqual(3);
    // 点击委托：条码原样进入 recheck 请求体（未被转义破坏、未被截断）
    const bodyPromise = page.waitForRequest(
      (rq) => rq.url().includes('/api/dispose/pending/recheck') && rq.method() === 'POST', { timeout: 8_000 });
    await page.locator('#disposeBody button.dispose-act[data-act="recheck"]').first().click();
    const rq = await bodyPromise;
    expect(JSON.parse(rq.postData() || '{}').barcodes).toEqual([XSS]);
    expect(alertFired, '恶意条码不得触发 alert').toBe(0);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
  });

  // 2. 下载配置：401 → 重新输入后重试一次成功；再 401 → 明确提示；取消 → 已取消
  test('下载配置 401：提示重输并重试一次；重输仍错明确提示；取消=已取消（P2）', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept', password: ['WrongPw1!', ADMIN_PW], route401: true,
      countMethods: ['GET /api/config/download', 'GET /api/auth/challenge'],
    });
    // 用 predicate 精确匹配 pathname（glob 对无 query 的完整 URL 匹配不可靠）：
    // 第一次强制 401（mock，无 CORS 头 → watch 按 route401 过滤其 pageerror 产物）；
    // 之后 route.fallback 透传真实后端：重输正确密码 → 200；重输仍错 → 真实 401。
    let dlTries = 0;
    await page.route((u) => u.pathname === '/api/config/download', (route) => {
      dlTries++;
      if (dlTries === 1) return route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ error: 'admin auth failed' }) });
      return route.fallback();
    });
    await page.goto('/');
    await openTab(page, 'forms');
    const dlPromise = page.waitForEvent('download', { timeout: 15_000 });
    await page.locator('button:has-text("下载配置文件")').click();
    const dl = await dlPromise; // 第二次尝试（真实密码）成功触发下载
    await dl.saveAs('/tmp/uniflow-e2e/ui8-dl-' + Date.now() + '.json');
    await expect(page.locator('#formsHint')).toContainText('已下载', { timeout: 10_000 });
    expect(st.prompts, '初始 1 次 + 401 后重输 1 次 = 恰 2 次').toBe(2);
    expect(st.reqCounts['GET /api/config/download'] || 0, '401 后仅重试一次（共 2 请求）').toBe(2);
    expect(st.dialogs.some((d) => d.type === 'prompt' && d.message.includes('管理密码错误或挑战过期')),
      '401 应提示重新输入').toBeTruthy();

    // 重输仍错 → 第二次 401：明确提示且不再重试
    st.prompts = 0; st.dialogs.length = 0;
    st.pwQueue = ['WrongPw1!', 'WrongPw2!'];   // 本轮两次弹窗都答错
    st.reqCounts['GET /api/config/download'] = 0; st.reqCounts['GET /api/auth/challenge'] = 0;
    await page.locator('button:has-text("下载配置文件")').click();
    await expect(page.locator('#formsHint')).toContainText('管理密码错误或挑战过期', { timeout: 10_000 });
    expect(st.prompts, '初始 + 重输 = 2 弹').toBe(2);
    expect(st.reqCounts['GET /api/config/download'] || 0, '重试仅一次').toBe(2);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '每次尝试恰取一个新挑战').toBe(2);

    // 初始弹窗就取消 → 已取消，未下载，0 下载请求
    st.reqCounts['GET /api/config/download'] = 0;
    page.removeAllListeners('dialog');
    page.on('dialog', async (d) => { st.prompts++; await d.dismiss().catch(() => {}); });
    await page.locator('button:has-text("下载配置文件")').click();
    await expect(page.locator('#formsHint')).toContainText('已取消，未下载', { timeout: 10_000 });
    expect(st.reqCounts['GET /api/config/download'] || 0, '取消后不得发下载请求').toBe(0);
  });

  // 3. 下载配置：403（未配置管理密码）→ 明确文案、不重试不弹重输
  test('下载配置 403：提示未配置管理密码、不重试（P2）', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept', route403: true,
      countMethods: ['GET /api/config/download', 'GET /api/auth/challenge'],
    });
    await page.route((u) => u.pathname === '/api/config/download', (route) => route.fulfill({
      status: 403, contentType: 'application/json', body: JSON.stringify({ error: 'Admin password not configured' }),
    }));
    await page.goto('/');
    await openTab(page, 'forms');
    await page.locator('button:has-text("下载配置文件")').click();
    await expect(page.locator('#formsHint')).toContainText('未配置管理密码', { timeout: 10_000 });
    await expect(page.locator('#formsHint')).toContainText('WebAdmin:AdminPassword');
    expect(st.prompts, '403 不应弹重输（仅初始 1 次）').toBe(1);
    expect(st.reqCounts['GET /api/config/download'] || 0, '403 不得重试').toBe(1);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0).toBe(1);
  });

  // 4. 取消密码文案：重启/清除错误取消 → 「已取消，未执行」+ 0 请求；保存全部取消 → 「已取消，未保存」
  test('取消管理密码：文案「已取消」且不发任何执行请求（P2 文案错位修复）', async ({ page }) => {
    const st = watch(page, {
      confirm: 'accept', countMethods: [
        'POST /api/restart', 'DELETE /api/errors',
        'PUT /api/config/batch', 'GET /api/config/download', 'GET /api/auth/challenge',
      ],
    });
    await page.goto('/');
    await openTab(page, 'forms');
    // 重启服务（初始弹窗取消）
    await page.locator('#restartBtn').click();
    await expect(page.locator('#formsHint')).toContainText('已取消，未执行', { timeout: 8_000 });
    // 清除所有错误（错误 tab）：confirm=accept、密码弹窗取消 → 静默中止、不发 DELETE
    await openTab(page, 'errors');
    await page.locator('#tab-errors button:has-text("清除所有错误")').click();
    await expect.poll(() => st.prompts, { timeout: 8_000 }).toBe(2);
    // 保存全部（修改一个字段后取消密码）
    await openTab(page, 'forms');
    const input = page.locator('.form-row', { has: page.locator('label[data-path="Aptio:DisposeSample:LoopIntervalSeconds"]') }).locator('input').first();
    await expect(input).toBeVisible();
    const current = await input.inputValue();
    await input.fill(current === '7' ? '8' : '7');
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已取消，未保存', { timeout: 8_000 });
    // 下载配置（取消）
    await page.locator('button:has-text("下载配置文件")').click();
    await expect(page.locator('#formsHint')).toContainText('已取消，未下载', { timeout: 8_000 });
    expect(st.prompts, '四个动作各弹一次密码').toBe(4);
    expect(st.confirms, '重启+清除各一次 confirm').toBe(2);
    expect(st.reqCounts['POST /api/restart'] || 0, '取消后不得发重启').toBe(0);
    expect(st.reqCounts['DELETE /api/errors'] || 0, '取消后不得发清除').toBe(0);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '取消后不得发保存').toBe(0);
    expect(st.reqCounts['GET /api/config/download'] || 0, '取消后不得发下载').toBe(0);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '取消后连挑战都不取').toBe(0);
    expect(st.dialogs.every((d) => !(d.type === 'alert' && d.message.includes('密码错误'))),
      '取消不得显示「密码错误」: ' + JSON.stringify(st.dialogs)).toBeTruthy();
  });

  // 5. 上传升级包 403（未配置管理密码）：显示后端错误并停止，不整包重传
  test('上传 403：显示未配置管理密码并停止，不重传（P2）', async ({ page }) => {
    const st = watch(page, { prompt: 'accept', confirm: 'accept', route403: true, countMethods: ['POST /api/update/upload'] });
    await page.route((u) => u.pathname === '/api/update/upload', (route) => route.fulfill({
      status: 403, contentType: 'application/json', body: JSON.stringify({ error: 'Admin password not configured' }),
    }));
    await page.goto('/');
    await openTab(page, 'forms');
    await page.setInputFiles('#updateFile', {
      name: 'e2e-dummy.zip', mimeType: 'application/zip',
      buffer: Buffer.from('PK\x05\x06' + '\0'.repeat(18)),
    });
    await expect(page.locator('#formsHint')).toContainText('未配置管理密码', { timeout: 12_000 });
    expect(st.prompts, '403 不弹重输（仅初始 1 次）').toBe(1);
    expect(st.reqCounts['POST /api/update/upload'] || 0, '403 不得整包重传').toBe(1);
  });

  // 6. favicon：head 内联空 data URI，网络层不再出现 /favicon.ico 404
  test('favicon 不再 404（P3）', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('#appVersion')).not.toHaveText('v?', { timeout: 10_000 });
    const href = await page.locator('link[rel="icon"]').getAttribute('href');
    expect(href).toBe('data:,');
    const faviconReqs = await page.evaluate(() =>
      performance.getEntriesByType('resource').filter((e) => /favicon/i.test(e.name)).map((e) => e.name));
    expect(faviconReqs, '不应再请求 /favicon.ico').toEqual([]);
  });
});

// UI-9 M-07 XSS 引号/属性注入 + 健康/错误消息的独立红闸门（维护单 2026-11-07 M-07，P3）
// 现有 UI-8 只覆盖 dispose 队列的「标签注入」；本组补齐三类此前无独立回归的攻击面，
// 并保证以下三类产品回退都能让用例变红：
//   ① 去掉/削弱 escapeHtml（& < > " ' 完整转义） → <img onerror> 注入执行；
//   ② 去掉引号转义 / 类名白名单过滤（class="level-*" / class="card *" 属性逃逸）；
//   ③ 把行内操作按钮的事件委托退回内联 onclick（可被条码在 JS 字符串里逃逸）；
//   ④ 去掉 escapeHtml 的**引号**转义 → 属性上下文（app.js:481 startEdit 的 value="..."、
//      以及本组第 6 条直接拼进 value/class 的 innerHTML）属性被提前闭合、逃逸出 on* 属性。
//      （UI-1~8 与本组 1~3 条的 payload 都在文本上下文里，去掉引号转义仍是绿的 —— 本组第 5、6 条专补这个缺口。）
// 全部用 page.route + fulfill mock（pathname predicate），不依赖真实 DB/config；基线 Features 全 false。
test.describe('UI-9 M-07 XSS 引号/属性注入与消息转义红闸门', () => {
  // 统计 body 内所有以 on 开头的「内联事件属性」（onclick/onerror/onmouseover/onfocus...）。
  // 注意：JS 里 el.onclick=fn（属性赋值）不会生成 HTML on* 属性，只匹配真实注入的字符串属性。
  const inlineEventAttrCount = (page, rootSel) => page.evaluate((sel) => {
    let n = 0;
    document.querySelectorAll(sel + ', ' + sel + ' *').forEach((el) => {
      for (const a of el.attributes) if (/^on/i.test(a.name)) n++;
    });
    return n;
  }, rootSel);

  // 1. dispose 队列「属性/引号注入」payload：按文本显示、无 alert、无 pageerror、
  //    DOM 无新增事件属性/img/script，且行内操作仍走事件委托（回退内联 onclick 即变红）。
  test('dispose 队列属性/引号注入按文本显示，行内仍走事件委托（M-07）', async ({ page }) => {
    const BC = 'BC" onmouseover="alert(1)';
    const LOC = "LOC' onfocus='alert(2)";
    const st = watch(page, { routePendingMock: true });
    let alertFired = 0;
    page.on('dialog', async (d) => { if (d.message().includes('alert')) alertFired++; await d.dismiss().catch(() => {}); });
    await page.route('**/api/dispose/pending**', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        minResend: 24, count: 1, pendingSend: 1, pendingConfirm: 0, failedTotal: 0,
        maxResendCount: 24, resendIntervalMinutes: 5, windowSize: 10, windowFull: false,
        items: [{
          barcode: BC, location: LOC, send: 3, resendCount: '9" onmouseover="alert(3)',
          checkcount: "1' onfocus='alert(4)", sendTime: '2026-11-07 00:00:00',
        }],
      }),
    }));
    await page.goto('/');
    await openTab(page, 'dispose');
    const firstRow = page.locator('#disposeBody tr').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    // 条码/位置/重排队次数/检查次数（全部 escapeHtml）按原始字面量文本显示
    await expect(page.locator('#disposeBody td').nth(1)).toHaveText(BC);
    await expect(page.locator('#disposeBody td').nth(2)).toHaveText(LOC);
    await expect(page.locator('#disposeBody td').nth(4)).toContainText('9" onmouseover="alert(3)');
    await expect(page.locator('#disposeBody td').nth(5)).toContainText("1' onfocus='alert(4)");
    // payload 未生成任何 DOM 元素/事件属性
    expect(await page.locator('#disposeBody img').count(), '条码属性注入不得生成 img').toBe(0);
    expect(await page.locator('#disposeBody script').count(), '不得生成 script').toBe(0);
    expect(await inlineEventAttrCount(page, '#disposeBody'), 'disposeBody 不得出现任何 on* 事件属性').toBe(0);
    // 事件委托未被退回内联 onclick：无任何 [onclick]，操作按钮带 data-act；
    // 委托按 data-i 取回原始条码进 recheck 请求体（不被 HTML/JS 转义截断）
    expect(await page.locator('#disposeBody [onclick]').count(), '行内不得有内联 onclick 属性').toBe(0);
    expect(await page.locator('#disposeBody button.dispose-act[data-act]').count()).toBeGreaterThanOrEqual(3);
    const bodyPromise = page.waitForRequest(
      (rq) => rq.url().includes('/api/dispose/pending/recheck') && rq.method() === 'POST', { timeout: 8_000 });
    await page.locator('#disposeBody button.dispose-act[data-act="recheck"]').first().click();
    const rq = await bodyPromise;
    expect(JSON.parse(rq.postData() || '{}').barcodes, '委托须原样取回带引号的条码').toEqual([BC]);
    expect(alertFired, '属性注入 payload 不得触发 alert').toBe(0);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
  });

  // 2. 健康卡片（/api/health）message 含 HTML + 引号 payload：按文本显示、无脚本执行。
  //    message 所在 status 用 'degraded'：此时第 211 行的「功能未启用」覆盖分支不触发，
  //    即使基线 Features 全 false，mock 的 message 也会原样进入卡片（无需 mock /api/config）。
  test('健康卡片 message 含 HTML/引号按文本显示，无脚本执行（M-07）', async ({ page }) => {
    const MSG = '<img src=x onerror=alert(1)> "q1" \'q2\'';
    const st = watch(page);
    let alertFired = 0;
    page.on('dialog', async (d) => { if (d.message().includes('alert')) alertFired++; await d.dismiss().catch(() => {}); });
    await page.route((u) => u.pathname === '/api/health', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        status: 'degraded',
        modules: { DisposeSample: { status: 'degraded', timestamp: '2026-11-07 10:00:00', message: MSG } },
        summary: { healthy: 0, degraded: 1, down: 0 },
      }),
    }));
    await page.goto('/');
    // 定位显示该 payload 的卡片（DisposeSample 卡片，meta 含模块名 + payload 文本）
    const card = page.locator('#healthCards .card', { hasText: MSG }).first();
    await expect(card).toBeVisible({ timeout: 10_000 });
    // payload 作为纯文本显示（含原始 < > " ' 字面量）
    await expect(card.locator('.meta')).toContainText(MSG);
    // 未生成任何 DOM 元素/事件属性（escapeHtml 生效）
    expect(await page.locator('#healthCards img').count(), 'message 不得生成 img').toBe(0);
    expect(await page.locator('#healthCards script').count(), 'message 不得生成 script').toBe(0);
    expect(await inlineEventAttrCount(page, '#healthCards'), 'healthCards 不得出现任何 on* 事件属性').toBe(0);
    expect(alertFired, 'message payload 不得触发 alert').toBe(0);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
  });

  // 3. 错误列表（/api/errors）module/level/timestamp/message 含 HTML + 引号 payload：
  //    message/module/timestamp 经 escapeHtml 按文本显示；level 经白名单过滤进 class 属性，
  //    若去掉 class 过滤/引号转义，level 的 x" onmouseover="..." 会逃逸成属性并被检测到（红闸门）。
  test('错误列表 module/level/timestamp/message 含 HTML/引号按文本显示，不逃逸属性（M-07）', async ({ page }) => {
    const MSG = '<img src=x onerror=alert(1)> "e"';
    const TS = '2026-11-07"T" onmouseover="alert(5)';
    const MOD = 'Mod" onmouseover="alert(6)';
    const LVL = 'x" onmouseover="alert(7)';
    const st = watch(page);
    let alertFired = 0;
    page.on('dialog', async (d) => { if (d.message().includes('alert')) alertFired++; await d.dismiss().catch(() => {}); });
    await page.route((u) => u.pathname === '/api/errors', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([
        { id: 1, timestamp: TS, module: MOD, level: 'ERROR', message: MSG },
        { id: 2, timestamp: '2026-11-07 10:00:00', module: 'DisposeSample', level: LVL, message: 'plain message' },
      ]),
    }));
    await page.route((u) => u.pathname === '/api/errors/summary', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ byModule: [{ module: 'Summary" onmouseover="alert(8)', count: 2 }] }),
    }));
    await page.goto('/');
    await openTab(page, 'errors');
    const firstRow = page.locator('#errorBody tr').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    // message / module / timestamp（全部 escapeHtml）按原始字面量文本显示
    await expect(page.locator('#errorBody td').nth(0)).toHaveText(TS);
    await expect(page.locator('#errorBody td').nth(1)).toHaveText(MOD);
    await expect(page.locator('#errorBody td').nth(3)).toContainText(MSG);
    // 任何错误单元格都没有把 payload 变成真实元素/事件属性
    expect(await page.locator('#errorBody img').count(), '错误消息不得生成 img').toBe(0);
    expect(await page.locator('#errorBody script').count(), '错误消息不得生成 script').toBe(0);
    expect(await inlineEventAttrCount(page, '#errorTable'), 'errorTable 不得出现任何 on* 事件属性').toBe(0);
    // 级别白名单过滤：带 class 的单元格 class 必须整体为 ^level-[A-Za-z]*$
    // （payload 里的引号/空格/等号被过滤掉；若去掉过滤，class 会变成 level-x" onmouseover="..."
    //  → 既违反该正则、又新增 onmouseover 事件属性，两处都会让本用例变红）
    const badLevelClass = await page.evaluate(() => {
      let n = 0;
      document.querySelectorAll('#errorBody td').forEach((td) => {
        const c = td.getAttribute('class');
        if (c === null) return;                 // 无 class 的单元格（ts/module/message）跳过
        if (!/^level-[A-Za-z]*$/.test(c)) n++;  // 含引号/空格/等号即视为逃逸
      });
      return n;
    });
    expect(badLevelClass, '级别 class 必须是纯 level-<字母>，不得属性逃逸').toBe(0);
    // 错误摘要（summary.byModule 亦 escapeHtml）按文本显示
    const summary = page.locator('#errorSummary');
    await expect(summary).toContainText('Summary" onmouseover="alert(8)');
    expect(await summary.locator('img').count(), '摘要不得生成 img').toBe(0);
    expect(await inlineEventAttrCount(page, '#errorSummary'), '摘要不得出现 on* 事件属性').toBe(0);
    expect(alertFired, '错误 payload 不得触发 alert').toBe(0);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
  });

  // 4. 多事件类型属性注入（onerror/onload/onmouseover）经事件属性计数断言捕获：
  //    去掉 escapeHtml 的标签转义 → <img>/<svg> 生成并带 on* 属性 → 计数 > 0 → 红。
  test('多事件类型标签注入（onerror/onload/onmouseover）被 escapeHtml 全部拦截（M-07）', async ({ page }) => {
    const MSGS = ['<img src=x onerror=alert(1)>', '<svg onload=alert(2)>', '<a onmouseover="alert(3)">m</a>'];
    const st = watch(page);
    let alertFired = 0;
    page.on('dialog', async (d) => { if (d.message().includes('alert')) alertFired++; await d.dismiss().catch(() => {}); });
    await page.route((u) => u.pathname === '/api/errors', (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify(MSGS.map((m, i) => ({ id: i + 1, timestamp: 't', module: 'm', level: 'ERROR', message: m }))),
    }));
    await page.route((u) => u.pathname === '/api/errors/summary', (route) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ byModule: [] }),
    }));
    await page.goto('/');
    await openTab(page, 'errors');
    await expect(page.locator('#errorBody tr').first()).toBeVisible({ timeout: 10_000 });
    expect(await page.locator('#errorBody tr').count(), '三条错误都要渲染').toBe(3);
    // 三条 payload 都按文本显示（escapeHtml 生效）
    for (const m of MSGS) await expect(page.locator('#errorBody')).toContainText(m);
    // 无任何注入元素、无任何 on* 事件属性
    expect(await page.locator('#errorBody img').count(), '<img> 不得被执行').toBe(0);
    expect(await page.locator('#errorBody svg').count(), '<svg> 不得被执行').toBe(0);
    expect(await page.locator('#errorBody a').count(), '<a> 不得被执行').toBe(0);
    expect(await inlineEventAttrCount(page, '#errorTable'), 'errorTable 不得出现任何 on* 事件属性').toBe(0);
    expect(alertFired, '多事件 payload 不得触发 alert').toBe(0);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
  });

  // 5. **真正的属性上下文**（此前 UI-9 的缺口）：payload 进 `value="..."` 属性内部。
  //    产品里唯一的属性拼接点 = app.js:481 `startEdit()`：
  //        html = '<input type="text" value="' + escapeHtml(...) + '">'
  //    （「配置管理」页签已从 index.html 移除（app.js:358-360 configTree 容器不存在），
  //      表单 renderControl 走 DOM 属性赋值 app.js:1055 天然安全 → 无法靠点 UI 触发属性逃逸，
  //      故按工单允许的方式：/api/config mock 让 payload 经真实渲染链进表单 + 页面内直接调用
  //      全局 startEdit（app.js:464，等价于 renderConfigNode:449 的调用），断言属性上下文安全。）
  //    回退「去掉 escapeHtml 的引号转义」时本用例必红：value 属性被 " 提前闭合
  //    → input 多出 onmouseover/onfocus 属性、或生成 <img onerror> 元素、value 原文被截断。
  test('配置编辑态 value="..." 属性上下文：引号/标签注入不得逃逸（M-07）', async ({ page }) => {
    const PAYLOADS = ['x" onmouseover="alert(1)', "y' onfocus='alert(2)", '"><img src=x onerror=alert(3)>'];
    const FIELD_PATH = 'Aptio:DisposeSample:CommandName';   // FORM_FIELDS 里的 text 字段
    const FIELD_ID = 'f_Aptio_DisposeSample_CommandName';
    const st = watch(page, { countMethods: ['GET /api/config'] });
    let alertFired = 0;
    page.on('dialog', async (d) => {
      if (d.type() === 'alert') alertFired++;
      await d.dismiss().catch(() => {});
    });
    // 在真实 /api/config 响应上打补丁（保留原结构/脱敏，避免整包 mock 打崩 renderForms）
    await page.route((u) => u.pathname === '/api/config', async (route) => {
      const resp = await route.fetch();
      let json;
      try { json = await resp.json(); } catch (e) { json = {}; }
      const parts = FIELD_PATH.split(':');
      let o = json;
      for (let i = 0; i < parts.length - 1; i++) {
        if (typeof o[parts[i]] !== 'object' || o[parts[i]] === null) o[parts[i]] = {};
        o = o[parts[i]];
      }
      o[parts[parts.length - 1]] = PAYLOADS[0];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(json) });
    });
    await page.goto('/');
    await openTab(page, 'forms');
    // (a) mock 生效：payload 走真实渲染链进表单输入框（renderControl 用 DOM 属性赋值 → 原文回显）
    const formInput = page.locator('#' + FIELD_ID);
    await expect(formInput).toBeVisible({ timeout: 10_000 });
    await expect(formInput).toHaveValue(PAYLOADS[0], { timeout: 10_000 });
    expect(await formInput.evaluate((el) =>
      Array.from(el.attributes).map((a) => a.name.toLowerCase()).filter((n) => /^on/i.test(n))),
      '表单输入框不得出现 on* 属性').toEqual([]);
    // (b) 属性上下文主断言：按 renderConfigNode 的方式对每个 payload 进入 startEdit 编辑态
    const res = await page.evaluate(({ payloads, fieldPath }) => {
      const out = payloads.map((p) => {
        const host = document.createElement('div');
        host.className = 'm07b-host';
        document.body.appendChild(host);
        const span = document.createElement('span');
        span.className = 'config-string';
        span.textContent = '"' + p + '"';            // 与 app.js:448 的叶子节点渲染一致
        host.appendChild(span);
        startEdit(span, fieldPath, p);               // app.js:464 → 内部 escapeHtml + value="..."
        const input = host.querySelector('input');
        const bad = [];                              // 非产品自有的 on* 属性（注入产物）
        host.querySelectorAll('*').forEach((el) => {
          for (const a of el.attributes) {
            const t = el.tagName.toLowerCase() + '@' + a.name.toLowerCase();
            if (/^on/i.test(a.name) && t !== 'button@onclick') bad.push(t + '=' + a.value);
          }
        });
        return {
          escaped: escapeHtml(p),                    // app.js:558（页面全局函数）
          inputValue: input ? input.value : null,    // DOM 属性值（应等于原文）
          attrValue: input ? input.getAttribute('value') : null,
          attrNames: input ? Array.from(input.attributes).map((a) => a.name.toLowerCase()) : null,
          injectedAttrs: bad,
          img: host.querySelectorAll('img').length,
          script: host.querySelectorAll('script').length,
        };
      });
      document.querySelectorAll('.m07b-host').forEach((h) => h.remove());
      return out;
    }, { payloads: PAYLOADS, fieldPath: FIELD_PATH });
    expect(res.length, '三个 payload 都要跑到编辑态').toBe(3);
    for (let i = 0; i < res.length; i++) {
      const r = res[i]; const p = PAYLOADS[i];
      // 引号必须转义：escapeHtml 输出内不得残留裸 " '（否则属性值可被提前闭合）
      expect(r.escaped, 'escapeHtml 必须转义引号（payload #' + (i + 1) + '）').not.toContain('"');
      expect(r.escaped).not.toContain("'");
      // 按原文显示：属性值与 DOM value 都等于原始字符串（未被引号截断）
      expect(r.attrValue, 'value 属性必须原样承载 payload（未逃逸）#' + (i + 1)).toBe(p);
      expect(r.inputValue, '输入框按原文显示 payload #' + (i + 1)).toBe(p);
      // 没有新增属性：input 只有 type + value；且全宿主无注入型 on* 属性
      expect(r.attrNames, 'input 只应有 type/value 两个属性 #' + (i + 1)).toEqual(['type', 'value']);
      expect(r.injectedAttrs, '编辑态不得出现注入的 on* 属性 #' + (i + 1)).toEqual([]);
      // 没有新增元素
      expect(r.img, 'payload 不得生成 img #' + (i + 1)).toBe(0);
      expect(r.script, 'payload 不得生成 script #' + (i + 1)).toBe(0);
    }
    await page.waitForTimeout(600);   // 等 onerror/onfocus 这类异步事件冒出来
    expect(alertFired, '属性注入 payload 不得触发 alert').toBe(0);
    expect(st.prompts, '进入编辑态不得弹密码').toBe(0);
    expect(st.reqCounts['GET /api/config'] || 0).toBeGreaterThanOrEqual(1);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
    st.expectClean();
  });

  // 6. escapeHtml 的最小「属性上下文」闸门（不依赖任何 UI 结构，纯函数 + innerHTML 复检）：
  //    ① 输出必须把 & < > " ' 五个字符全转义（去掉引号转义 → 断言 2/3 立即红）；
  //    ② 把结果拼进 value="..." 与 class="..." 两种属性上下文塞给 innerHTML 后：
  //       属性值必须与原文逐字相等（未被引号提前闭合）、不得多出属性、不得生成 img/script、
  //       不得出现任何 on* 属性、插入的文本节点必须还原为原文。
  test('escapeHtml 五字符完整转义，拼入 value/class 属性后无逃逸无 on*（M-07）', async ({ page }) => {
    const PAYLOADS = [
      'x" onmouseover="alert(1)',
      "y' onfocus='alert(2)",
      '"><img src=x onerror=alert(3)>',
      "'><svg/onload=alert(4)>",
      '&lt;script&gt;alert(5)&lt;/script&gt;',
      '" onmouseover=alert(6) data-x="',
    ];
    const st = watch(page);
    let alertFired = 0;
    page.on('dialog', async (d) => {
      if (d.type() === 'alert') alertFired++;
      await d.dismiss().catch(() => {});
    });
    await page.goto('/');
    const res = await page.evaluate((payloads) => {
      const raw = String.fromCharCode(34) + 'A' + String.fromCharCode(39);   // "A' —— 故意混排两种引号
      const out = { items: [], rawQuoteDouble: false, rawQuoteSingle: false, rawOut: escapeHtml(raw), ampRoundTrip: escapeHtml('&lt;a&gt;') };
      if (out.rawOut.indexOf('"') !== -1) out.rawQuoteDouble = true;
      if (out.rawOut.indexOf("'") !== -1) out.rawQuoteSingle = true;
      payloads.forEach((p) => {
        const e = escapeHtml(p);
        // 两种属性上下文：value="..."（input）与 class="..."（div）
        const host = document.createElement('div');
        host.innerHTML = '<input value="' + e + '"><div class="x ' + e + '" title="' + e + '">' + e + '</div>';
        const kids = Array.from(host.querySelectorAll('*')).map((el) => ({
          tag: el.tagName.toLowerCase(),
          attrs: Array.from(el.attributes).map((a) => a.name.toLowerCase()),
          onAttrs: Array.from(el.attributes).filter((a) => /^on/i.test(a.name)).map((a) => a.name + '=' + a.value),
          value: el.tagName === 'INPUT' ? el.getAttribute('value') : null,
          cls: el.tagName === 'DIV' ? el.getAttribute('class') : null,
        }));
        out.items.push({
          escaped: e,
          kids,
          img: host.querySelectorAll('img').length,
          svg: host.querySelectorAll('svg').length,
          script: host.querySelectorAll('script').length,
          text: host.querySelector('div') ? host.querySelector('div').textContent : null,
        });
        host.remove();
      });
      return out;
    }, PAYLOADS);
    // ① 五个字符完整转义（" 与 ' 分别独立断言：删掉任一条 replace 都会红）
    expect(res.rawQuoteDouble, 'escapeHtml 必须转义双引号（value="..." 会逃逸）').toBe(false);
    expect(res.rawQuoteSingle, 'escapeHtml 必须转义单引号（value=\'...\' 会逃逸）').toBe(false);
    expect(res.rawOut).toBe('&quot;A&#39;');
    expect(res.ampRoundTrip, '& 必须先于 < 转义：&lt;a&gt; 原文须变 &amp;lt;a&amp;gt;').toBe('&amp;lt;a&amp;gt;');
    for (let i = 0; i < res.items.length; i++) {
      const it = res.items[i]; const p = PAYLOADS[i]; const e = it.escaped;
      const where = '#' + (i + 1) + ' [' + p + ']';
      // & < > 也被转义（&lt; 之类原文须变成 &amp;lt;）
      expect(e, 'payload 不得残留裸尖括号 ' + where).not.toContain('<');
      // 属性上下文：属性值逐字等于原文 = 没有被引号提前闭合
      const input = it.kids.find((k) => k.tag === 'input');
      const div = it.kids.find((k) => k.tag === 'div');
      expect(input, 'input 必须存在 ' + where).toBeTruthy();
      expect(div, 'div 必须存在 ' + where).toBeTruthy();
      expect(input.value, 'value 属性必须原样承载（未逃逸）' + where).toBe(p);
      expect(div.cls, 'class 属性必须原样承载（未逃逸）' + where).toBe('x ' + p);
      expect(div.attrs, 'div 只应有 class/title 两个属性 ' + where).toEqual(['class', 'title']);
      expect(input.attrs, 'input 只应有 value 一个属性 ' + where).toEqual(['value']);
      // 无注入元素、无注入事件属性、文本按原文显示
      expect([it.img, it.svg, it.script], '不得生成 img/svg/script ' + where).toEqual([0, 0, 0]);
      const onAll = it.kids.reduce((acc, k) => acc.concat(k.onAttrs), []);
      expect(onAll, '不得出现任何 on* 事件属性 ' + where).toEqual([]);
      expect(it.text, 'innerHTML 插入后文本节点须还原为原文 ' + where).toBe(p);
    }
    await page.waitForTimeout(600);
    expect(alertFired, '属性上下文 payload 不得触发 alert').toBe(0);
    expect(st.dialogs, '不应有任何 dialog: ' + JSON.stringify(st.dialogs)).toEqual([]);
    expect(st.pageErrors, 'pageerror: ' + st.pageErrors.join('\n')).toEqual([]);
    st.expectClean();
  });
});

// UI-10 管理验证记忆（服务端短令牌，默认 5 分钟）
// 口径：密码验证成功后服务端下发 X-Admin-Token（固定 5 分钟、不滑动续期）；
//       浏览器**只存内存**（不落 localStorage/sessionStorage）→ 刷新页面即失效，
//       下一次破坏性动作重新弹密码。
// 红闸门：去掉服务端令牌校验 / 去掉前端令牌复用 / 令牌落盘 → 本组用例变红。
test.describe('UI-10 管理验证记忆（短令牌，5 分钟）', () => {
  const LOOP = 'Aptio:DisposeSample:LoopIntervalSeconds';
  const loopInput = (page) => page.locator('.form-row', { has: page.locator('label[data-path="' + LOOP + '"]') }).locator('input').first();

  test('第一次保存弹 1 次密码；5 分钟内第二次保存不再弹且成功；刷新页面后重新弹 1 次', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept', confirm: 'accept',
      countMethods: ['PUT /api/config/batch', 'GET /api/auth/challenge'],
    });
    await page.goto('/');
    await openTab(page, 'forms');
    let input = loopInput(page);
    await expect(input).toBeVisible();
    const original = await input.inputValue();
    const next = original === '7' ? '8' : '7';

    // ① 第一次保存：恰弹 1 次密码，恰 1 次 batch 请求
    await input.fill(next);
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    expect(st.prompts, '第一次保存应恰弹一次密码').toBe(1);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '第一次保存应发一次批量保存').toBe(1);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '第一次保存取一次鉴权挑战').toBe(1);

    // ② 5 分钟内第二次保存：不再弹密码（复用服务端短令牌 X-Admin-Token）
    st.prompts = 0;
    st.reqCounts['GET /api/auth/challenge'] = 0;
    input = loopInput(page);                       // 保存后表单会重渲染，重新定位
    await expect(input).toBeVisible();
    await input.fill(original);                     // 再改一次 → 产生一次新的保存
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    expect(st.prompts, '5 分钟内第二次保存不应再弹密码').toBe(0);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '第二次保存仍应发一次批量保存').toBe(2);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '第二次保存走令牌，不应再取鉴权挑战').toBe(0);

    // ③ 刷新页面：内存令牌丢失 → 第一次保存重新弹 1 次密码
    await page.reload();
    await openTab(page, 'forms');
    input = loopInput(page);
    await expect(input).toBeVisible();
    st.prompts = 0;
    st.reqCounts['GET /api/auth/challenge'] = 0;
    await input.fill(next);
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    expect(st.prompts, '刷新页面后第一次保存应重新弹一次密码').toBe(1);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '刷新后回到 nonce/hash 流程，取一次挑战').toBe(1);

    // 恢复原值（不留副作用：基线配置的最终值不变）
    input = loopInput(page);
    await expect(input).toBeVisible();
    await input.fill(original);
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    st.expectClean();
  });

  test('短令牌只存浏览器内存：不落 localStorage/sessionStorage', async ({ page }) => {
    const st = watch(page, { prompt: 'accept', confirm: 'accept' });
    await page.goto('/');
    await openTab(page, 'forms');
    const input = loopInput(page);
    await expect(input).toBeVisible();
    const original = await input.inputValue();

    // 存一次 → 服务端下发短令牌
    await input.fill(original === '7' ? '8' : '7');
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    expect(st.prompts, '第一次保存应弹一次密码').toBe(1);

    // 令牌在浏览器内存里（模块级变量，非 window 属性）
    const memToken = await page.evaluate(() => (typeof _adminToken !== 'undefined') ? _adminToken : null);
    expect(memToken, '短令牌应存在浏览器内存（app.js 模块级变量 _adminToken）').toBeTruthy();

    // 但**不能**落任何 Web Storage
    const stored = await page.evaluate(() => ({
      ls: JSON.stringify(window.localStorage),
      ss: JSON.stringify(window.sessionStorage),
    }));
    expect(stored.ls, 'localStorage 不得落管理令牌').not.toContain('X-Admin-Token');
    expect(stored.ss, 'sessionStorage 不得落管理令牌').not.toContain('X-Admin-Token');
    expect(stored.ls.toLowerCase(), 'localStorage 不得含 admin token 字样').not.toContain('admintoken');
    expect(stored.ss.toLowerCase(), 'sessionStorage 不得含 admin token 字样').not.toContain('admintoken');

    // 恢复原值
    const input2 = loopInput(page);
    await expect(input2).toBeVisible();
    await input2.fill(original);
    await page.locator('button:has-text("保存全部")').click();
    await expect(page.locator('#formsHint')).toContainText('已保存', { timeout: 15_000 });
    st.expectClean();
  });

  // L3 P2 闸门：401 重输时**留空**必须视为「用户取消本动作」——
  //   不能沿用上一次（可能就是错的）密码再打一次（必然再 401、白耗一次尝试、还多弹一次窗）。
  // watch 的 password 数组按弹窗顺序作答：第 1 次弹窗答错密码 → 401；第 2 次（401 重输）留空。
  test('401 重输留空：视为取消中止，只发 1 次请求、不再重试（L3 P2）', async ({ page }) => {
    const st = watch(page, {
      prompt: 'accept', password: ['WrongPw1!', ''],   // 1 次=错密码；2 次（401 重输）= 留空
      countMethods: ['PUT /api/config/batch', 'GET /api/auth/challenge'],
    });
    await page.goto('/');
    await openTab(page, 'forms');
    const input = loopInput(page);
    await expect(input).toBeVisible();
    const original = await input.inputValue();
    const fileBefore = fs.readFileSync(APP_DIR + '/appsettings.json', 'utf8');

    // 改一个字段 → 触发一次「保存全部」（密码答错 → 服务端 401）
    await input.fill(original === '7' ? '8' : '7');
    await page.locator('button:has-text("保存全部")').click();

    // 401 重输弹窗留空 → 视为取消：提示「已取消」、不发第 2 次请求、不再弹第 3 次窗
    await expect(page.locator('#formsHint')).toContainText('已取消', { timeout: 15_000 });
    await expect(page.locator('#formsHint')).toContainText('已取消，未保存');
    await expect(page.locator('#formsHint')).not.toContainText('❌ 保存失败');
    expect(st.prompts, '初始 1 次 + 401 重输 1 次 = 恰 2 次弹窗').toBe(2);
    expect(st.reqCounts['PUT /api/config/batch'] || 0, '留空即中止：只应发出 1 次保存请求').toBe(1);
    expect(st.reqCounts['GET /api/auth/challenge'] || 0, '留空即中止：不应再取第 2 次鉴权挑战').toBe(1);
    // 配置不得有任何写入（请求在鉴权层就被 401 挡下）
    expect(fs.readFileSync(APP_DIR + '/appsettings.json', 'utf8'), '留空中止后配置文件不得变化').toBe(fileBefore);
    st.expectClean(['/api/config/batch', '401 (Unauthorized)']);   // 401 是本用例预期的响应
  });
});
