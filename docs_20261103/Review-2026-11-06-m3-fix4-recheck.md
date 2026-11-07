# L3 复验报告 — P1 大小写绕过修复（二轮，HEAD 6d684fa + 未提交修复）

- 复核人：reviewer（GLM-5.3-flash，独立于实施方）
- 日期：2026-11-06
- 结论：**PASS**。实施方对 P1 的修复经读码 + 317 全量重跑 + /tmp 副本负向红证全部成立；负向红数（12）与实施方声明一致；Playwright 43 用例数经工具独立证实。
- 仓库零写：负向验证在 /tmp 副本完成并删除；复核后 `grep MUTATION`=0、`out var exact` 在位、`out cur` 不存在、app.js 仍过 `node --check`。

## 1) 结论表

| # | 被复核断言 | 我的命令 | 原始输出片段 | 执行位置 |
|---|---|---|---|---|
| 1 | build 0 error | `dotnet build code/src/UniFlow/UniFlow.csproj -c Release` | `2 Warning(s) 0 Error(s)`（仅 NU1903 依赖告警） | 本机仓库 |
| 1 | test 317/317 | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 317, Skipped: 0, Total: 317`（300→317，+17 与"新增 17 测试"吻合） | 本机仓库 |
| 1 | node check | `node --check code/src/UniFlow/wwwroot/app.js` | 通过 | wwwroot/app.js |
| 2 | Navigate 不再覆盖 cur、回退命中 | 读码 | L446-456：`TryGetProperty(seg, out var exact)` 独立变量 → 精确命中；未命中在**原 cur** 上 `EnumerateObject` + `OrdinalIgnoreCase` 匹配；`hit.Name == null → return null`；空路径 L437-438 返回 null；注释明示旧坑（L432-433） | ConfigService.cs:434-459 |
| 2 | ReadPaths 单条独立 try/catch | 读码 + 测试 | L407-420 每条 path 独立 try/catch（失败记 null 并 Warning，不拖垮整批）；`ReadPaths_OneBadPath_DoesNotBlank_OtherPaths` 过（`nope:nope`→null，`logging:loglevel`/`Aptio:Ip` 仍命中） | ConfigService.cs:396-428 |
| 2 | ReadPath("")→null 无调用方依赖 | grep 调用方 | 唯一按用户输入调用的 `GET /api/config?path=` 在 L282 先 `IsNullOrWhiteSpace` 拦截（空值走全量，不进 ReadPath）；其余调用点（L95-101、L354、DatabaseConnectionTester L183/192）均为字面量路径；测试 ConfigServicePathCaseTests:68 锁死语义 | ApiEndpoints.cs, ConfigService.cs |
| 2 | 单条大小写变体 → 400 文件不变 | 读测试（317 内） | `[InlineData("logging:loglevel"/"LOGGING:LOGLEVEL"/"Logging:loglevel")]` → 400、`DeepEquals(before, after)`、LogLevel 仍为对象；`Aptio:database`→400 | ConfigSegmentNodeTests.cs:150-215 |
| 2 | batch 大小写变体 → 400 且一个不写 | 读测试 | `BatchConfig_CaseVariant_SegmentNode_Returns400_AndNothingWritten`：合法项 `Aptio:Ip` 也未写（仍 127.0.0.1） | ConfigSegmentNodeTests.cs:176-198 |
| 2 | 叶子大小写变体 200、无影子键 | 读测试 | `logging:loglevel:default`→200 且 `Logging:LogLevel:Default=="Warning"`、LogLevel 子键数仍为 2；`aptio:srmnodeids`→200 且写回 `Aptio:SrmNodeIds`（无影子键） | ConfigSegmentNodeTests.cs:218-245 |
| 2 | 大小写读取语义 | 读测试 | ConfigServicePathCaseTests 9 用例全过：4 个大小写变体命中同一对象节点、未知路径 null 不抛、混合 batch 各取各值 | ConfigServicePathCaseTests.cs |
| 2 | GET 打码不受影响 | 读码（无小写密码 path 专项测试，见 §5） | GET 处理器 `IsSecretKey(path)` 用 OrdinalIgnoreCase（ConfigRedactor.cs:24-27）→ `?path=aptio:database:password` 仍打码；`RedactElement` 键名遮蔽同样大小写不敏感；ConfigRedactionApiTests 8 用例全过（在 317 内） | ApiEndpoints.cs:287-295 |
| 3 | 负向：去掉大小写回退 | /tmp 副本把 Navigate 改回 `if (!TryGetProperty(seg, out cur)) return null;` | **Failed: 12, Passed: 12**（12 个红 = 3 个 InlineData 段节点 + batch + 叶子变体 + ConfigServicePathCaseTests 7 个读取用例）——与实施方"12 用例红"**完全一致** | /tmp/opencode/review_v2（已删除） |
| 4 | Playwright 43 | `node node_modules/.bin/playwright test --list`（/tmp/uniflow-e2e/playwright，spec 与仓库 md5 一致：fada56b7/6748bc37） | `Total: 43 tests in 2 files`；`.last-run.json = {"status":"passed","failedTests":[]}`（主会话实跑残留） | /tmp/uniflow-e2e/playwright |

更正上轮记录：我上轮静态 `grep "test("` 得 33 是**计数方法错误**（playwright 官方 --list 为准 = 43，与实施方声明一致，非实施方偏差）。

## 2) 负向证据
- 把 Navigate 回退逻辑移除、恢复 `out cur` 形态 → 12 用例红（含安全关键的 `PutConfig_CaseVariant_SegmentNode_Returns400_AndFileUnchanged` 3 个变体与 batch 变体）——闸门可红，红数与声明一致。
- 仓库零写证明：mutation 仅写入 /tmp 副本；删除后 grep `MUTATION`=0、`out var exact` 仍在、`out cur`=0、app.js 语法检查通过。

## 3) 新问题
- **P3（补测建议）**：`GET /api/config?path=` 的小写**密码**变体（如 `aptio:database:password`）无专项测试；静态核对 `IsSecretKey`/`RedactElement` 均大小写不敏感、不会泄露，但既然 ReadPath 现在大小写不敏感（原来会 404，现在会返回节点），建议补一条小写密码 path → "***" 的回归测试锁死。
- 无 P0/P1/P2 新发现。

## 4) 放行建议
**放行**。317/317 全绿、负向 12 红与声明一致、P1 修复点（Navigate out 参数 + ReadPaths 逐条隔离）均落实并有回归测试。上轮遗留两项条件：P1 已修（本报告证实）；43 用例数已证实（我方计数方法错误，非实施方问题）。

## 5) 未验证项
- Playwright **实跑**由主会话执行，我复用的是其产物（spec md5 一致 + `--list`=43 + `.last-run.json=passed`）；报告 HTML 为自包含压缩数据，无法静态解出逐用例结果。
- 真实进程冒烟"4 个大小写变体全 400"未复现（实施方在真实进程做；等价的 InMemory TestWebHost 用例已覆盖并全绿）。
- docker build / 容器实跑仍未做（与本 P1 无关，属上轮遗留项）。

—— reviewer，本报告唯一写入文件：docs_20261103/Review-2026-11-06-m3-fix4-recheck.md
