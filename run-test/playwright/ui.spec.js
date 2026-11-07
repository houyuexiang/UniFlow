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
