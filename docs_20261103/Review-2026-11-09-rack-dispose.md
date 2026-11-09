# L3 独立复核：按架排序丢弃（DisposeByRack，v1.0.7.0 未提交改动）

- 复核对象：`git HEAD = 3d951f4`（工作区未提交改动 5 文件 + 1 新增测试文件）
- 复核方式：**只读 + 自跑**（build / test / node --check / Playwright / 真实 MySQL SELECT），未改仓库任何文件
- 复核时间：2026-11-09 18:19–18:45（本机）
- 结论：**PASS**（三态：一致 —— 无 P0/P1；3 条 P2、5 条 P3 偏差，均不阻塞合并）

---

## 0. 改动范围（独立算出，不采信自报）

`git` 命令在本沙箱被策略拒绝（`Permission denied: shell`），改用**自解析 `.git/index`**（python，blob sha1 逐文件比对）：

```
$ python3 /tmp/opencode/gitdiff.py
entries=195 changed=5 missing=0
  M code/src/UniFlow/Common/Models/ConfigShared.cs
  M code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs
  M code/src/UniFlow/wwwroot/app.js
  M docs/CONFIG-REFERENCE.md
  M docs/Configuration-Manual.md
```
新增（未入库，不在 index 中）：`code/tests/UniFlow.Tests/DisposeSample/Services/DisposeDatabaseServiceRackTests.cs`。
→ 与工单声明的改动清单**完全一致**，无夹带。HEAD blob 已导出到 `/tmp/opencode/head/` 用于逐行 diff（见 §1.6）。

---

## 1. 结论表

