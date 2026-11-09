DONE

# 修复报告：按架排序复核 P2-2 / P2-3 / P3-1 / P3-2（P2-1 仅文档）

- 复核来源：`docs_20261103/Review-2026-11-09-rack-dispose.md`（PASS，无 P0/P1；3×P2 + 5×P3）
- 基线：`git HEAD = 3d951f4` + 工作区未提交的按架排序改动（5 文件 + 1 新增测试文件）
- 范围：**只改**下列 4 个文件 + 本报告；**未 commit / 未 tag / 未改版本号**；
  未触碰 `archives/**`、`docs/CHANGELOG-*`、`docs/Maintenance-Order-*`、`docs/Fix-Report-*`、
  `docs/dispose-state-machine.*`
- 环境：`/usr/share/dotnet/dotnet`（.NET 10.0.302），`HOME=/root`；
  MySQL 测试库 `uniflow_loc_analysis` @ 127.0.0.1:3306（`UNIFLOW_TEST_MYSQL_HOST/DB` + `MYSQL_PWD`）
- 结论：**DONE**（P2-2 / P2-3 / P3-1 / P3-2 已修并自验；P2-1 仅补文档说明，未改代码）

---

## 0. 改动清单（file:line）

| 文件 | 位置 | 改动 |
|---|---|---|
| `code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs` | 177–184 | `DisposeRackKeyExpression`：`&1/&2` 要求 6 段；`&3` → NULL；其余 → `'~other'` |
| 同上 | 225 | `ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC, r.cnt DESC, r.oldest ASC, v.location ASC` |
| 同上 | 763–774 | `GetLocationRackNo`：改为**要求 6 段**（`parts.Length < 6 → ""`），`&1` 取第 2/3 段、`&2` 取第 2/5 段 |
| 同上 | 161–176 / 186–199 / 749–762 | XML 文档注释同步新口径（含"两侧都不丢弃"说明） |
| `code/tests/UniFlow.Tests/DisposeSample/Services/DisposeDatabaseServiceRackTests.cs` | 35–70 | 文本断言改新口径（含 `~other` / `&3` NULL / 新 ORDER BY） |
| 同上 | 130–137 | `GetLocationRackNo_ExcludedLocations_ReturnEmpty` 增短格式用例 `&1-13-000078` / `&2-01-000000-00` |
| 同上 | 271–274, 317–364 | 真实 MySQL：合成视图验证 `~other` 组（保留、排最后）+ 同架内 location 升序 |
| 同上 | 384–424 | **新增** MySQL 集成测试：按架 SQL 失败 → 自动回退原查询（P2-2） |
| `docs/CONFIG-REFERENCE.md` | 89, 189–224 | P3-1 措辞改正 + `~other` 说明 + P2-1 性能说明 |
| `docs/Configuration-Manual.md` | 229–267 | 同上 |

---

## 1. P2-3：`&1/&2` 之外的位置被静默丢弃 → **已修（不静默丢弃）**

### 1.1 SQL 侧（`DisposeDatabaseService.cs:177-184`）

```
CASE
  WHEN location LIKE '&1-%-%-%-%-%' THEN CONCAT('&1-', …第2段…, '-', …第3段…)
  WHEN location LIKE '&2-%-%-%-%-%' THEN CONCAT('&2-', …第2段…, '-', …第5段…)
  WHEN location LIKE '&3-%' THEN NULL
  ELSE '~other'
END
```

- `&1`/`&2` 的 `LIKE` 带 **5 个 `-`** → 要求 **6 段**，与 C# 侧 `parts.Length < 6` 守卫同口径；
- `&3`（已丢弃哨兵）→ `NULL` → 被 `WHERE v.uni_rack_key IS NOT NULL` 排除（**仍排除**）；
- `&0` / `NULL` / 其它前缀 / **短格式** → `'~other'`（**保留**，不丢）。

### 1.2 排序（`DisposeDatabaseService.cs:225`）

