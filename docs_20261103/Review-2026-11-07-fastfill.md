# L3 独立复核报告 — DisposeSample「快速填窗」（v1.0.6.7 之后的未提交改动）

**有偏差**（任务口径：PASS-with-findings —— 功能闸门全绿、无 P0/P1，但存在 5 项 P2 偏差；**有条件放行**，前置见 §4）

- 复核人：reviewer（L3 critic，DeepSeek V4.1 Flash，独立会话；未参与实施）
- 日期：2026-11-07
- 被测物：`/root/opencode_project/UniFlow`，HEAD `b68994b`（v1.0.6.7）+ 工作区 7 个文件未提交改动
- 被测物指纹（复核前后各算一次，见 §1 第 17 行）：7 文件 md5 完全一致、`git status` 条目数一致 → **零写**
- 范围：只复核本次「快速填窗」新需求；不重审 1.0.6.7 历史项

---

## 1. 结论表（每条均为我自己跑出的命令与输出）

| # | 被复核断言 | 我的命令 | 原始输出片段 | 执行位置 |
|---|---|---|---|---|
| 1 | Release 构建通过 | `export HOME=/root; /usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release` | `Build succeeded.` / `4 Warning(s)` / `0 Error(s)`（4 条均为既有 NU1902 MySqlConnector 2.4.0、NU1903 SQLitePCLRaw 2.1.11） | 仓库 |
| 2 | 单测 461/461（声称 461） | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release`（第 1 轮）；`--no-build` 第 2 轮；`-t:Rebuild` 后第 3 轮 | `Passed! - Failed: 0, Passed: 461, Skipped: 0, Total: 461, Duration: 51 s` / 53 s / 51 s（三轮全同） | 仓库 |
| 3 | 前端语法 | `node --check code/src/UniFlow/wwwroot/app.js; echo rc=$?` | `node_rc=0`（node v24.16.0） | 仓库 |
| 4 | `EnableFastFill=false` 时单条路径与 1.0.6.7 **逐字节一致** | `git show b68994b:…/DisposeSampleWorker.cs > /tmp/…; awk` 抽取 `HandleReadyAsync` 的 `var response = await _commands.SendStatusRequestAsync` 到函数尾；新旧两段 `diff -u` + `md5sum` | `diff_rc=0`；`a81871db0e2100104d7aa75d0300ab96` 两段相同（各 72 行） | `/tmp/opencode/rv` |
| 5 | 默认关闭仍「发 1 条 → Ack」 | 交付用例 `FastFill_DisabledByDefault_SendsOneAndEntersAck` | `Received(1)` COMMENT、`UpdateStatusAsync("S001","send")` ×1、`CurrentState == Ack` | 仓库测试 |
| 6 | 窗口计数：`window=1` 只发 1；在途 9/10 只补 1；`window<=0` 回单条路径 | 自写用例 `Rv_WindowOne_SendsExactlyOne` / `Rv_InFlightNine_SendsOnlyRemaining`（/tmp 副本）+ 交付 `FastFill_WindowUnlimited_KeepsSingleRecordPath` | 3 条全绿；`window=0` 时 `COMMENT×1`、`state=Ack` | `/tmp` 副本 + 仓库 |
| 7 | 失败停止：投递失败 / `UpdateStatus("send")` 失败 / 节点未就绪 / 空条码 / skip 失败 | 交付 `FastFill_ThirdNotDelivered_*`、`FastFill_SendStatusUpdateFails_*`、`FastFill_NodeNotReady_*`；自写 `Rv_SkipUpdateFails_StopsLoop`、`Rv_EmptyBarcode_Stops`、`Rv_StatusFails_StopsBeforeSelect` | 全部通过；skip 失败时 `skip×1`、`COMMENT×0`、`state=Ready`、健康记录 `degraded`；空条码时 `Select` ×1、`COMMENT×0` | `/tmp` + 仓库 |
| 8 | skip 成功 → 继续下一条、不卡队首、不死循环 | 交付 `FastFill_UnknownNodeSkip_AdvancesQueueHeadAndKeepsSending` | `UpdateStatus("S001","skip")×1`、`COMMENT×10`（窗口满即停）、`state=Ready` | 仓库测试 |
| 9 | 批量确认：关闭时 **0 次 DB** | 交付 `FastConfirm_Disabled_DoesNotTouchDb` + 读 `DisposeSampleWorker.cs:233` | `RecheckPendingDisposalsAsync` 0 次；代码首行 `if (!_dc.EnableFastFill) return 0;`（在任何 await 之前） | 仓库 |
| 10 | 节流生效；用 `(200, 0)`；**不推进** `_recheckCursor` | 交付 `FastConfirm_Enabled_QueriesOncePerInterval_WithCursorReset`；自写 `Rv_ConfirmSecondsZero_ClampedToOne`；`grep -n _recheckCursor` | `RecheckPendingDisposalsAsync(200, 0)`；5s 内 0 次、6s 后第 2 次；配置 0 秒时 0.5s 仍节流、1.1s 放行（钳到 1s）；`_recheckCursor` 仅在 `:275`（`RunMaintenanceAsync`）赋值 | 仓库 + `/tmp` |
| 11 | 命中置 `disposed=1`（复用现有 SQL） | 读 `DisposeDatabaseService.cs:350-356`；既有 `DisposeDatabaseServiceRecheckTests`/`QueueTests` | `UPDATE sam_dispose_status SET disposed=1, disposed_time=@Now WHERE barcode IN @Hits AND send=1 AND disposed=0`（`:354`） | 仓库 |
| 12 | 重排队：窗口满 + 有候选 → 发 1 条 STATUS + requeue；无候选 → 不发 STATUS；窗口释放 `send=1→0` | 交付 `Requeue_WindowFull_StillRequeuesEligibleRecords` / `..._NoCandidates_DoesNotSendStatus`；读 `RequeueForResendCoreAsync:520-527` | 两条通过；SQL `SET send=0 WHERE barcode IN @Barcodes AND send=1 AND disposed=0` | 仓库 |
| 13 | 状态机交互：快速填窗不设 Ack；Ack 兜底转 None 不重复发送、不丢确认 | 自写 `Rv_AckFallback_TurnsNoneWithoutConfirm` + 对照 `Rv_Ack_DisabledConfirmControl`；自写 `Rv_AckFallback_WithWindowZero_StillSkipsConfirm` | 通过：开启时 `CheckDisposedAsync` 0 次、`state=None`；关闭时 `CheckDisposedAsync("S001","18")` ×1。**窗口<=0 时兜底仍然生效**（见 P2-3） | `/tmp` |
| 14 | 新增可选构造参数 `Func<DateTime>? utcNow = null` 不破坏 DI（`AddHostedService<T>` = `ActivatorUtilities`） | 自写 `Rv_DiCanConstructWorker_WithOptionalClockParam`（`ServiceCollection` + `ActivatorUtilities.CreateInstance`） | 通过 | `/tmp` |
| 15 | 文档与代码一致（3 字段：路径/默认/热生效） | 读 `ConfigShared.cs:93,95,98`、`app.js:713-715`、`CONFIG-REFERENCE.md:89-91`、`Configuration-Manual.md:169-171` | 路径/默认值 `false/1000/5`/`Hot` 四处一致；**但文档声明的数值范围代码未 clamp**（P2-1） | 仓库 |
| 16 | 无「窗口满连 STATUS 都不发」旧描述残留 | `grep -rn "连 STATUS 都不发\|不发 COMMENT/STATUS\|窗口满" docs/`（全仓，排除 bin/obj） | 现行文档（CONFIG-REFERENCE §3/§6、Configuration-Manual）已改为新语义；**残留：`docs/dispose-state-machine.lifecycle.json:122,344` 与 `docs/dispose-state-machine.html:4006,4013` 仍写「inFlight ≥ 10：不发 COMMENT/STATUS」**（P2-4）。`CHANGELOG-1.0.6.6:106`、`Fix-Report-2026-11-07:85` 为历史版本记录，可不改 | 仓库 |
| 17 | 复核零写 | 复核前后 `md5sum` 7 个改动文件 + `git status --porcelain` | `MD5_IDENTICAL`；前后均为 `6 M + 1 ??`（7 条），HEAD 仍 `b68994b` | 仓库 |

**用例数核对**：新文件 `DisposeSampleWorkerFastFillTests.cs` 实测 `Passed: 14`（=声称 14 例）；`RequeueTests` 1 改 2 增（净 +1）；446（1.0.6.7 基线，见 `Review-2026-11-07-D1-D3-D4-M07-recheck.md`）+14+1 = **461** ✓。

---

## 2. 负向证据（均在 `/tmp/opencode/rv/neg/code` 副本做，做完恢复；仓库零写见第 17 行）

基线（副本未改）：`--filter FastFillTests|RequeueTests` → `Passed: 42, Failed: 0`。

| # | 如何破坏 | 失败输出（原始片段） |
|---|---|---|
| 2.1 | `HandleReadyAsync` 去掉开关：`if (_dc.EnableFastFill && window > 0)` → `if (window > 0)` | `Failed FastFill_DisabledByDefault_SendsOneAndEntersAck` / `Failed! - Failed: 1, Passed: 13, Total: 14` |
| 2.2 | 去掉窗口停止：删除 `if (window > 0 && inFlight >= window) break;` | `Failed FastFill_Enabled_SendsUpToWindowInOneTick_StaysReady`、`Failed FastFill_UnknownNodeSkip_AdvancesQueueHeadAndKeepsSending` / `Failed: 2, Passed: 12` |
| 2.3 | 恢复重排队提前 return（窗口满时 `LogRequeueSkip(...); return 0;`） | `Failed Requeue_WindowFull_StillRequeuesEligibleRecords`（RequeueTests 与 FastFillTests 各 1 条）/ `Failed: 2, Passed: 40` |
| 2.4 | 去掉批量确认开关：删除 `if (!_dc.EnableFastFill) return 0;` | `Failed FastConfirm_Disabled_DoesNotTouchDb` / `Failed: 1, Passed: 13` |
| 2.5 | 补充边界（我的独立用例，副本新增 `RvFastFillBoundaryTests.cs`，**不在仓库**） | `Passed: 10, Failed: 0`：skip 失败停止 / 空条码停止 / STATUS 失败不查队列 / window=1 / 在途 9/10 / 确认间隔 0 钳到 1s / Ack 兜底 / 窗口<=0+开启时同样跳过逐条确认 / 关闭时逐条确认对照 / DI 构造 |

恢复副本后重跑基线仍 `42/42` 全绿 → 4 条闸门都「能红」，且红点与代码一一对应。

---

## 3. 偏差清单

### P2-1 配置「范围」只写在文档，代码无上限 clamp（含阻塞放大）
- 证据：`DisposeSampleWorker.cs:861-868`（`DelayFastFillAsync`）只判 `ms <= 0`；`MaybeFastConfirmAsync` 只判 `seconds < 1`（`:236-237`）。`RestartLevelResolver.ValidateValue` 仅校验 `int` 类型（`RestartLevelResolver.cs:118-140`），无范围校验；`app.js:714-715` 只给 `min`、无 `max`。
- 文档：`CONFIG-REFERENCE.md:90-91`、`Configuration-Manual.md:170-171` 明写 `0~5000` / `1~60`。
- 影响：`FastFillIntervalMs=600000` 会让**一个 tick 阻塞约 100 分钟**（窗口 10 时），期间健康心跳/维护停摆，超过 `HealthStore.StaleThreshold=5min` 即被面板判 stale。属「填错数字」即触发的现场风险。
- 建议：代码 clamp（`Math.Clamp(ms, 0, 5000)`、`Math.Clamp(s, 1, 60)`）+ 页面 `max`，或文档删掉范围声明。

### P2-2 快速填窗 + `SkipOnUnknownNode=true`：单 tick 内 skip 循环无迭代上限
- 证据：`HandleReadyFastFillAsync` 的 skip 成功分支 `await DelayFastFillAsync(ct); continue;`（`:809-811`），不增加 `sent`/`inFlight`，也不计 skip 次数 → 只要队首连续是未知节点，一个 tick 会遍历**整个待发队列**（`MaxPendingRecords=0` 默认不限）。每条 = 1 次 STATUS（`_statusResponseTimeout` 最长 10s，现场实测约 3.5s，见 `CHANGELOG-1.0.6.7:310`）+ `FastFillIntervalMs`。
- 影响：`SrmNodeIds` 配错（如 `18` vs `018`）时队列若有数千条，单 tick 可阻塞数十分钟到数小时；期间 `MaybeRecheckAsync`/`MaybeRequeueForResendAsync`/健康上报都不执行（`ExecuteAsync` 串行）。旧路径同样慢，但每 3s 一个 tick、健康不会 stale。
- 缓解：取消可打断（下一次 socket 调用抛 `OperationCanceledException` 会退出），不会永久卡死。
- 建议：给 skip 迭代加上限（如 `window*2` 或 50），或每 N 条 `break` 交还主循环。

### P2-3 窗口 `<=0` 时「确认口径」与文档措辞不一致（实现自洽，文档未说明）
- 事实：`HandleAckAsync` 的兜底只看 `_dc.EnableFastFill`（`:873`），不看 `window`；因此 `EnableFastFill=true` + `MaxOnetimeSelectDiscardCount<=0` 时：**发送**确实回退为一条一条（`:683`），但 **Ack 逐条确认被跳过**（`CheckDisposedAsync` 0 次，`disposed`/`checkcount` 都不写），确认完全依赖 `MaybeFastConfirmAsync`（仍生效）/60s 轮转；`MaxWaitDiscardCount` 语义失效。
- 证据：`Rv_AckFallback_WithWindowZero_StillSkipsConfirm` 通过（`CheckDisposedAsync` 0 次）；对照 `Rv_Ack_DisabledConfirmControl`（开关关闭、同一窗口）`CheckDisposedAsync("S001","18")` ×1。
- 影响：不丢确认、不重复发送（记录 `send=1` 不会被再次选中），只影响「逐条确认/最大等待次数」口径；文档 `CONFIG-REFERENCE.md:207`、`Configuration-Manual.md:184` 只写「仍按一条一条发」，未说明确认仍为批量。
- 建议（二选一）：guard 改为 `if (_dc.EnableFastFill && _dc.MaxOnetimeSelectDiscardCount > 0)`；或在两处文档补一句「窗口<=0 时确认仍走批量，`MaxWaitDiscardCount` 不生效」。

### P2-4 状态机图残留旧语义
- `docs/dispose-state-machine.lifecycle.json:122`：`"sublabel": "inFlight ≥ 10：不发 COMMENT/STATUS"`；`:344`：`"inFlight≥10 不发任何 COMMENT/STATUS"`。
- `docs/dispose-state-machine.html:4006,4013`：同文案。
- 与本次改动（窗口满仍发 1 条 STATUS 并重排队）矛盾；图中也没有「快速填窗」分支。属**现行设计图**（非历史 CHANGELOG），建议随本次改动更新或标注「1.0.6.7 版」。

### P2-5 重排队行为变更**不受** `EnableFastFill` 控制，默认配置也会变
- 证据：`MaybeRequeueForResendAsync` 的窗口满分支（`:342-345`）无开关判断；`git diff` 显示原 `return 0` 被删除。
- 影响：即使不开快速填窗，窗口满（10 条在途且 5 分钟未确认）时也会发 1 条 STATUS 并把到期候选放回 `send=0` → 立刻重发（重复 COMMENT，24 次上限内）。这是需求要求的死锁修复，但**改变了 1.0.6.7 的默认现场行为**；文档已同步（CONFIG-REFERENCE §3/§6）。
- 建议：现场开启前用测试环境确认「冰箱慢（>5 分钟未确认）」场景下的重复 COMMENT 可接受；无需改代码。

### P3（记录，不阻塞）
1. `DelayFastFillAsync` 吞掉 `OperationCanceledException`（`:866`）→ 取消后不立即退出，最多多做一轮（依赖下一次 socket 调用抛出）。
2. `_lastFastConfirmUtc = now` 在查库**之前**（`:241`）→ 查询异常后需再等满一个间隔；且 `EnableFastFill=true` 时**空闲也每 5s 查一次库**（12 次/分钟）。
3. 批量确认固定 `(200, 0)`：`MaxOnetimeSelectDiscardCount` 可设 >200，超出 200 的在途尾部只能等 60s 轮转（默认窗口 10 无影响）。
4. 交付用例未覆盖「空条码 / STATUS 失败 / skip 失败」三条停止路径（我已用 /tmp 副本 3 条用例证明代码正确）；建议补进仓库。
5. 无 CHANGELOG 条目（`docs/` 最新为 1.0.6.7）；发布前需补。`Configuration-Manual.md:189`「最多延迟 `FastFillConfirmIntervalSeconds`（默认 5 秒）」偏乐观——实际 = 节流 5s + 循环 3s + 一个 tick 的时长（可能 10~45s）。
6. `UpdateStatusAsync` 在 UPDATE 影响 0 行时仍返回 `true`（既有弱点）。快速填窗「skip 成功即 continue」放大了它：若 `barcode` 为 NULL 会死循环。已核对 DDL `barcode VARCHAR(25) NOT NULL`（`DisposeDatabaseService.cs:67`），空串会命中自身行（`UPDATE ... WHERE barcode=''`），**当前表结构下不可达**，仅记录。

---

## 4. 放行建议：**有条件放行**

无 P0/P1，默认关闭路径与 1.0.6.7 逐字节一致，461 三轮全绿，闸门可红。合并前建议完成：

1. **P2-1**：`FastFillIntervalMs` clamp `0~5000`、`FastFillConfirmIntervalSeconds` clamp `1~60`（代码或页面 `max` 至少一处；两处都做更好）。
2. **P2-2**：skip 迭代上限（防配错节点时单 tick 长时间占用）。
3. **P2-3**：Ack 兜底加 `window > 0` 条件，或文档补「窗口<=0 时确认仍批量、`MaxWaitDiscardCount` 不生效」。
4. **P2-4**：更新 `dispose-state-machine.html` / `.lifecycle.json` 的「不发 STATUS」旧文案。

P2-5 不改代码，但需要现场按 `Configuration-Manual.md`「风险提示」先验证连续 COMMENT 与重复重发。

---

## 5. 我没能验证的部分

| 项 | 原因 | 谁能补 |
|---|---|---|
| 真实 Aptio 2055 / SRM / 冰箱端到端（连续 10 条 COMMENT 是否被接受、是否互相干扰、慢 STATUS 下的实际 tick 时长） | 本机无设备、无 2055 连接 | 实施方/现场（测试环境开启后观察 `Fast-fill sent N` / `Fast-fill confirm: N`） |
| WebAdmin 页面 3 字段的实际渲染与保存（浏览器点击） | 保存会写 `appsettings.json`（仓库文件/运行态），违反只读边界；未启服务 | 用 `run-test/playwright` 或现场页面；后端路径已由 `FastFill_ConfigPaths_AreHotAndTyped`（ResolveWithKind ≥ 可写）佐证 |
| Playwright/UI 自动化 | 本次改动未包含 UI 用例 | 实施方补 |
| 真实 MySQL 下 `RecheckPendingDisposalsAsync(200,0)` 的耗时与锁竞争 | 无 MySQL 实例 | 现场；SQL 与既有 60s 补查同源，风险低 |
| 生产时钟（`DateTime.UtcNow`）跨 NTP 跳变对节流的影响 | 测试用注入时钟 | 低风险，可忽略 |

---

## 6. 附：复核用文件位置（均在仓库外）
- 旧版抽取/对比：`/tmp/opencode/rv/worker_b68994b.cs`、`old_path.txt`、`new_path.txt`
- 负向副本：`/tmp/opencode/rv/neg/code`（含我的 `RvFastFillBoundaryTests.cs`，未进入仓库）
- 复核前后 md5：`/tmp/opencode/rv/md5_before.txt`、`md5_after.txt`
