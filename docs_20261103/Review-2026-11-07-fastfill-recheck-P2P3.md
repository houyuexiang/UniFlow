# 复核报告：fast-fill P2-1~P2-4 + P3 spot-check（L3 独立复核）

- 日期：2026-11-07
- 复核人：reviewer（L3，独立重跑，非实施方）
- 被测物：HEAD `b68994b` + 未提交工作区（快速填窗 + 1.0.7.0 发布工程）
- 范围：仅 `docs_20261103/Review-2026-11-07-fastfill.md` 的 P2-1~P2-4 修复 + 3 条 P3；未重审整个功能

## 结论：PASS（7/7 项通过，可提交）

## 1. 结论表

| # | 被复核断言 | 我的命令 | 原始输出片段 | 执行位置 |
|---|---|---|---|---|
| P2-1a | clamp 0~5000 / 1~60 存在且被使用 | `grep -n "ClampFastFill\|_dc.FastFill"` | `:90 ClampFastFillIntervalMs => Math.Clamp(ms,0,FastFillIntervalMaxMs)`；`:916`（DelayFastFillAsync）、`:926`（DelayFastFillSkipAsync）、`:270`（MaybeFastConfirmAsync）三处调用，全仓无未钳制读取 | 仓库 `DisposeSampleWorker.cs:73-97,270,916,926` |
| P2-1b | app.js `max:5000`/`max:60` | `grep -n "FastFill" wwwroot/app.js` | `:714 min:0, max:5000`；`:715 min:1, max:60` | 仓库 app.js:714-715 |
| P2-1c | 文档范围一致 | grep CONFIG-REFERENCE / Configuration-Manual | `CONFIG-REFERENCE.md:90-91,211-212`、`Configuration-Manual.md:170-171,174-175` 均写 `0~5000` / `1~60`（代码硬钳制） | 仓库 docs |
| P2-2a | skip 上限 200 + Warning + break | 读 `HandleReadyFastFillAsync` | `:850-857 skipped>=FastFillMaxSkipsPerTick → LogWarning("Fast-fill skipped {Skipped} … skip limit {Limit}") + break`；`:81 FastFillMaxSkipsPerTick=200` | 仓库 DisposeSampleWorker.cs:849-858 |
| P2-2b | skip 后延迟 min(interval,100ms) | 读 `DelayFastFillSkipAsync` | `:926-928` clamp 后 `if (ms > FastFillSkipDelayMs) ms = 100` | 仓库 DisposeSampleWorker.cs:924-930 |
| P2-2c | 测试 201 条 → 只 skip 200 | 读 + 全量重跑测试 | `FastFill_UnknownNodeSkip_StopsAfterMaxSkipsPerTick`（201×"99"，断言 `Received(FastFillMaxSkipsPerTick)` + Warning 文案）通过 | 测试 :495-517 |
| P2-3 | Ack 兜底加 `MaxOnetimeSelectDiscardCount > 0` | 读 `HandleAckAsync` | `:938 if (_dc.EnableFastFill && _dc.MaxOnetimeSelectDiscardCount > 0) { _state=None; return; }`；窗口<=0 落到逐条确认（`:954 CheckDisposedAsync`）；测试 `Ack_FastFillEnabledWindowZero_KeepsPerRecordConfirm` 通过；文档两处（CONFIG-REFERENCE:207、Configuration-Manual:188）补了「确认仍走逐条，MaxWaitDiscardCount 继续生效」 | 仓库 DisposeSampleWorker.cs:938-959 |
| P2-4 | 状态机图旧文案清除 | `grep -rn "不发 COMMENT/STATUS\|不发任何 COMMENT/STATUS\|连 STATUS 都不发\|窗口满不发" docs/ code/` | 现行设计文件 0 命中；`lifecycle.json:122` =「inFlight ≥ 10：不发 COMMENT（重排队可发 1 条 STATUS）」、`:344` 补 5 分钟重排队；`html:4315`、`json:356` 补「可选快速填窗」；唯一命中是 `CHANGELOG-1.0.7.0.md:162,319`（描述本次改动本身，属合理引用） | 仓库 docs |
| P3-取消 | 取消不再吞掉 | 读代码 + 测试 | `DelayFastFillAsync` `:918 await Task.Delay(ms, ct)` 无 try/catch；测试 `FastFill_CancelledDuringDelay_PropagatesCancellation`（:637）断言抛 OCE 且只发 1 条 COMMENT，通过 | 仓库 :914-919 |
| P3-用例 | 三条停止路径入库 | grep 测试文件 | `FastFill_EmptyBarcode_StopsWithoutSending`(:572)、`FastFill_StatusCheckFails_StopsBeforeSelectingRecord`(:596)、`FastFill_SkipUpdateFails_StopsLoopAndReports`(:614) 三条均在且通过 | 测试文件 |
| 版本 | csproj 1.0.7.0 | grep Version | `:5-7 Version/AssemblyVersion/FileVersion = 1.0.7.0` | UniFlow.csproj |
| 包 | 1.0.7.0 md5 与 changelog 一致 | `md5sum`/`sha256sum`/`stat` | linux `c7e6e3eb…`、win `cbafe75d…`；sha256 `b5ee7875…`/`f4bd7e4b…`；大小 46312535/47140272 B —— 与 CHANGELOG §7.2 逐字一致 | release/ |
| 旧包未动 | 1.0.6.6/1.0.6.7 md5 | md5sum | 1.0.6.6 `5fa3daa8…`/`c109ef92…`、1.0.7 前版 `8c60ceef…`/`570458a3…` —— 与上轮复核记录一致 | release/ |
| CHANGELOG | 473/473、不夸大 | 全量重跑 + 读 | 我重跑：`Passed! - Failed: 0, Passed: 473, Skipped: 0, Total: 473`；§7「未做的环境级 E2E：真 MySQL/Aptio/SRM/冰箱/Windows Service/Docker」、§9「真实环境 E2E 未做 ⚠️ 必须现场验证」均在 | 仓库 + 测试运行 |
| HEAD/tag | b68994b、未 tag | `/usr/bin/git log -1; git tag -l` | `b68994b release: v1.0.6.7 …`；tags 最高 `v1.0.6.5`，无 1.0.6.6/1.0.6.7/1.0.7.0 tag | 仓库 |
| app.js 语法 | node --check | `node --check code/src/UniFlow/wwwroot/app.js` | 无输出 + `APPJS-SYNTAX-OK` | 仓库 |