```
ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC, r.cnt DESC, r.oldest ASC, v.location ASC
```

首个条件把"其它组"**一律压到最后**（否则 `~other` 组 cnt 大时会被 `cnt DESC` 顶到最前）。

### 1.3 C# 侧（`DisposeDatabaseService.cs:763-774`）

```csharp
if (parts.Length < 6) return "";
if (parts[0] == "&1") return $"&1-{parts[1]}-{parts[2]}";
if (parts[0] == "&2") return $"&2-{parts[1]}-{parts[4]}";
return "";
```

### 1.4 真机 MySQL 实证（合成视图，9 行 → 8 行；`&3` 被排除，其余一行没丢）

```
$ mysql -h 127.0.0.1 -u root -D uniflow_loc_analysis   # 合成视图 view_rack_other_test
barcode  location                  uni_rack_key
A1       &1-13-000001-01-01-01     &1-13-000001
A2       &1-13-000001-01-01-02     &1-13-000001
B1       &1-13-000002-01-01-01     &1-13-000002
C1       &2-01-000000-00-18-01     &2-01-18
E1       NULL                      ~other
D1       &0-00-000000-00-00-00     ~other
G1       &1-13-000078              ~other      ← 短格式
H1       &2-01-000000-00           ~other      ← 短格式
```

- `F1`（`&3-13-000000-00-00-00`）**未出现** → `&3` 仍排除 ✔
- `D1`(`&0`) / `E1`(NULL) / `G1` / `H1`(短格式) **全部保留且排最后** ✔
- `A1` 在 `A2` 前（A2 的 `update_time` 更早）→ **同架内按 location 升序，不是 update_time** ✔（P3-1 实证）
- `B1` 在 `C1` 前（架级最早 `update_time`）→ **update_time 只用于架之间** ✔

该段已固化为自动化用例：`DisposeDatabaseServiceRackTests.cs:317`（`AssertOtherLocationsKeptAndSortedLastAsync`，
由 `:271` 的用例调用）。

---

## 2. P2-2：回退分支零自动化覆盖 → **已修（新增 1 条 MySQL 集成测试）**

新增 `DisposeDatabaseServiceRackTests.cs:384`
`GetDisposeSamplesAsync_OnRealMySql_RackQueryFails_FallsBackToPlainQuery`：

1. 在测试库建**没有 `update_time` 列**的视图 `view_rack_fallback_test`（`:395`）
   → 按架 SQL 内层聚合 `MIN(update_time)` 必然报 `Unknown column 'update_time' in 'field list'`；
2. 用 `DisposeByRack=true`（`AptioConfig` 默认）的 `DisposeDatabaseService` 调
   `GetDisposeSamplesAsync(0, "view_rack_fallback_test", 10)`；
3. 断言**不抛异常**且 `samples.Count == 10`（回退到 plain 查询成功）；
4. `finally` 里 `DROP VIEW IF EXISTS view_rack_fallback_test`。

**边界**：只在测试库建/删**临时视图**，不建表、不改表数据、不碰生产库；视图已删（见 §5.4）。

---

## 3. P3-1：文档口径不准 → **已改正**

- `docs/CONFIG-REFERENCE.md:201-209` / `docs/Configuration-Manual.md:229-237`：
  原文"同一架内，**最早 `update_time` 优先**" → 改为
  「**同架内按 `location` 升序**（位置 01→48）；`update_time` 只决定**架与架之间**的先后（该架最早 `update_time`）」，
  并各加一条 ⚠️ 口径提醒。
- `docs/CONFIG-REFERENCE.md:89`：配置表里的"`&0`/NULL **排除**"改为"归 `~other` 组**保留并排最后**（不静默丢弃）"。

## 4. P3-2：畸形 location 两侧口径 → **已统一（可接受）**

6 段守卫（SQL `LIKE '&1-%-%-%-%-%'` / C# `parts.Length < 6`）让两侧**都不产生错误架键**：