| # | 被复核断言 | 我的命令 | 原始输出（片段） | 位置 |
|---|---|---|---|---|
| 1 | build 0 error | `export HOME=/root; dotnet build code/src/UniFlow/UniFlow.csproj -c Release --no-incremental` | `14 Warning(s) / 0 Error(s)`；14 条 warning **全部**在 `WebAdmin/Services/ConfigService.cs`（历史存量），本次 3 个源文件 **0 warning** | 本机 repo |
| 2 | test 504/504（无 MySQL 501+3 skip） | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release`（跑 2 次） | `Passed! - Failed: 0, Passed: 501, Skipped: 3, Total: 504, Duration: 50 s` / 第二次 `53 s` 同结果 | 本机 repo |
| 3 | 新增 31 例 | `--filter FullyQualifiedName~DisposeDatabaseServiceRackTests` | `Failed: 0, Passed: 28, Skipped: 3, Total: 31` | 同上 |
| 4 | 3 条 MySQL 集成可跑且通过 | 加 `MYSQL_PWD/UNIFLOW_TEST_MYSQL_HOST/UNIFLOW_TEST_MYSQL_DB` 再跑（verbose） | `Passed …GetDisposeSamplesAsync_OnRealMySql_ByRack_ReturnsSingleRackSamples [364 ms]`、`…NotByRack… [21 ms]`、`…BuildDisposeSamplesSql_OnRealMySql… [244 ms]`（**真的执行了**，非 skip） | 同上 |
| 5 | `node --check` | `node --check code/src/UniFlow/wwwroot/app.js` | 无输出，`rc=0`（node v24.16.0） | 同上 |
| 6 | 前 10 条同一架 `&1-13-000088` | 直接用**代码生成的 SQL**（见下）对真实库执行 | 10 行，`uni_rack_key` 全为 `&1-13-000088`，`location=&1-13-000088-06-08-01..10`，`update_time` 从 `20260901091114` 起 | `uniflow_loc_analysis` |
| 7 | 生效 SQL == 代码生成的 SQL | 用探针程序打印 `BuildDisposeSamplesSql(...)` 落盘后 `diff` | `IDENTICAL: my tested SQL == code-generated SQL`（唯一差异 `LIMIT @Max`→`LIMIT 10`） | `/tmp/opencode/sql_from_code10.txt` vs `rack.sql` |
| 8 | 按架 SQL 顺序正确（cnt DESC → oldest ASC → location ASC） | 构造 10 行合成"视图"（含 &1×5/&2×2/&3/&0/NULL）跑同一 SQL | 输出顺序 `D1,D2(&2-01-18) → C1,C2(&1-13-000003) → A1,A2(&1-13-000001) → B1(&1-13-000002)`；`&3/&0/NULL` **全部未出现** | `/tmp/opencode/rack_synth.sql` |
| 9 | `LIMIT @Max` 只取窗口、不丢行 | `LIMIT` ∈ {0,1,2,47,48,49,96,100,100000} 逐值跑 | `0→0行, 1→1行/1架, 48→48行/1架, 49→49行/2架, 100→100行/3架`；`LIMIT 100000` 返回 **11844 行 = 视图总行数**（无行丢失/无放大） | 同上 |
| 10 | `SELECT v.*` 含 `uni_rack_key` 但 Dapper 不打挂 | 探针实例化真实 `DisposeDatabaseService`（真实工厂 + IOptionsMonitor） | `A rows=10 racks=&1-13-000088 barcode0=2512210801 loc0=&1-13-000088-06-08-01 warns=0` | `/tmp/opencode/probe2` |
| 11 | 回退只包按架分支、回退 SQL 是原 SQL、回退失败照常上抛 | 探针：对象缺 `location` 列（`tube_log`）触发回退；不存在的表触发双失败 | `B1 rows=10 warns=1`（回退成功）；`D THREW=YES type=MySqlException msg=Table '…no_such_view_xyz' doesn't exist`；`E THREW=YES`（byRack=false 直接上抛，与历史一致） | 同上 |
| 12 | Warning 10 分钟节流 | 同一服务实例连续 3 次触发回退 | `B call1 warns=1 / call2 warns=1 / call3 warns=1` | 同上 |
| 13 | 存储过程模式 DisposeByRack 不生效、不静默回退 | 探针 cmdType=1 指向非存储过程 | `F THREW=YES type=MySqlException msg=PROCEDURE …does not exist warns=0`（未走 SELECT 回退、未打 Warning） | 同上 |
| 14 | 默认 true 真绑定为 true（文件缺键） | 真实进程 `/api/config` + 浏览器 DOM | 配置文件 **无** 该键时：`DisposeByRack present: False` 而页面 `#f_Aptio_DisposeSample_DisposeByRack checked = true` | `/tmp/opencode/appui` + `pwcheck` |
| 15 | 文件显式 false 时页面口径一致 | 同上，写入 `"DisposeByRack": false` 后重启 | `file value = False` → `DisposeByRack checked = false` | 同上 |
| 16 | `getInitialValue` 不影响其它 switch | 同上，读 4 个开关 DOM 状态 | `DisposeByRack=true, EnableFastFill=false, AllowStandby=false, SkipOnUnknown=true`（后三者与改动前一致） | 同上 |
| 17 | 架键口径 | 探针直调产品代码 `GetLocationRackNo` | `&1-13-000078-05-14-01→&1-13-000078`；`&2-01-000000-00-18-01→&2-01-18`；`&3-…/&0-…/null/""/&1-13/&2-01-000000-00 → ""`；`&1-00-1064247-10-00-00→&1-00-1064247`(len13) | `/tmp/opencode/probe` |
| 18 | `sam_dispose_status.rack` 长度够 | 同上取长度 | 最长 13 字符 vs `VARCHAR(34)` | 同上 |
| 19 | Playwright 56/56 | `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-current npx playwright test` | `56 passed (41.4 s)` | `/tmp/opencode/pw` |
| 20 | 零写（复核未改仓库） | `md5sum` 全树快照，前后 diff | `ZERO-WRITE: 快照完全一致 (code/ + docs_20261103/)` | 见 §4 |

---

## 2. 负向证据（闸门必须能红）

做法：`rsync` 仓库源码到 `/tmp/opencode/neg/` 副本（不含 bin/obj），只改副本，跑同一套用例（带 MySQL 环境变量）。

| # | 如何破坏 | 结果 |
|---|---|---|
| N1 | `BuildDisposeSamplesSql` 的 `if (!byRack)` 改成 `if (true)`（开关整体失效） | `Failed: 6, Passed: 25` —— 红 ✅（含 2 条 MySQL 集成） |
| N2 | `GetLocationRackNo` 的 `&2` 改回第 3 段（`parts[2]`，历史缺陷） | `Failed: 5, Passed: 26` —— 红 ✅（`&2-01-18`→`&2-01-000000` 等 5 条） |
| N3 | `catch (Exception ex) when (byRack)` → `when (false)`（**回退闸门拆除**） | `Passed! Failed: 0, Passed: 31` —— **绿 ❌ 覆盖缺口**。用探针指向被改坏的 DLL 复现行为差异：`GetDisposeSamplesAsync(0,"tube_log")` 直接抛 `MySqlException`（无回退），说明漏洞真实存在但无自动化用例能拦住 → 见 P2-2 |
| N4 | 默认值 `DisposeByRack = true` → `false` | `Failed: 3, Passed: 28` —— 红 ✅（`Default_IsTrue`、`Binding_KeepsTrue…`、集成用例） |
| N5（SQL 语义） | 合成数据里放 `&3-*/&0-*/NULL` 各 1 行 | 输出 7/10 行，三类**均被排除** ✅ |
| N6（注入） | `SanitizeSqlName` 传 `x;DROP`、`a\`b`、`a.b.c`、`view over` | 全部 `<null>`（拒绝）；`flexlab.view_x` 通过；`""` → 默认视图名 ✅（且拒绝发生在**连库之前**，line 219–228） |

