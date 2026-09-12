# UniFlow 代码审查报告（v1.0.5.9 后）

- 日期：2026-09-10
- 对象：`/root/opencode_project/UniFlow` 当前源码（`code/src/UniFlow`）
- 依据：
  - 项目文档：`README.md`、`docs/CONFIG-REFERENCE.md`、`docs/Functional-Design.md`
  - 测试报告链：`/mnt/nasbk/UniFlow/UniFlow-1.0.3 ~ 1.0.5.9 部署测试报告 / 修复记录`
- 方法：逐模块源码审阅，对照需求与测试报告遗留问题交叉验证。

---

## 一、需求背景

核心业务为 **DMS 样本清理（`SampleCleanup`）**：按 `测试名:超时分钟` 触发，先由 `sid` 经 `reqtube` 换取 `oid`，再删除该样本在各关联表的记录（含仅含 `codoid` 的 `orders` / `orders_details` / `orders_tat`）。

近期版本修复轨迹：
- v1.0.5.8：修复 orders 族误带 `codsid` 的删除 SQL；引入 `sid→oid`。
- v1.0.5.9：新增专属错误收集 Worker（生产者-消费者），修正删除汇总日志，orders 族删除逻辑实测通过。

---

## 二、已正确实现（与报告修复项一致）

| 项 | 位置 | 结论 |
|---|---|---|
| orders 族按表列组合条件删除 | `SampleCleanupService.cs:79-88` | ✅ 精确表名 + 逐表 `codsid`/`codoid` AND，与报告一致 |
| 专属错误收集 Worker | `ErrorReporter.cs` / `ErrorCollectorWorker.cs` / `DmsDatabaseService.cs:121` | ✅ DMS 删除失败入队上报，进入 SQLite/Web 错误库 |
| 删除汇总区分成功/失败 | `DmsDatabaseService.cs:125-130` | ✅ `Sample deleted` vs `Sample delete incomplete`（Warning） |
| Router 精确去重 + 两阶段扫描 | `AptioTaskRouter.cs` | ✅ 基于扫描结果去重，`PushTtl` 兜底 |
| 生产者-消费者（扫描/推送/消费） | `AptioBatchScannerWorker` + 各 Worker | ✅ 与 v1.0.5.6 改动记录一致 |

---

## 三、问题清单（按优先级）

### 🔴 P0 — `DeleteTableList` 配置完全未生效

**位置**：`SampleCleanupService.InitDeleteSqlAsync`（`SampleCleanupService.cs:69-95`）

**现象**：构建删除模板时仅用 `GetTableColumnsAsync("codsid")` / `("codoid")` 扫描出**全部**含相应列的表，从未读取 `_config.SampleCleanup.DeleteTableList`。

**佐证**：该字段仅出现在 3 处——
- 定义：`DmsOrderConfig.cs:35`
- 配置：`appsettings.json:149`（值为 `""`）
- 文档：`docs/CONFIG-REFERENCE.md:217`（"删除涉及的表列表"）

业务逻辑零引用。即 v1.0.5.9 部署测试报告"备注 1"（`DeleteTableList` 未限制删除范围）所指的遗留项，至今未处理。

**后果**：生产 dms 库 100+ 张表 → 每次删除样本都对 92~93 张表逐表 `DELETE`，性能差；配置形同虚设，运维误以为已限定删除范围。

**建议**：`DeleteTableList` 非空时作为**白名单**（分号分隔，如 `reqtest;reqtube;orders;orders_details;orders_tat;reqtestresult`），与动态扫描结果求交集；为空时才回退到"全部含 `codsid`/`codoid` 的表"。

---

### 🟠 P1 — 删除 SQL 存在注入 / 断裂风险

**位置**：`DmsDatabaseService.DeleteSampleAsync`（`DmsDatabaseService.cs:106`）

**现象**：`string.Format(template, sid, oid)` 将 `sid`/`oid` 直接拼进 SQL，模板形如 `... WHERE codsid = '{0}'`。`sid`/`oid` 取自 DB（`reqtest`/`reqtube`），一旦值含单引号即语法错误或可被注入。

**对比**：其余模块（`GetOidBySidAsync`、`StatusCorrectionService` 等）均走 Dapper 参数化，此处为例外。

**建议**：模板改为占位符形式（`WHERE codsid = @sid AND codoid = @oid`），`ExecuteAsync(sql, new { sid, oid })`。

---

### 🟠 P1 — StatusCorrection "模式 1 = 不修正"仍执行破坏性删除

**位置**：`StatusCorrectionService.ExecuteAsync`（`StatusCorrectionService.cs:27-38`）

**现象**：在进入模式判断之前，无条件执行
```sql
DELETE FROM {db}.reqtestresult WHERE flgstatus IN ('E','R','P') OR valresult1 = ''
```
随后才 `if (AutoModifyTestStatus == "1") return`。而 `CONFIG-REFERENCE.md:209` 标注 `1=不修正`，但实际仍删数据。

**风险**：语义与文档矛盾，运维选择"不修正"仍会删除 `E/R/P`/空结果记录。

**建议**：与需求确认。若"模式 1"应为纯只读，则将该 `DELETE` 移入模式 2/3 分支（或独立开关）。

---

### 🟡 P2 — 杂项

1. **配置获取方式并存**（`Program.cs:47` 与 `:40`）：同时 `AddSingleton(aptio)`（启动期实例）与 `Configure<AptioConfig>`。`DisposeDatabaseService` 等用启动期构建的 `aptioFactory`，改 `Aptio.Database` 必须重启（已文档化）。建议统一为 `IOptions<AptioConfig>`，避免两套取值来源混淆。

2. **`SampleCleanupService.ExecuteAsync` 外层异常不上报**（`SampleCleanupService.cs:43-46`）：外层 `catch` 仅 `_logger.LogError`，未 `_errors.Report`；与内层 per-test 上报（`:134`）不一致。`InitDeleteSqlAsync` 失败（连不上库）时模板保持旧值且无告警。

3. **删除模板缓存 1 天**（`SampleCleanupService.cs:18`，`DeleteSqlRefreshInterval`）：新增表需等待最长一天才被纳入，与 v1.0.5.9 报告"备注 2"观察一致。若需即时纳入，可在配置变更或首轮执行时 `force` 重建。

---

## 四、结论

v1.0.5.9 的三项核心修复（orders 族删除、专属错误收集 Worker、删除汇总日志）在当前源码中**均已正确落地**。

遗留且应优先处理的是 **P0：`DeleteTableList` 未生效** —— 这是测试报告明确指出、且直接影响生产删除范围与性能的问题。次优先为两处 P1（删除 SQL 参数化、StatusCorrection 模式 1 语义）。

### 修复优先级建议

| 优先级 | 项 | 影响 |
|---|---|---|
| P0 | `DeleteTableList` 白名单生效 | 生产删除范围/性能，报告遗留 |
| P1 | 删除 SQL 参数化 | 健壮性/注入面 |
| P1 | StatusCorrection 模式 1 语义对齐 | 破坏性操作语义 |
| P2 | 配置统一 / 异常上报 / 模板缓存 | 可维护性 |