| 短格式 | SQL 侧 | C# 侧 | 是否丢弃 |
|---|---|---|---|
| `&1-13-000078`（3 段） | `~other`（保留，排最后） | `""` | **否** |
| `&2-01-000000-00`（4 段） | `~other`（保留，排最后） | `""` | **否** |
| `&1-13` / `&2-01-000000-00-18` 等更短 | `~other` | `""` | **否** |

两者都"不丢弃"，口径一致 → **可接受**。该说明已同时写入两份文档
（`docs/CONFIG-REFERENCE.md:198-199`、`docs/Configuration-Manual.md:250-251`）与本报告。

## 5. P2-1：视图被扫两次的固定成本 → **仅文档说明（未改代码）**

`docs/CONFIG-REFERENCE.md:221-224` / `docs/Configuration-Manual.md:264-267` 新增"性能提示"：
按架 SQL 扫视图两次 → 14k 行实测约 **0.2~0.3 s**（原约 **0.05 s**）；对 3 s 循环可接受；
现场视图很大时建议 `EXPLAIN ANALYZE`，或临时 `DisposeByRack=false` 回退。

---

## 6. 验收证据

### 6.1 build（0 error）

```
$ export HOME=/root; dotnet build code/src/UniFlow/UniFlow.csproj -c Release --no-incremental
    14 Warning(s)
    0 Error(s)
```
14 条 warning **全部**为历史存量（`WebAdmin/Services/ConfigService.cs`、`WorkListCleaner/Services/ImmuliteCleaner.cs`、
NU1902/NU1903 依赖告警）；**本次改动的 2 个源文件 0 warning**：

```
$ dotnet build … | grep warning | grep -c "DisposeDatabaseService.cs"           → 0
$ dotnet build … | grep warning | grep -c "DisposeDatabaseServiceRackTests.cs" → 0
```

### 6.2 test

| 环境 | 结果 |
|---|---|
| 有 MySQL（`UNIFLOW_TEST_MYSQL_HOST=127.0.0.1 UNIFLOW_TEST_MYSQL_DB=uniflow_loc_analysis MYSQL_PWD=…`） | `Passed! - Failed: 0, Passed: 506, Skipped: 0, Total: 506` |
| 无 MySQL | `Passed! - Failed: 0, Passed: 502, Skipped: 4, Total: 506` |

按架相关子集（`--filter FullyQualifiedName~DisposeDatabaseServiceRackTests`，有 MySQL）：
`Failed: 0, Passed: 33, Skipped: 0` —— 其中 4 条 MySQL 集成**真的执行了**（非 skip）：

```
Passed …BuildDisposeSamplesSql_OnRealMySql_OrdersByRack_AndKeepsOtherLocations [346 ms]
Passed …GetDisposeSamplesAsync_OnRealMySql_ByRack_ReturnsSingleRackSamples       [302 ms]
Passed …GetDisposeSamplesAsync_OnRealMySql_NotByRack_ReturnsPlainOrder           [ 20 ms]
Passed …GetDisposeSamplesAsync_OnRealMySql_RackQueryFails_FallsBackToPlainQuery  [ 68 ms]
```

**与工单验收数的差异（说明，非缺陷）**：工单写"无 MySQL 501+3 skip / 有 MySQL 505/505（新增 1 条集成）"。
实际为 **506**，差 +1，成因是工单同时要求的短格式用例 `&1-13-000078`（Theory **新增 1 个 InlineData**）
与"新增 1 条集成"合计 +2（504 → 506）；其中 `&2-01-000000-00` 早已存在于基线，未重复添加。
无 MySQL 时新增的集成用例按设计 skip → skip 由 3 变 4（3 原有 + 1 新增）。**除这 +2 例外，其余 504 例行为与基线完全一致。**

### 6.3 `node --check`

```
$ node --check code/src/UniFlow/wwwroot/app.js
NODE_CHECK_OK rc=0        # 无输出，rc=0
```
（本次未改 `app.js`。）

