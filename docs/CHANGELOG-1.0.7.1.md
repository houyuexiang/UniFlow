# UniFlow v1.0.7.1 改动记录

- 日期：2026-11-09
- 版本：1.0.7.0 → **1.0.7.1**
- 部署包：`UniFlow-1.0.7.1-linux-x64.tar.gz` / `UniFlow-1.0.7.1-win-x64.zip`
- 概述：**按架排序 + 使用说明书**。本版给 SRM 样本丢弃链路加了**「按架排序」**（`DisposeByRack`，**默认开启**）—— 视图模式下候选**按"架"分组**，让发送窗口里的 N 条**尽量来自同一架**；同时新增一册通《UniFlow 使用说明书》，并**打进安装包**。

> 本版**新增 1 个配置字段** `Aptio:DisposeSample:DisposeByRack`（**热生效**），**默认 `true`** → **升级即生效**。
> 但它**只改「候选顺序」**，**不改窗口大小、不改发送逻辑、不改重排队逻辑** —— 见第一节与第六节。
> 需求与原始问题描述见 `docs/Maintenance-Order-2026-11-07.md`（维护单）；独立复核记录见 `docs_20261103/Review-2026-11-09-rack-dispose.md`，复核发现项的修复记录见 `docs_20261103/Fix-2026-11-09-rack-dispose-P2P3.md`。
> ⚠️ **本版尚未在真实 Aptio 2055 / 真实 SRM / 真实冰箱 / 真实客户 MySQL / Windows Service / Docker 容器上做过端到端验收** —— 第七节「已知问题 / 延后项」与其中的「现场验收清单」必须逐项确认后再投产。

---

## 一、按架排序 `DisposeByRack`（默认开启）

**位置**：`code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs:177-184`（架键表达式）、`:206-228`（SQL 构造）、`:250-266`（分支与回退）、`:280-287`（Warning 节流）；开关定义 `code/src/UniFlow/Common/Models/ConfigShared.cs:128`。

### 1.1 为什么改

1.0.7.0 的候选取法是 `SELECT * FROM <视图> LIMIT 10` —— **视图返回什么顺序就丢什么顺序**，同一批 10 条可能来自 10 个不同的架。现场按架逐个清理时，这会导致**同一架被反复打开/关闭**，且同一架的样本被拆散到多个批次。

### 1.2 架键规则（`DisposeDatabaseService.cs:177-184`）

| location 形态 | 架键 | 说明 |
|---|---|---|
| `&1-{节点}-{架号}-{层}-{槽}-{位}`（**6 段**） | `&1-{节点}-{架号}` | 例 `&1-13-000078-05-14-01` → `&1-13-000078` |
| `&2-{节点}-000000-00-{槽}-{位}`（**6 段**） | `&2-{节点}-{槽}` | 例 `&2-01-000000-00-18-01` → `&2-01-18`（真正的架/槽在**第 5 段**） |
| `&3-*`（已丢弃/归档哨兵） | **NULL → 排除** | 不参与按架排序，**不丢弃** |
| `&0-*` / `NULL` / 其它前缀 / **段数不足 6 段的短格式** | `~other` | **保留**（不静默丢弃），**排最后** —— 见第二节 |

> 段数判断：SQL 侧用 `LIKE '&1-%-%-%-%-%'`（5 个 `-` → 6 段），C# 侧 `GetLocationRackNo`（`:769`）用 `parts.Length < 6` 守卫 —— **两侧同口径**。

### 1.3 排序口径（`DisposeDatabaseService.cs:225`）

```sql
ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC,
         r.cnt DESC,        -- ① 架级「合格样本数」降序
         r.oldest ASC,      -- ② 架级「最早 update_time」升序
         v.location ASC     -- ③ 同架内按 location 升序（位置 01→48）
```

- ① 「合格数最多的架先丢」→ 优先把一个架一次性清空；
- ② 合格数相同时，**最早**的那架先丢；
- ③ **同架内按 `location` 升序**（不是按 `update_time`）—— `update_time` 只决定**架与架之间**的先后（复核 P3-1 已修正文档口径）；
- `~other` 组被第一个条件**一律压到最后**（否则 `~other` 组条数多时会被 `cnt DESC` 顶到最前）。

### 1.4 窗口不变，只改顺序

