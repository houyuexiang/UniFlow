// UniFlow WebAdmin Playwright 配置（UI + API 测试）
// 被测环境：冻结副本 publish 产物（默认 /tmp/uniflow-e2e/app，可用环境变量覆盖）。
// webServer 会在每次运行前把基线配置复制回 app 目录并以 127.0.0.1:5199 启动 UniFlow。
// 注意：workers=1（共享同一服务进程，重启类用例放在最后）。
const { defineConfig } = require('@playwright/test');

const APP_DIR = process.env.UNIFLOW_APP_DIR || '/tmp/uniflow-e2e/app';
const BASELINE = process.env.UNIFLOW_BASELINE_CFG || '/tmp/uniflow-e2e/appsettings.test.json';
const DOTNET = process.env.UNIFLOW_DOTNET || '/usr/share/dotnet/dotnet';
const PORT = process.env.UNIFLOW_PORT || '5199';

module.exports = defineConfig({
  testDir: '.',
  testMatch: /.*\.spec\.js$/,
  timeout: 120_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  outputDir: 'test-results',
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'api', testMatch: /api\.spec\.js$/ },
    { name: 'ui', testMatch: /ui\.spec\.js$/, use: { browserName: 'chromium' } },
  ],
  // 每次运行重置 appsettings 基线（上一轮可能改过配置），reuseExistingServer=false：
  // 若端口已有服务会直接报错，避免测到非冻结副本。
  webServer: {
    command: `cp ${BASELINE} ${APP_DIR}/appsettings.json && cd ${APP_DIR} && HOME=/root ${DOTNET} UniFlow.dll`,
    url: `http://127.0.0.1:${PORT}/api/version`,
    reuseExistingServer: false,
    timeout: 120_000,
  },
});