## 2. 构建与测试（我重跑的实际数）

```
export HOME=/root DOTNET_ROOT=/usr/share/dotnet
/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release
→ 14 Warning(s), 0 Error(s)
/usr/share/dotnet/dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release
→ Passed! - Failed: 0, Passed: 473, Skipped: 0, Total: 473, Duration: 54 s
```
注意：PATH 上的 `dotnet` 是 SDK 8.0.133，**不能**构建（NETSDK1045, net10.0）；必须用 `/usr/share/dotnet/dotnet`（SDK 10.0.302），与 CHANGELOG §7 记录的命令一致。

## 3. 负向证据（/tmp 副本，仓库零写）

| 破坏方式 | 失败输出 |
|---|---|
| negA：`ClampFastFillIntervalMs` 改为直接返回（去 clamp），`/tmp/opencode/negA` | `Failed! - Failed: 1, Passed: 0` — `FastFill_ClampHelpers_EnforceDocumentedRanges` 在 :412 红（600000→期望 5000）。另：`IntervalMs600000` 用例在无 clamp 时**挂起**（真的 Task.Delay 100 分钟，420s 超时未归）→ 证明 clamp 是防挂死闸门，不是摆设 |
| negB：`FastFillMaxSkipsPerTick` 改 `int.MaxValue`（去上限），`/tmp/opencode/negB` | `Failed! - Failed: 1` — `Expected to receive exactly 2147483647 calls`，`FastFill_UnknownNodeSkip_StopsAfterMaxSkipsPerTick` 红（skip 不再停在 200，循环吞到队空） |

## 4. 工作区完整性（复核前后快照）

- `git status --porcelain` 前后 diff 为空（`STATUS-IDENTICAL`）；`git diff | sha256sum` 前后均 `b8e51dde07372b70`。
- 关键文件 md5 前后一致（csproj / DisposeSampleWorker.cs / app.js / lifecycle.json / html 均 OK）。
- status 内容 = 14 个 M + 6 个 ??（新测试文件、CHANGELOG-1.0.7.0、本目录两份报告、两个 1.0.7.0 包），与「快速填窗 + 1.0.7.0 发布工程」预期吻合；bin/obj 未出现在 status（gitignored）。
- HEAD `b68994b` 未变，无新 tag。

## 5. 新问题（分级）

- 无 P0/P1/P2。
- 【记录，不阻塞】`FastFill_UnknownNodeSkip_StopsAfterMaxSkipsPerTick` 用 `Received(FastFillMaxSkipsPerTick)` 与常量耦合，若未来改常量测试语义仍自洽，可接受。
- 【记录】负向 negA 显示 `FastFill_IntervalMs600000_EffectiveDelayCappedAt5Seconds` 在 clamp 缺失时会挂 ~100 分钟而非快速失败；建议未来给该用例加外层超时注解（可选）。

## 6. 放行建议

**放行（可提交）**。7/7 项通过，两条负向闸门均可红，仓库零写，包校验值与旧包 md5 全部对上。

## 7. 我没能验证的部分

- 真实环境 E2E（真 Aptio/SRM/MySQL/冰箱）：本环境无设备，CHANGELOG 已如实声明「必须现场验证」——需现场按 §9 清单补。
- Windows 包内容逐文件抽查（只验了外层 md5/sha256/大小与 changelog 一致，未展开 zip 逐文件比对）：可由发布方或下一轮用 `unzip -l` + 抽样 md5 补。