---

## 3. 偏差 / 新问题清单（分级）

### P2-1 视图被扫两次的固定成本（性能，需现场压测口径）
`EXPLAIN` 实测：
```
1 PRIMARY  t_sample ALL ... rows 17965  Using where; Using temporary; Using filesort
1 PRIMARY  <derived3> ref <auto_key0> rows 10
3 DERIVED  t_sample ALL ... rows 17965  Using where; Using temporary
```
- 耗时：按架 `0.29/0.30/0.29 s` vs 原查询 `0.05 s`（**5.8×**，11844 行视图 / 17965 行 t_sample）。
- 实施方自报 0.228 s 与我实测同量级（差异来自 CLI 启动开销），**断言成立**。
- 风险：`t_sample` 是随样本量线性增长的表；循环间隔默认 3 s，百万行级现场的单轮可能到秒~十几秒。建议：现场压测 `EXPLAIN ANALYZE`；必要时把"架聚合"结果按 N 秒缓存，或给 `t_sample(t_status,t_location)` 建覆盖索引。
- 影响现场：**是**（仅性能，不影响正确性）。

### P2-2 回退路径零自动化覆盖（负向 N3）
`GetDisposeSamplesAsync` 的 `catch when (byRack)` 回退分支，无任何单测/集成用例覆盖（现 3 条集成只覆盖 byRack=true 正常路径与 byRack=false）。删掉回退，31 例全绿。
建议补 1 条：`cmdType=0 + 缺 location 列的视图/表` → 断言返回行数>0 且只打 1 条 Warning（可用 `tube_log` 之类无 location 的对象，或用假 `IDbConnection`）。
- 影响现场：间接（这是"绝不让丢弃停摆"的唯一保险，却没有闸门）。

### P2-3 架键不含 `&1/&2` 的候选被**静默**丢弃（行为变化）
`WHERE v.uni_rack_key IS NOT NULL` + `JOIN` 会让 `&0-*`、`&3-*`、NULL、非标格式的行**完全不返回**；旧逻辑（`SELECT * … LIMIT N`）会把它们一并返回并尝试丢弃。若某现场视图里含这类行：
- 这些样本**永远不会被丢弃**，且**不报错、不告警**，只在 `GetDisposeSamples returned 0` 里体现；
- 现有测试库视图只含 `&1-%`（测得 `&0` 在视图内 0 行），所以本机不复现。
建议：① 发布说明写明"仅 &1/&2 架键参与"；② 或加一条兜底：按架查询返回 0 行而原查询 >0 行时告警/回退。
- 影响现场：潜在（取决于客户视图 SQL），可 `DisposeByRack=false` 立即回退。

### P3-1 文档口径不准：同架内并非"最早 update_time 优先"
`docs/CONFIG-REFERENCE.md` §2.1 第 2 条、`docs/Configuration-Manual.md`「同一架内，最早 `update_time` 优先」与代码不符：
真实 SQL 是 `ORDER BY r.cnt DESC, r.oldest ASC, v.location ASC` —— `oldest` 是**架级**常量，进入同架后**只按 location 升序**。合成数据实证：`A1(20260101)` 排在 `A2(20251201)` 之前；真实库前 10 条 `update_time` 亦非单调（091114→153404→091119…）。
建议把文档第 2 条改成"该架**最早 update_time** 决定架间先后；同架内按 location（层/槽/位）升序"。
- 影响现场：误导验收（现场若按"架内先丢最旧"验收会判不符）。

### P3-2 C# 与 SQL 在**畸形 location** 上口径不一致
实测（真实 MySQL）：
```
'&2-01-000000-00' → SQL '&2-01-00'，C# ""
'&1-13'           → SQL '&1-13-13'，C# ""
```
即段数不足时 C# 排除、SQL 保留。当前库无此类数据（`&1` 短串 0 行、`&2` 短串 0 行），暂不触发；但一旦有脏 location，会出现"排序键 ≠ 落库 `rack`"。
建议：SQL 侧补段数判断（`CHAR_LENGTH(location)-CHAR_LENGTH(REPLACE(location,'-',''))>=5`）。