- 发送窗口**仍是** `MaxOnetimeSelectDiscardCount`（**默认 10**）—— **按架排序不改窗口**；
- **一个满架 48 条 → 分 5 批取完**（10 + 10 + 10 + 10 + 8），但**每批始终是同一架**；
  - 本机实测（真实库 `view_overtimestoragesample`，11844 行）：按架 SQL 前 10 行 `uni_rack_key` **全部为 `&1-13-000088`**（`cnt=48`，`oldest=20260901091114`），`location` 从 `-06-08-01` 递增到 `-06-08-10`；
  - 复核方实测 `LIMIT 48` → **48 行 / 1 架**（`LIMIT 49` → 2 架），与"满架 48 条"一致。

### 1.5 回退与开关

| 场景 | 行为 | 位置 |
|---|---|---|
| `DisposeByRack=false` | 走**原顺序** `SELECT * FROM <视图> LIMIT @Max`（与 1.0.7.0 **完全一致**） | `:208`、`:253` |
| 按架 SQL **执行失败**（视图缺 `update_time`、MySQL 版本/语法不支持等） | **自动回退原查询**，丢弃**不停摆**；Warning **10 分钟最多一条** | `:259-265`、`:280-287` |
| `CommandType=0`（**视图模式**） | **按架排序生效** | `:250-266` |
| `CommandType=1`（**存储过程模式**） | **不生效**（排序由数据库侧决定）→ 只打一条 Debug 日志，**不静默回退、不报错** | `:267-273` |

> 回退是"绝不让丢弃停摆"的**唯一保险** —— 本版已为它补上自动化闸门（第三节）。

### 1.6 性能（**需现场观察**）

- 按架 SQL **把视图扫了两遍**（外层出样本列 + 内层出 `update_time` 聚合）；
- **本机实测**（MySQL 8.0.45，视图 11844 行 / 底层 `t_sample` 17965 行，热缓冲池，`SHOW PROFILES`）：
  - 按架 SQL：**0.2095 s** / **0.2170 s**
  - 原查询 `SELECT * FROM <视图> LIMIT 10`：**0.0129 s** / **0.0132 s**
  - 复核方在**冷缓存**下测得 **0.29 ~ 0.30 s** vs **0.05 s**
- 丢弃循环默认 `LoopIntervalSeconds=3` → **0.2~0.3 s 相对 3 s 循环可接受**；
- **视图特别大时（百万行级）请现场 `EXPLAIN ANALYZE`**，必要时临时 `DisposeByRack=false` 回退（热生效，无需重启）；
- 优化方向（**本版未做**）：架聚合结果按 N 秒缓存 / 给 `t_sample(t_status,t_location)` 建覆盖索引。

---

## 二、`~other` 兜底（**不静默丢弃**）

**背景**：复核方在 P2-3 指出，首版按架 SQL 的 `WHERE uni_rack_key IS NOT NULL` + `JOIN` 会让 `&0-*`、`NULL`、非标格式的行**完全不返回** —— 这些样本**永远不会被丢弃，且不报错、不告警**，只在日志的 `GetDisposeSamples returned 0` 里体现。

**本版已修**：

| location | 处理 | 结果 |
|---|---|---|
| `&3-*` | 架键 = `NULL` → 排除 | **仍排除**（已丢弃哨兵，不应再丢） |
| `&0-*` / `NULL` / 其它前缀 / **短格式**（`&1-13-000078`、`&2-01-000000-00` 等，段数 < 6） | 架键 = `'~other'` | **保留**（进候选），并**排最后** |

- C# 侧 `GetLocationRackNo`（`:763-773`）对短格式返回 `""`（不产生错误架键）→ **两侧都是"不丢弃"，口径一致**；
- 落库 `sam_dispose_status.rack` 对短格式写**空串**（不是错误架键）；
- 复核 P2-3 的放行条件「发布说明写明仅 `&1-*` / `&2-*` 架键参与按架排序」→ 见第六节「升级注意」。

---

## 三、回退路径的自动化闸门（复核 P2-2）

**背景**：复核方在 P2-2 指出，「按架 SQL 失败 → 自动回退原查询」这条**唯一的保险**，当时**零自动化覆盖**（把 `catch (Exception ex) when (byRack)` 改成 `when (false)`，31 例**全绿** → 闸门不红）。

