# UniFlow WebAdmin Playwright 测试（UI + API）

对**冻结副本** `dotnet publish` 产物做界面 + API 回归，重点：管理鉴权（一次性 nonce）、
配置读写脱敏、破坏性端点防护、UI「一次动作一次密码」口径。

## 文件清单
| 文件 | 说明 |
|---|---|
| `playwright.config.js` | baseURL/trace/截图/视频策略 + `webServer` 自动起停被测服务（每次运行先恢复基线配置） |
| `api.spec.js` | API 用例（版本/健康、配置脱敏、nonce 一次性、batch 校验、下载、test-db、队列读、内部重启） |
| `ui.spec.js` | UI 用例（首页、只读页不弹密码、丢弃队列、测试连接、保存全部恰一次密码+一次请求、下载、破坏性动作弹密码、最后重启） |
| `package.json` | 依赖 `@playwright/test`（`node_modules` 不入库） |

## 前置（一次性，按本机路径可调整环境变量）
```bash
# 1) 冻结副本 + 构建
mkdir -p /tmp/uniflow-e2e
rsync -a --exclude bin --exclude obj --exclude .git --exclude node_modules \
  /root/opencode_project/UniFlow/ /tmp/uniflow-e2e/src/
HOME=/root /usr/share/dotnet/dotnet publish \
  /tmp/uniflow-e2e/src/code/src/UniFlow/UniFlow.csproj -c Release -o /tmp/uniflow-e2e/app
# 注意：必须用 /usr/share/dotnet/dotnet（net10）；/usr/bin/dotnet 是 SDK 8 会失败。

# 2) 基线测试配置（密码 Advia1/TestDbPwd/DmsPwd；Features 全 false；127.0.0.1:5199）
#    内容见本目录 README 末尾「基线配置要点」，写到 /tmp/uniflow-e2e/appsettings.test.json
```

基线配置要点（相对仓库 appsettings.json 的改动）：
- `WebAdmin`: `Enabled=true`、`BindIp=127.0.0.1`、`Port=5199`、`AdminPassword=Advia1`
- `Aptio`: `Ip=127.0.0.1`、`Port=2055`、`SrmNodeIds=["09"]`、`Database.Password=TestDbPwd`
- `DMS.DbPassword=DmsPwd`
- `Features` 全部 `false`（避免真实 worker 连接 Aptio/MySQL 影响测试）

## 运行
```bash
cd run-test/playwright
npm install            # 或复用外部 node_modules（见下）
npx playwright test    # webServer 自动恢复基线配置并启动 app；结束后停掉
# 报告：test-results/（trace/截图/视频，仅失败保留）+ playwright-report/index.html
```
可覆盖环境变量：`UNIFLOW_APP_DIR`（默认 `/tmp/uniflow-e2e/app`）、
`UNIFLOW_BASELINE_CFG`（默认 `/tmp/uniflow-e2e/appsettings.test.json`）、
`UNIFLOW_DOTNET`（默认 `/usr/share/dotnet/dotnet`）、`UNIFLOW_PORT`（默认 `5199`）。

本机免安装跑法（node_modules 放 /tmp，不入库）：
```bash
mkdir -p /tmp/uniflow-e2e/playwright
cp /root/opencode_project/UniFlow/run-test/playwright/{*.spec.js,playwright.config.js,package.json} /tmp/uniflow-e2e/playwright/
ln -sfn <含 @playwright/test 的 node_modules> /tmp/uniflow-e2e/playwright/node_modules
cd /tmp/uniflow-e2e/playwright && npx playwright test
```

## 用例与顺序约定
- `workers=1`、projects 顺序 api → ui；重启类用例放在各自文件最后。
- **绝不**带鉴权调用 `POST /api/restart/process`（真实执行会杀掉被测进程）。
- 「取消密码后不得发出请求」类用例通过 watch `page.on('request')` 计数验证。

## 已知限制（SKIP 项）
- 丢弃队列「勾选记录 → 重置/清空/重新检查」全流程需要 MySQL `sam_dispose_status` 真实数据；
  测试环境无 Aptio 业务库（Features 全 false → 内层宿主返回 503），只验证了无鉴权读不 401、
  无勾选时不弹密码不发请求。补数据需要真实 MySQL + DisposeSample worker。
- 真实 Aptio 2055 socket 交互不在范围（测试环境无 Aptio）。
- 升级包 upload→apply→进程重启 全链路未做（进程重启无法在共享环境安全执行）。