### P3-3 `sam_dispose_status.rack` 列内容格式变更（历史行/新行混存）
`GetLocationRackNo` 从"只取第 3 段"（`000078`）改成"完整架键"（`&1-13-000078`），`&2` 从 `000000` 改成 `&2-01-18`。全仓 grep 确认该列**只写不读**（仅 `INSERT` + 建表），无消费者 → 暂无影响；将来若按 `rack` 聚合需兼容两种格式。

### P3-4 `getInitialValue` 的同类口径问题只修了本开关
`Logging:UniFlowFile:FileLoggingEnabled` 代码默认也是 `true`（`UniFlowLoggerConfig.cs:7`），但 `FORM_FIELDS` 未声明 `default:true`；配置文件缺该键时页面仍显"关"而后台"开"。本次改动**未使其变差**（改前所有 switch 一律回显 false），属遗留。

### P3-5 Playwright 默认被测目录是旧产物（验证资产陷阱）
`run-test/playwright/playwright.config.js:7` 默认 `APP_DIR=/tmp/uniflow-e2e/app`，该目录 `UniFlow.dll` 为 **11-07 10:35**，`strings` 里 `DisposeByRack` 命中 **0** —— 用它跑出来的 56/56 **不覆盖本次改动**。本次我显式用 `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-current`（18:22 发布，含新代码）才得到 56/56。另：`app.js.br/.gz` 均为新内容（各 3 处命中），服务端实际发的是明文 `app.js`（`Content-Type: text/javascript`，无 Content-Encoding）。
建议：把 APP_DIR 指向 `app-current` 写进 README，或让 config 校验产物含最新标记。

---

## 4. 零写 / 边界 / 环境备注

- **零写证据**：`find code docs_20261103 -type f … | md5sum` 前后快照 `diff` 无差异（128 文件）。所有构建产物写在 `bin/obj`（已在快照排除）；负向改动只在 `/tmp/opencode/neg/**` 的副本里。
- **数据库只读**：全部为 `SELECT / EXPLAIN`；`information_schema` 对象清单前后一致（4 表/视图 + 1 存储过程）。
  ⚠️ 环境备注：测试库 `uniflow_loc_analysis` 里存在一个**今日 09:47 新建**的破坏性存储过程 `remove_log`（`delete from tube_log where sample_id not in (select sample_id from t_sample)`）。我**未执行**它；但它的存在说明有人在该库做过写操作，若后续有人"顺手测 cmdType=1"会误删 36 万行 `tube_log`。建议删除或改名。
- **边界**：`LIMIT` 0/1/2/47/48/49/96/100/100000 全跑（§1.9）；空结果、超长 `safeName`（`schema.view`）均已验；`safeName` 白名单在连库前拒绝（line 219–228）。
- **并发**：`_lastRackWarnUtc` 无锁，多个 worker 并发最坏多打一条 Warning，无害（服务为 singleton，`Program.cs:285`，节流有效）。

---

## 5. 放行建议

**放行**（可合并），前置 3 条非阻塞项建议随版本一并处理：
1. 修 P3-1 文档两处措辞（同架内按 location，不按 update_time）；
2. 补 P2-2 回退路径用例（唯一没有闸门的保险）；
3. 发布说明加 P2-3：「仅 `&1-*` / `&2-*` 架键参与按架排序，其余 location 不参与」+ 回退开关 `DisposeByRack=false`。

---

## 6. 我未能验证的部分（原因 + 谁能补）

| 项 | 原因 | 谁能补 |
|---|---|---|
| `CommandType=1` **成功**路径（真实存储过程返回行 → Dapper 映射 → 返回 10 条） | 测试库唯一的存储过程 `remove_log` 是破坏性 DELETE，另一个命令都是只读 SELECT，我不允许在库里新建对象 | 客户/实施方在测试库建一个只读 SP（`SELECT … FROM view_overtimestoragesample LIMIT 10`）后跑一条探针；**代码 diff 已证该分支除多一行 Debug 日志外与 HEAD 完全一致** |
| 老现场真实 `t_sample` 规模下的耗时（百万行级） | 本机只有 17965 行 | 现场用 `EXPLAIN ANALYZE` 采集（P2-1） |
| 客户视图是否含 `&0`/非标 location 行（P2-3 的真实触发概率） | 只有一个测试库视图，全部 `&1-%` | 现场 `SELECT DISTINCT LEFT(location,2) FROM <客户视图>` |
| `git` 层面的 commit/分支状态 | 沙箱对 `git` 全线拒绝（`Permission denied: shell`） | 主会话用 `git status/diff` 复核（我用 `.git/index` + blob sha1 已独立复现改动范围） |