**本版已修**：新增 **1 条 MySQL 集成测试**

- 用例：`code/tests/UniFlow.Tests/DisposeSample/Services/DisposeDatabaseServiceRackTests.cs:384`
  `GetDisposeSamplesAsync_OnRealMySql_RackQueryFails_FallsBackToPlainQuery`
- 做法：
  1. 在测试库建一个**没有 `update_time` 列**的视图 `view_rack_fallback_test` → 按架 SQL 内层 `MIN(update_time)` 必然报
     `Unknown column 'update_time' in 'field list'`；
  2. 用 `DisposeByRack=true`（`AptioConfig` 默认）的 `DisposeDatabaseService` 调
     `GetDisposeSamplesAsync(0, "view_rack_fallback_test", 10)`；
  3. 断言**不抛异常**且 `samples.Count == 10`（回退到 plain 查询成功，仍取满窗口）；
  4. `finally` 里 `DROP VIEW IF EXISTS view_rack_fallback_test`（只在测试库建/删临时视图，不建表、不改表数据）。
- **负向证据（闸门能红）**：在 `/tmp` 副本里把 `catch (Exception ex) when (byRack)` 改成 `when (false)`（拆掉回退闸门）→
  `Failed: 1, Passed: 32` —— 红的正是这条用例（失败信息 `MySqlException : Unknown column 'update_time' in 'field list'`）。

---

## 四、使用说明书（新增文档，并打进安装包）

### 4.1 新增《UniFlow 使用说明书》

- 新文件：`docs/UniFlow-使用说明书.md`（31,620 字节，**一册通**）
- 章节：0 五分钟快速开始 / 1 系统概述 / 2 安装与升级 / 3 配置说明 / 4 Web 管理界面 /
  **5 样本丢弃流程（重点）** / 6 其它模块 / 7 日志、健康与故障排查 / 8 安全 / 9 备份与恢复 / 10 附录
- 其中 §5.7 单列「按架排序（`DisposeByRack`，默认开启）」，含架键表、排序口径、回退开关与性能提示。

### 4.2 README 加文档入口

- `README.md:30` 新增首行：`- [**UniFlow 使用说明书**](docs/UniFlow-使用说明书.md)（一册通：安装 / 配置 / WebAdmin / 样本丢弃 / 故障排查）`。

### 4.3 安装包内新增 `使用说明书.md`

- `deploy/package.sh`：**Linux 段与 Windows 段各加一行**，把 `docs/UniFlow-使用说明书.md` 复制为包内 `uniflow/使用说明书.md`；
- `deploy/INSTALL.md`：**两个平台的包内容表各加一行** `使用说明书.md`；并把文档里硬编码的 `1.0.6.5` 版本/包名更新为 `1.0.7.1`（只改当前示例，`deploy/INSTALL.md` 第六节「版本记录」的历史叙述保留）；
- `docs/Release-Process.md`：包自检从「两包各 3 份 `.md`」改为「**两包各 4 份 `.md`（README / INSTALL / CONFIG-REFERENCE / 使用说明书）**」，并加 `cmp` 检查使用说明书两平台**逐字节一致**（`MANUAL_OK`）；发布清单同步更新。

---

## 五、测试

### 5.1 构建

```
$ export HOME=/root; /usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release
    14 Warning(s)
    0 Error(s)
```

14 条 warning **全部为历史存量**（`WebAdmin/Services/ConfigService.cs`、`WorkListCleaner/Services/ImmuliteCleaner.cs`、
NU1902 / NU1903 依赖告警）；**本次改动涉及的源文件 0 warning**。

### 5.2 单元 / 集成测试

| 环境 | 命令 | 结果 |
|---|---|---|
| **有 MySQL** | `UNIFLOW_TEST_MYSQL_HOST=127.0.0.1 UNIFLOW_TEST_MYSQL_DB=uniflow_loc_analysis MYSQL_PWD=… dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 506, Skipped: 0, Total: 506, Duration: 49 s` |
| **无 MySQL** | 同上但**不设** MySQL 环境变量 | `Passed! - Failed: 0, Passed: 502, Skipped: 4, Total: 506, Duration: 48 s` |