### 6.4 负向（/tmp 副本，恢复后证明仓库零写）

| # | 如何破坏 | 结果 |
|---|---|---|
| N1 | 副本里 `catch (Exception ex) when (byRack)` → `when (false)`（**拆掉回退闸门**） | `Failed! - Failed: 1, Passed: 32, Skipped: 0, Total: 33` —— 红 ✔<br>失败用例：`GetDisposeSamplesAsync_OnRealMySql_RackQueryFails_FallsBackToPlainQuery`（`MySqlException : Unknown column 'update_time' in 'field list'`）→ **P2-2 的覆盖缺口已闭合**（复核 N3 原本是"全绿"） |
| N2 | 副本里删掉 `WHEN location LIKE '&3-%' THEN NULL` 且 `ELSE '~other'` → `ELSE NULL`（**去掉 `~other`/`&3` 排除**） | `Failed! - Failed: 2, Passed: 31, Skipped: 0, Total: 33` —— 红 ✔<br>失败用例：`BuildDisposeSamplesSql_ByRack_HasAmp1AndAmp2CaseBranches`（`sql should contain`）、`BuildDisposeSamplesSql_OnRealMySql_OrdersByRack_AndKeepsOtherLocations`（`barcodes.Count`） |

副本目录 `/tmp/opencode/neg1`、`/tmp/opencode/neg2` 已删除；仓库内的
`DisposeDatabaseService.cs` 不含上述任何变异（见 §6.5）。

### 6.5 零写与边界

- 变更文件**仅 4 个**（2 源码 + 2 文档）+ 本报告；与基线快照逐文件 md5 比对，差异集合 =
  `{DisposeDatabaseService.cs, DisposeDatabaseServiceRackTests.cs, CONFIG-REFERENCE.md, Configuration-Manual.md}`。
- `git` 在本沙箱被策略拒绝（`Permission denied: shell`）→ 改用 `.git` 无关的 **md5 快照比对**
  （`find code docs docs_20261103 -type f -not -path '*/bin/*' -not -path '*/obj/*' | xargs md5sum`），
  基线快照 152 个文件。
- 仓库内无负向变异残留：`grep -n "when (false)" …/DisposeDatabaseService.cs` → 无匹配；
  `grep -n "ELSE '~other'" …` → 1 处（`DisposeDatabaseService.cs:182`）。
- 数据库只读/最小写：仅 `SELECT` + 在测试库建/删 2 个临时视图
  （`view_rack_other_test`、`view_rack_fallback_test`，均由 `finally` 删除）。
  测试库对象清单与改动前一致（4 表/视图），未新建表、未改任何表数据。
- 未改版本号、未改 `docs/CHANGELOG-*` / `docs/Maintenance-Order-*` / `docs/Fix-Report-*` /
  `docs/dispose-state-machine.*`、未动 `archives/**`、**未 commit / 未 tag**。

---

## 7. 未处理 / 留给别人的

| 项 | 说明 | 谁能补 |
|---|---|---|
| P2-1 性能优化（缓存架聚合 / 覆盖索引 `t_sample(t_status,t_location)`） | 工单要求"只写文档说明"，**未动代码** | 现场用 `EXPLAIN ANALYZE` 采集后决定 |
| P3-3 `sam_dispose_status.rack` 列内容格式变更 | 复核判定无消费者（只写不读），本次未改 | 将来若按 `rack` 聚合需兼容两种格式 |
| P3-4 `getInitialValue` 同类口径（`FileLoggingEnabled`） | 遗留问题，本次未动 | 单独工单 |
| P3-5 Playwright 默认被测目录是旧产物 | 属复核方资产建议，未动 | 复核方/实施方写 README |
| 存储过程模式（`CommandType=1`）成功路径 | 复核方未能在测试库建只读 SP，未做 | 客户/实施方在测试库建只读 SP 后补 |
