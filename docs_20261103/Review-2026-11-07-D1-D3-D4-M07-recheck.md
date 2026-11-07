# L3 独立复核报告 — 1.0.6.7 追加项（D1 / D3 / D4 / M-07）spot-check

- 复核人：reviewer（L3 critic，GLM-5.3-Flash，独立会话）
- 日期：2026-11-07
- 被测物：仓库 `/root/opencode_project/UniFlow`，HEAD `6012902`（1.0.6.6），工作区 = 1.0.6.7 未提交改动
- 范围：只复核上一轮（Review-2026-11-07-M01-M08.md）之后追加/修改的 4 处，不重审 M-01~M-08 全部历史
- **结论：PASS（可提交）**

## 1. 结论表

| 断言 | 我的命令 | 原始输出片段 | 执行位置 |
|---|---|---|---|
| D1 升级 Error 只在 fails 为 10 的倍数时一条 | 读 `SampleCleanupService.cs:288-299` | `escalationPoint = ... fails % RollbackErrorEscalationCount == 0`，命中即 `LogError` + `return` | 仓库 `code/src/UniFlow/DMSAutoOrder/Services/SampleCleanupService.cs` |
| D1 成功清零 / Warning 按 sid 10min 节流 | 读 `:238-239`、`:34`、`:290-308` | `_rollbackWarnAtUtc.Remove(sid); _rollbackFailCount.Remove(sid);`；`RollbackWarnInterval = FromMinutes(10)` | 同上 |
| D1 测试覆盖（14 轮恰 1 / 20 次恰 2 / 成功清零） | `dotnet test --filter ...ThrottleTests` | 9 条全绿（见下） | `SampleCleanupServiceThrottleTests.cs:122-179` |
| D3 publish 后检查 $LASTEXITCODE + 产物校验 + exit 1 | 读 `install-service.ps1:63-95` | `$publishExitCode = $LASTEXITCODE` → `if ($publishExitCode -ne 0) { ... exit 1 }` → `Test-Path $stagedExe` 否则 `exit 1` | 仓库 `deploy/windows/install-service.ps1` |
| D3 `-SkipPublish` 合理 | 读 `:105-125` | 无 exe → `exit 1`；有 exe → 打印 binary/project version，不一致红色 STALE 警告 | 同上 |
| D3 文本断言真能抓住"去掉 LASTEXITCODE" | /tmp 副本删除该闸门后重跑 | `PublishService_Checks_LastExitCode_AndAbortsOnFailure`、`PublishFailure_Cannot_Reach_NewService_Or_StartService` **变红** | `InstallServiceScriptPathTests.cs:202-230` |
| D4 正则 `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$`，连库前拒绝 | 读 `DisposeDatabaseService.cs:163-174`、`:685-686` | `SanitizeSqlName` 在 `NewConnection()` **之前**调用，null → throw；正则与工单逐字一致 | 仓库 `code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs` |
| D4 测试覆盖（flexlab.view_x 过 / a.b.c 等拒） | `dotnet test --filter ...SqlNameTests` | 28 条全绿；/tmp 副本把规则改回 `^[A-Za-z0-9_]+$` → 4 条 schema 用例**变红** | `DisposeDatabaseServiceSqlNameTests.cs` |
| M-07 属性上下文用例在 :667 / :761，只改 run-test/playwright | `git diff run-test/playwright/ui.spec.js` + grep | 362 行纯新增、0 删除；`app.js` 不在改动清单（escapeHtml 五字符转义为 1.0.6.6 既有，`app.js:558-566`）；`:667` = 配置编辑态 value 属性上下文、`:761` = 五字符转义 + value/class 属性拼接 | `run-test/playwright/ui.spec.js` |
| 构建 | `dotnet build code/src/UniFlow/UniFlow.csproj -c Release` | `14 Warning(s) / 0 Error(s)` | 仓库 |
| 单测 | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 446, Skipped: 0, Total: 446`（声称 446 ✓） | 仓库 |
| 前端语法 | `node --check code/src/UniFlow/wwwroot/app.js` | `NODE_CHECK_OK`（node v24.16.0） | 仓库 |
| 版本号 | `grep Version code/src/UniFlow/UniFlow.csproj` | `1.0.6.7`（Version/Assembly/FileVersion 三处）；`deploy/build.sh:7 VERSION="1.0.6.7"` | 仓库 |
| CHANGELOG 不夸大 | 通读 `docs/CHANGELOG-1.0.6.7.md` | 明确写 `446/446`、Playwright `55/55`、"本版尚未在真实 MySQL/Aptio/Windows Service/Docker 上做过端到端验收" | 仓库 |
| 包校验值 | `md5sum`/`sha256sum`/`stat` | 1.0.6.7 两包 md5 `8c60ceef…`/`570458a3…`、sha256/大小与 CHANGELOG §9.2 逐字一致 | `release/` |
| 1.0.6.6 包未动 | `md5sum release/UniFlow-1.0.6.6-*`（复核前后各一次） | `5fa3daa8be0b0ae0c54b6bad1e2d74be`（linux）、`c109ef9237a09038a925e25ceba1d9e4`（win），两次一致 | `release/` |
| git status 只含预期改动 | `git status --porcelain` | 21 M + 10 新增（8 测试文件 + CHANGELOG + 复核报告）+ 2 新包 = 33 条，无意外文件 | 仓库 |

## 2. 负向证据（/tmp 副本 `rev16067`，仓库零写）

同时注入 3 处突变后，三个测试类 `Failed: 8, Passed: 40 / Total: 48`：

| 突变 | 变红用例 |
|---|---|
| D1：`fails % 10 == 0` → `fails >= 10`（改回每轮刷 Error） | `Rollback_EscalationError_IsLogged_OnlyOnce_NotEveryRound`（实测 Error 数 4 ≠ 1）、`Rollback_EscalationError_Repeats_EveryN_Failures` |
| D3：删除 `$LASTEXITCODE` 捕获与失败闸门 | `PublishService_Checks_LastExitCode_AndAbortsOnFailure`、`PublishFailure_Cannot_Reach_NewService_Or_StartService` |
| D4：规则改回 `^[A-Za-z0-9_]+$`（拒绝 schema） | `SanitizeSqlName_SchemaPrefixedName_IsAccepted` ×3、`SanitizeSqlName_StrictRule_WouldHaveRejectedSchemaPrefix` |

恢复原文件后正向对照：`Passed! - Failed: 0, Passed: 48`。三条闸门都红过 → 都是真闸门。

## 3. 偏差清单

无（本轮复核未发现新问题）。既有已登记项（D5/D6/D7/D8/D10/D11、M-09/M-10）不属本轮范围。

## 4. 放行建议

**放行（可提交）**。1.0.6.6 包 md5 前后一致，负向实验只发生在 /tmp 副本，仓库源码零写（仅构建产生的 gitignored bin/obj）。

## 5. 未能验证的部分

- Playwright 55/55 未能重跑：本复核环境无浏览器/Playwright 运行时配置，只静态核对了新增用例的断言逻辑与行号锚点（谁可补：有 Playwright 环境的实施/主会话）。
- install-service.ps1 在 Windows 实机的行为（CHANGELOG 已如实声明未验证，属 E-01~E-07 现场验收项）。
- M-07 的"负向（删引号转义）变红"我未重复实施方的 Playwright 破坏实验，仅确认 :667/:761 的断言确实以"输出中含裸 `"`/`'`"为失败条件（:761 用例显式断言 `rawOut.indexOf('"') === -1`），该断言在删掉引号转义时必然失败。