- 无 MySQL 时跳过的是 **4 条** MySQL 集成用例（`DisposeDatabaseServiceRackTests` 下 4 条 `OnRealMySql`），**不是**失败；
- 4 条 MySQL 集成在有 MySQL 时**真的执行**（非 skip），含本版新增的回退用例。

### 5.3 前端与端到端

| 项 | 命令 | 结果 |
|---|---|---|
| JS 语法 | `node --check code/src/UniFlow/wwwroot/app.js` | 无输出，`rc=0`（node v24.16.0） |
| Playwright（**本版新发布产物**） | `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-1.0.7.1 npx playwright test` | **56 passed**（41.1 s；复跑 42.9 s） |
| Playwright（**冻结副本**） | `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-current npx playwright test` | **56 passed**（41.2 s） |

> 说明：`run-test/playwright/playwright.config.js` 的**默认** `APP_DIR=/tmp/uniflow-e2e/app` 是 **11-07 的旧产物**（`DisposeByRack` 命中 0），用它跑出来的 56/56 **不覆盖本版改动**（复核 P3-5）。上表两次运行均**显式指定** `UNIFLOW_APP_DIR`；第一次的 `app-1.0.7.1` 是本次按 1.0.7.1 重新 publish 的产物。

### 5.4 版本冒烟（**跑的是安装包里的二进制**）

```
$ tar -xzf release/UniFlow-1.0.7.1-linux-x64.tar.gz -C smoke && cd smoke/uniflow && ./UniFlow &
$ curl -s http://127.0.0.1:5100/api/version
{"version":"1.0.7.1","informational":"1.0.7.1+3d951f49d9d56bbbabaf06b5f8e157c59a5662db"}
```

- `version` = **`1.0.7.1`** ✔；`informational` 带 HEAD 提交号 `3d951f4`（本版代码尚未提交，属预期）。

---

## 六、升级注意

### 6.1 默认行为变化（升级即生效）

- `DisposeByRack` **默认 `true`** → **升级后立即按架排序**，**不需要改任何配置**；
- **但它只改「候选顺序」**：窗口大小（`MaxOnetimeSelectDiscardCount`）、发送逻辑、确认逻辑、重排队逻辑**全部不变**；
- 需要恢复 1.0.7.0 行为 → 改 `appsettings.json` 的 `Aptio:DisposeSample:DisposeByRack` 为 `false`（**热生效，无需重启**），或页面「功能配置」取消勾选「按架排序」。

### 6.2 现场必查项

| 项 | 要求 |
|---|---|
| **`SrmNodeIds` 必须含数据里实际出现的节点号** | 例如 `["13"]`；数据里有 `&2-*` 存储时还要含 `01`。**配错是"样本不丢"的首要原因**（`SkipOnUnknownNode=true` 时不在清单里的样本会被标记 `send=2` 跳过）—— 与本版无关，但按架排序会改变**取样顺序**，更早暴露这个问题 |
| **视图必须有 `barcode` / `location` / `update_time`** | 缺 `barcode` / `location` → 按架 SQL 直接失败 → **自动回退原查询**（丢弃不停摆，但按架排序不生效）；缺 `update_time` → 同理**只影响按架排序**，已由集成测试固化（第三节） |
| **`&3` 排除** | `&3-*` 是已丢弃/归档哨兵，**不参与按架排序、不再丢弃** |
| **`&0` / `NULL` / 短格式** | 归 `~other` 组，**保留**（会进候选）并**排最后** |
| **存储过程模式** | `CommandType=1` 时 **`DisposeByRack` 不生效**（排序由数据库侧决定）—— 不要误以为配错了 |

### 6.3 其它

- 配置键名 / 层级 / 其余默认值**全部不变**；仓库默认 `appsettings.json` **不含**该键 → **现场 `appsettings.json` 不需要改**；
- 1.0.7.0 的快速填窗（`EnableFastFill`，默认 `false`）、重排队死锁修复、管理面鉴权 / 配置打码 / XSS 闸门**全部原样保留**，本版未削弱；
- 本版**未改动** DMS 下单链路、SrmExport、递送 / 优先级 / 批量扫码链路的任何业务判定。

---

## 七、已知问题 / 延后项（**不阻塞本版**）

| 项 | 状态 | 说明 |
|---|---|---|
| **真实环境 E2E 未做** | ⚠️ **必须现场验证** | **未在真实 Aptio 2055 / 真实 SRM / 真实冰箱 / 真实客户 MySQL / Windows Service / Docker 容器上验证过本版任何行为**。按架排序的现场效果（同一架是否真的连续成批）、`~other` 组在客户视图里的真实占比、`&3` 是否已完全排除，**均需现场确认** |
| **MyISAM / InnoDB 未区分验证** | ⚠️ 需现场确认 | 本机测试库 `uniflow_loc_analysis` 的 `t_sample` 是 **MyISAM**（无 MVCC、表级锁），两遍扫描期间若并发写入，架计数与样本列**可能不一致**；InnoDB 单语句内为一致性快照，风险更低但**本版未实测**。MyISAM 的**读锁还可能阻塞对 `t_sample` 的写入** —— 客户库若为 InnoDB 需另行确认 |
| **M-09** `send` 已投递但 DB 写失败时重复下发 | 延后 | 命令已写入 socket，但 DB `send=1` 失败 → 保持 Ready，下一轮可能重发同一条 `COMMENT`。协议幂等（TRASH）通常无害；极端 DB 故障时重复指令 / 日志。**本版未改**。 |
| **M-10** 依赖漏洞升级 | 延后 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（NU1903，high）、`MySqlConnector` 2.4.0（NU1902，moderate）。当前使用面为本地 admin SQLite / 内网 MySQL，无直接可利用入口；升级需单独维护窗口 + 全量回归，**不与业务改动混提**。**本版未改**。 |
| **HTTP 60 秒重放** | 接受（既有） | Web 管理走 **HTTP 明文**；管理口令的 `X-Admin-Hash` nonce **60 秒有效、一次性** —— nonce 只能防"同一次动作重复提交"，**不能防"抓到包后在 60 秒窗口内立刻重放"**（MITM）。**本版未改**，现场应限制管理端口的网络可达范围。 |
| **按架 SQL 扫视图两次的性能需现场观察** | 需现场采集 | 本机实测 0.21~0.22 s（11844 行视图），复核方冷缓存 0.29~0.30 s；3 s 循环可接受。**百万行级现场请 `EXPLAIN ANALYZE`**；必要时 `DisposeByRack=false` 回退。缓存架聚合 / 覆盖索引**本版未做**。 |
| **架聚合未缓存** | 延后 | 每轮丢弃循环都重算一次架聚合（扫视图两遍）。优化方案：按 N 秒缓存 / `t_sample(t_status,t_location)` 覆盖索引。**本版未做**。 |
| **`sam_dispose_status.rack` 列格式变更** | 接受 | `GetLocationRackNo` 从"只取第 3 段"改为"完整架键"（`&1-13-000078`），`&2` 从 `000000` 改为 `&2-01-18`。全仓 grep 确认该列**只写不读** → 暂无影响；将来若按 `rack` 聚合需兼容两种格式。 |

### 现场验收清单（本版**未做**真实环境 E2E，必须现场确认）

1. **按架排序**：测试环境跑一轮丢弃，确认日志/队列里**连续 10 条属于同一架**（`location` 前缀一致）；
2. **回退**：把 `DisposeByRack` 改成 `false`，确认候选顺序**立即回到原顺序**（热生效，无需重启）；
3. **`~other` 兜底**：在客户视图上跑 `SELECT DISTINCT LEFT(location,2) FROM <客户视图>`，确认是否存在 `&0` / 非标 location 行，以及它们是否被排到最后；
4. **真实 MySQL 性能**：对客户视图跑 `EXPLAIN ANALYZE`，确认单轮耗时可接受（必要时 `DisposeByRack=false`）；
5. **真实 Aptio 2055 / SRM**：确认同一架连续丢弃被冰箱侧接受；
6. **Windows / Docker**：Windows 服务升级路径、Docker `Features__*` 开关与卷 / 日志路径。

### 升级步骤建议

1. 备份现场 `appsettings.json`；
2. 停服务 → 覆盖程序文件 → **保留现场 `appsettings.json`**（安装包内的配置是模板，不要覆盖现场配置）；
3. 启动后查看日志，确认无 `Worker error`；
4. **先观察一轮**按架排序的实际效果（不需要改配置，默认已开启）；
5. 若按架排序不符合现场预期 → `DisposeByRack=false`（热生效，立即回退）；
6. 确认无误后再投产。

---

## 附一：产物校验（本机实测）

| 产物 | 大小（字节） | md5 | sha256 |
|---|---|---|---|
| `release/UniFlow-1.0.7.1-linux-x64.tar.gz` | 46,331,992 | `6704be8a97e3a853b7c25ca9a820f939` | `d35bbf4c9f1b52f9068ff910ac76de5257825ef4fe052c0fe58f3ac8ff512acb` |
| `release/UniFlow-1.0.7.1-win-x64.zip` | 47,157,407 | `6fe284a003443cdecbba16856c222140` | `4795ff714a66312f632b55833d7c3342c934afa601d05f980b29a892bdc4484f` |

包内自检（全部通过）：

| 检查项 | 结果 |
|---|---|
| Linux 包 `.md` 清单 | `README.md` / `CONFIG-REFERENCE.md` / `INSTALL.md` / `使用说明书.md` → **4 份** ✔ |
| Windows 包 `.md` 清单 | 同上 → **4 份** ✔ |
| `cmp` INSTALL.md（两平台） | `INSTALL_OK` ✔ |
| `cmp` CONFIG-REFERENCE.md（两平台） | `CONFREF_OK` ✔ |
| `cmp` 使用说明书.md（两平台） | `MANUAL_OK` ✔ |
| 使用说明书 == 源文件 `docs/UniFlow-使用说明书.md` | `MANUAL_SRC_OK` ✔ |
| Linux 包内 `UniFlow` / `appsettings.json` / `wwwroot/` / `使用说明书.md` | 全部存在 ✔ |
| Windows 包内 `UniFlow.exe` / `appsettings.json` / `wwwroot/` / `使用说明书.md` | 全部存在 ✔ |

## 附二：改动文件索引（发布相关）

| 文件 | 位置 | 改动 |
|---|---|---|
| `code/src/UniFlow/UniFlow.csproj` | `:5-7` | `Version` / `AssemblyVersion` / `FileVersion` → `1.0.7.1` |
| `deploy/build.sh` | `:7` | `VERSION` → `1.0.7.1` |
| `deploy/package.sh` | `:6` | 用法注释示例 → `1.0.7.1` |
| `deploy/package.sh` | `:57`（Linux 段） / `:72`（Windows 段） | **各加一行**：复制 `docs/UniFlow-使用说明书.md` → 包内 `使用说明书.md` |
| `deploy/INSTALL.md` | `:1,9,22,48` + `:19,33`（两个包内容表） | 版本/包名 `1.0.6.5` → `1.0.7.1`；包内容表加 `使用说明书.md` |
| `docs/Release-Process.md` | `:39,69,88` | 可复制即用的示例版本号 → `1.0.7.1`（历史叙述未改） |
| `docs/Release-Process.md` | `:70,79,82` + 发布清单 `:125` | 包自检 → **4 份 `.md`** + `cmp` 使用说明书（`MANUAL_OK`） |
| `docs/CHANGELOG-1.0.7.1.md` | 本文件 | 本版改动记录 |
| `docs/UniFlow-使用说明书.md` | 新增（工作区已有）+ **本版发布同步 5 处**：§2.2 两个平台的包内容树加 `使用说明书.md`；§2.3 解压示例包名 `1.0.7.0` → `1.0.7.1`；§10.2 文档索引加 `docs/CHANGELOG-1.0.7.1.md`；§10.4 版本摘要去掉「（待发布）」 |
| `README.md` | `:30` | 文档入口（未改动，工作区已有） |
| `docs/Maintenance-Order-2026-11-07.md` | — | 维护单（本版实现依据；**未改**） |
| `docs_20261103/Review-2026-11-09-rack-dispose.md` | — | L3 独立复核报告（PASS-with-findings；**未改**） |
| `docs_20261103/Fix-2026-11-09-rack-dispose-P2P3.md` | — | 复核 P2-2 / P2-3 / P3-1 / P3-2 修复记录（**未改**） |

> 本版**未改动**任何业务判定逻辑（除已在本工作区完成的按架排序实现外）、**未改动** `archives/**`、旧包
> `release/UniFlow-1.0.6.*` / `1.0.7.0-*`、`docs/Maintenance-Order-*`、`docs/Fix-Report-*`、
> `docs/dispose-state-machine.*`；**未 commit / 未 tag / 未 push**。
