# UniFlow 配置参考 (appsettings.json 逐字段说明)

配置文件位于安装目录下：Linux `/usr/share/uniflow/appsettings.json`，Windows `C:\UniFlow\appsettings.json`。

> 热加载：标注 **热加载** 的字段修改后 3-5 秒自动生效，无需重启。
> 其余字段修改后需重启服务（Linux: `systemctl restart uniflow`，Windows: `Restart-Service UniFlow`）。

---

## Logging

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `LogLevel.Default` | string | `Information` | 全局默认日志级别：Trace/Debug/Information/Warning/Error/Critical |
| `LogLevel.Microsoft.Hosting.Lifetime` | string | `Warning` | 框架生命周期日志级别 |
| `LogLevel.UniFlow` | string | `Information` | 应用日志级别 |
| `Console.FormatterName` | string | `simple` | 控制台输出格式 |
| `Console.FormatterOptions.TimestampFormat` | string | `HH:mm:ss ` | 控制台时间戳格式 |
| `UniFlowFile.FileLoggingEnabled` | bool | `true` | 是否启用文件日志 |
| `UniFlowFile.LogDirectory` | string | `logs` | 日志目录（相对安装目录） |
| `UniFlowFile.MaxFileSizeMb` | int | `10` | 单文件大小上限(MB)，超限自动滚动 `_0`→`_1` |
| `UniFlowFile.RetentionDays` | int | `30` | 日志保留天数，超期自动清理 |
| `UniFlowFile.LogLevel` | string | `Information` | 文件日志级别 |

> 日志按天目录存放：`logs/<yyyy-MM-dd>/<模块>_<序号>.log`，如 `logs/2026-09-04/DisposeSample_0.log`。

---

## Features（功能开关，**热加载**）

按功能归属分组，每个功能独立开关：

### Aptio 组

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Aptio.DisposeSample` | bool | `true` | 样本丢弃 |
| `Aptio.SrmExport` | bool | `true` | SRM 数据导出 |
| `Aptio.Delivery` | bool | `false` | 按测试名触发 delivery |
| `Aptio.DeliveryFile` | bool | `false` | delivery 清单文件递送（独立于测试名 delivery） |
| `Aptio.Priority` | bool | `false` | 优先级控制 |
| `Aptio.TestNameDispose` | bool | `false` | 按测试名丢弃 |

### Immulite 组

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Immulite.WorkListCleaner` | bool | `false` | WorkListCleaner 工作列表清理 |

### Dms 组

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Dms.PitStopMonitor` | bool | `false` | PitStop 监控 |
| `Dms.StatusCorrection` | bool | `false` | 状态修正 |
| `Dms.SampleCleanup` | bool | `false` | 样本清理 |
| `Dms.EmptyResultCleanup` | bool | `false` | 空结果清理 |

> Web 仪表盘页提供滑块开关，与这些字段一一对应（路径格式 `Features:Aptio.DisposeSample` 等）。

---

## Aptio（仪器连接，需重启）

### 公共配置

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Ip` | string | `127.0.0.1` | Aptio 服务器地址 |
| `Port` | int | `2055` | Aptio 通信端口 |
| `SrmNodeIds` | string[] | `["09","16"]` | SRM 节点编号列表。**以 appsettings.json 为准，不与代码默认值合并**（配置写 `["16"]` 即只有 `["16"]`） |
| `Database.Host` | string | `127.0.0.1` | MySQL 地址 |
| `Database.Port` | int | `3306` | MySQL 端口（支持自定义） |
| `Database.User` | string | `root` | MySQL 用户名 |
| `Database.Password` | string | `root` | MySQL 密码 |
| `Database.Database` | string | `flexlab` | MySQL 数据库名 |

### Aptio.DisposeSample（样本丢弃，对应 `Features.Aptio.DisposeSample`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DisposeSample.CommandType` | int | `0` | 命令类型（0=视图，其他=存储过程） |
| `DisposeSample.CommandName` | string | `view_overtimestoragesample` | 查询命令名/视图名 |
| `DisposeSample.DiscardRunDate` | string | `1,2,3,4,5,6,7` | 运行日期（1-7 周一~周日） |
| `DisposeSample.DiscardTimeRange` | string | `01:30-05:45,3800;18:00-07:00,7500;` | 丢弃时间段与阈值 |
| `DisposeSample.LoopIntervalSeconds` | int | `3` | 循环间隔(秒) |
| `DisposeSample.MaxWaitDiscardCount` | int | `2` | 最大等待确认次数 |
| `DisposeSample.MaxOnetimeSelectDiscardCount` | int | `10` | 单次最大选择丢弃数；**同时是发送窗口**（未丢弃总数上限） |
| `DisposeSample.DisposeByRack` | bool | `true` | **按架排序丢弃（默认开启）**。`true` = 视图模式下候选按"架"排序：**合格样本最多、最老的架优先**，窗口 N 条尽量来自同一架；架键规则：`&1-{节点}-{架号}`、`&2-{节点}-{槽}`（均要求 6 段），`&3`（已丢弃哨兵）**排除**，`&0`/NULL/短格式归 `~other` 组**保留并排最后**（不静默丢弃）。`false` = 恢复原顺序 `SELECT * FROM <视图> LIMIT N`（可随时回退）。**只改候选顺序**，不改窗口/发送/重排队逻辑（窗口仍是 `MaxOnetimeSelectDiscardCount`，默认 10）；按架 SQL 执行失败时**自动回退原查询**（Warning 节流），丢弃不停摆。仅对 `CommandType=0`（视图）生效，存储过程模式排序由数据库侧决定。热生效 |
| `DisposeSample.EnableFastFill` | bool | `false` | **快速填窗开关，默认关闭**。关闭（默认）= 行为与 1.0.6.7 完全一致（一条一条发、逐条确认）。开启 = 一个 tick 内连续发 `COMMENT` 直到发送窗口满（窗口上限仍是 `MaxOnetimeSelectDiscardCount`，默认 10），不再每条等确认；确认改为**批量**（`FastFillConfirmIntervalSeconds`）。热生效 |
| `DisposeSample.FastFillIntervalMs` | int | `1000` | 快速填窗时每条 `COMMENT` 之间的间隔（毫秒，范围 `0~5000`，**代码硬钳制**）；`0` = 不等待（连发）。**注意：`0` 只是不等待，不是关闭快速填窗**——开关只有 `EnableFastFill`。仅在 `EnableFastFill=true` 时生效。热生效 |
| `DisposeSample.FastFillConfirmIntervalSeconds` | int | `5` | 快速填窗的**批量确认**间隔（秒，范围 `1~60`，**代码硬钳制**）：每该秒用**一条** SQL 确认所有 `send=1 & disposed=0` 的在途记录，命中即置 `disposed=1` 并释放发送窗口。仅在 `EnableFastFill=true` 时生效。热生效 |
| `DisposeSample.AllowSrmErrorCode` | string | `0000,0F0A,0E59,AAAA` | 允许的 SRM 错误码 |
| `DisposeSample.SkipOnUnknownNode` | bool | `true` | 未知节点是否跳过 |
| `DisposeSample.AllowStandbyDispose` | bool | `false` | **测试开关**：开启后 `STB`（Stand-by）与 `P`（Pink）也允许丢弃；`GTO` / `PA` / `OFF` / `FR` / `SH` / `EXE` **始终不允许**。关闭（默认）= 可丢弃 = `Mode=="ON"` 且 `Status∈{G,Y}` 且错误码在允许列表。STB 不允许进新样本，但允许执行丢弃；本开关只管「是否允许 STB/P 参与丢弃判断」，不涉及进样本逻辑。热生效 |
| `DisposeSample.ResendIntervalMinutes` | int | `5` | **重排队检查间隔（分钟）**：每该分钟检查一次 `send=1` 且未确认丢弃的记录；命中条件（节点 `ON` + 状态 `G/Y` + 错误码在允许列表）时把记录**放回待发队列**（`send=0`），由状态机按发送窗口一条条发送（**不直接下发丢弃命令**）；`STB` 等非 `ON` 状态一律等待；`0` = 关闭重排队 |
| `DisposeSample.MaxResendCount` | int | `24` | 单条记录最多重发次数（**真正发送成功时**才计数）；用尽后标记 `send=3`（失败，不再自动重发，交由页面人工处置）；`0` = 不限 |
| `DisposeSample.MaxPendingRecords` | int | `0` | 待处理队列硬上限（`send=0` 或 `send=1 且 disposed=0`）；达到上限不再插入新丢弃记录；`0` = 不限 |
| `DisposeSample.PendingWarnThreshold` | int | `1000` | 待处理队列超过该数量打 Warning（每 10 分钟最多一次）；`0` = 不告警 |

### Aptio.SrmExport（SRM 导出，对应 `Features.Aptio.SrmExport`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `SrmExport.ExportTime` | string | `08:30` | 每日导出时间(HH:mm) |
| `SrmExport.LoopIntervalSeconds` | int | `60` | 导出检查循环间隔(秒) |
| `SrmExport.LogRetentionDays` | int | `30` | 导出文件保留天数 |
| `SrmExport.OutputPath` | string | `DisposeFile` | 导出目录（相对安装目录或绝对路径） |

> 支持绝对路径（如 `/data/exports` 或 `D:\exports`），相对路径按安装目录拼接。

### Aptio.Delivery（按测试名 delivery，对应 `Features.Aptio.Delivery`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Delivery.TestName` | string | `` | delivery 测试名（完全匹配 `test` 字段第 5 段） |

### Aptio.DeliveryFile（清单文件递送，对应 `Features.Aptio.DeliveryFile`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DeliveryFile.DeliveryListFilePath` | string | `` | delivery 清单文件目录路径 |
| `DeliveryFile.LoopIntervalSeconds` | int | `60` | 文件检查循环间隔(秒) |

> 与 `Delivery`（测试名触发）是两个独立功能，各自独立开关。

### Aptio.Priority（优先级，对应 `Features.Aptio.Priority`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Priority.TestName` | string | `` | 优先级测试名（完全匹配 `test` 字段第 5 段） |

### Aptio.TestNameDispose（按测试名丢弃，对应 `Features.Aptio.TestNameDispose`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `TestNameDispose.DisposeTestName` | string | `` | 按测试名丢弃的测试名（完全匹配 `test` 字段第 5 段） |

### Aptio.BatchScan（测试触发共享扫描，无独立开关）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `BatchScan.LoopIntervalSeconds` | int | `60` | 数据库扫描间隔(秒) |
| `BatchScan.MaxOnetimeScanCount` | int | `500` | 单次扫描最大样本数 |

> **无独立开关**：任一测试触发功能（Delivery / Priority / TestNameDispose）开启即自动启用，防止误关导致测试触发功能失效。

> **工作方式（两阶段）**：
> 1. **SQL 初筛**：扫描器一次查询 `t_sample`，用各注册功能测试名的并集做 `OR LIKE` 粗筛（参数化），只拉取可能匹配的行，减少传输量；
> 2. **内存分发**：逐行解析 `test` 字段第 5 段做**完全匹配**，命中注册者且对应功能开关开启时，推送到该 Worker 的队列；未开启的功能不推送。

> **扩展新测试触发任务**：只需新建 Worker（构造时 `router.Register(name, testName, 匹配委托, 自持Channel, 开关委托)`）并在 Program.cs 注册一行 HostedService，扫描器与 Router 无需修改。

---

## DisposeSample 队列与确认口径

本节说明 `Features.Aptio.DisposeSample`（样本丢弃）从「统计 → 选候选 → 入队 → 投递 → 确认丢弃」的**实际口径**，
供现场核对与排障（对应实现：`DisposeSampleWorker` / `DisposeDatabaseService` / `AptioCommandService`）。

### 1. 统计（超期样本数）

```sql
SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE '&1-{node}-%'
```

- **无时间过滤**：统计的是「已完成且仍停在 `&1-{node}-`（SRM 存储位）的样本数」，与样本入库时间/超期时长无关。
- **按节点分别统计再求和**：`Aptio.SrmNodeIds` 中每个节点各查一次，`totalCount` = 各节点之和。
- **阈值来自当前命中的 RunDays / TimeRanges**：
  - 先按 `RunDays`（1=周一 … 7=周日）判定当天是否运行；不在运行日 → 本轮不丢弃。
  - 再按 `TimeRanges` 依次匹配当前时刻，**多段取第一个命中**的 `Threshold`；段内为 `Start <= End`（普通段），
    `Start > End` 时按**跨天段**处理（`now >= Start || now <= End`）。
  - `totalCount <= threshold` → 本轮不丢弃；`totalCount > threshold` → 本轮需丢弃 `totalCount - threshold` 条。

### 2. 候选（超期样本来源）

| 项 | 说明 |
|----|------|
| 取数方式 | `Aptio.DisposeSample.CommandType` = `0` → 视图（`SELECT * FROM <CommandName>`）；非 0 → 存储过程（`CommandType` 作为命令类型调用） |
| 默认命令名 | `view_overtimestoragesample` |
| 「超期」年龄条件 | **由数据库侧的视图 / 存储过程定义，UniFlow 不定义也不校验**。现场须用 `SHOW CREATE VIEW view_overtimestoragesample;`（或对应的 `SHOW CREATE PROCEDURE`）核对「超期」判定条件与字段口径 |
| 候选顺序（按架排序） | `Aptio.DisposeSample.DisposeByRack`（默认 `true`）→ 视图模式下按"架"排序，详见 §2.1 |

### 2.1 按架排序（`DisposeByRack`，默认开启）

**只改「候选顺序」，不改窗口 / 发送 / 重排队逻辑** —— 发送窗口仍是
`MaxOnetimeSelectDiscardCount`（默认 **10** 条），本开关不改变窗口大小。

**架键规则**（由 `t_sample.t_location` 推出；C# 与 SQL 两侧同一口径，**都要求 6 段**）：

| `t_location` 形态 | 架键 | 示例 |
|------|------|------|
| `&1-{节点}-{架号}-{层}-{槽}-{位}` | `&1-{节点}-{架号}` | `&1-13-000078-05-14-01` → `&1-13-000078` |
| `&2-{节点}-000000-00-{槽}-{位}` | `&2-{节点}-{槽}`（架/槽在**第 5 段**） | `&2-01-000000-00-18-01` → `&2-01-18` |
| `&3-{节点}-000000-00-00-00`（已丢弃/归档哨兵） | — | **排除**（不返回） |
| `&0-…`（特殊/未定位）、`NULL`、其它前缀、**段数不足 6 段的短格式**（如 `&1-13-000078`、`&2-01-000000-00`） | `~other`（"其它"组） | **保留**、排在最后 —— **不静默丢弃** |

> 📌 短格式（段数不足 6 段）在 SQL 侧归 `~other` 组（保留、排最后），在 C# 侧 `GetLocationRackNo` 返回 `""`：
> 两侧都**不会产生错误的架键**，也都**不丢弃**该样本 —— 口径一致、可接受。

**排序规则**（架组优先、其它组最后）：

1. **其它组（`~other`）一律排最后**（`&0` / `NULL` / 短格式等不参与"架优先"，但**不会被丢弃**）；
2. **合格样本数降序**（同一架里可丢弃的样本多的架优先）—— 一次把一架子清空，避免"每架丢一条"；
3. **该架最早 `update_time` 升序**（最老的架优先）—— **只决定架与架之间的先后**；
4. `location` 升序（**同架内**的顺序：位置 01→48；稳定可复现）。

> ⚠️ **口径提醒**：`update_time` **不参与同架内的排序**。同一架内是 **location 升序**（位置 01→48），
> 不是"最早 `update_time` 优先"；架与架之间才是按"该架最早 `update_time`"决定先后。

结果：窗口内的 N 条（默认 10）**尽量来自同一架**。
这是**最优努力**：若某架只有部分样本合格（其余仍在 `&1`/`&2` 但不合格），则该架**先丢合格的那部分**，
下一轮再按剩余情况重新排序 —— 不保证"整架一次性清空"。

**失败回退**：按架 SQL 执行失败（视图列名不匹配、MySQL 版本不支持等）时，
**自动退回原查询** `SELECT * FROM <视图> LIMIT @Max`，并打一条 Warning（10 分钟节流，
内容：`rack-aware query failed, fallback to plain view query; disposal continues`）
—— **绝不让丢弃功能停摆**。

**性能提示**：按架 SQL 需要**扫描候选视图两次**（一次取样本列、一次按架聚合 `update_time`）——
实测 14k 行视图约 **0.2~0.3 s**（原单次查询约 **0.05 s**）。对默认 **3 s** 的循环间隔可接受；
但现场视图很大（百万行级）时，**建议先用 `EXPLAIN ANALYZE` 实测**，
必要时临时把 `Aptio:DisposeSample:DisposeByRack` 改成 `false` 回退到单次查询（热生效，无需重启）。

**适用范围**：仅对 `CommandType=0`（视图）生效。`CommandType=1`（存储过程）时排序由数据库侧决定，
本开关**不生效**（只记 Debug 日志，不报错）。

### 3. 队列

| 项 | 口径 |
|----|------|
| 单轮选择/插入上限 | `Aptio.DisposeSample.MaxOnetimeSelectDiscardCount`（默认 `10`）：一次最多从候选里取多少条；**同时是发送窗口**，见下方「发送窗口」 |
| 队列存储 | `sam_dispose_status`（`send=0` 待投递）；同一条码不重复插入 |
| 队首推进 | 每轮取 `send=0` 中 `ORDER BY ID` 的第一条；投递成功 → `send=1`；未知节点且 `SkipOnUnknownNode=true` → `send=2`（skip sentinel），**不再阻塞队首** |
| 单条确认等待 | `Aptio.DisposeSample.MaxWaitDiscardCount`（默认 `2`）个循环：投递后进入 Ack 态，每轮查一次 `t_sample` 位置是否已变为 `&3-{node}-%`（已丢弃）；未确认则 `checkcount+1`，达到上限即视为本条结束（不等了），继续下一条 |
| 队列硬上限 | 发送窗口 `MaxOnetimeSelectDiscardCount`（默认 `10`）限制「未丢弃总数」；另有独立闸门 `MaxPendingRecords`（默认 `0` = 不限）限制待处理队列。实际规模受「SRM 内未丢弃的唯一条码数」约束 |
| 历史清理 | `send>=1`（含 `send=1` 已投递与 `send=2` skip sentinel）且 `send_time` 距今 > 10 天的记录自动删除（每轮进入 None 态时执行） |

### 3.1 发送窗口（`MaxOnetimeSelectDiscardCount` = 10）

`Aptio.DisposeSample.MaxOnetimeSelectDiscardCount`（默认 **10**）**同时是「发送窗口」**：
冰箱（SRM）侧未丢弃的样本总数上限 —— `active = send=0（待发） + send=1 且 disposed=0（已发未确认）`。

| 场景 | 行为 |
|------|------|
| 有待发记录（`send=0`） | **先发**：`HandleNone` 直接转 `Ready`（**不受窗口阻断**）。发送闸门在 `HandleReady`：按**在途数** `inFlight = send=1 且 disposed=0` 判断，满则本轮不发任何命令 |
| `inFlight >= 窗口`（发送闸门） | **只等待**：不下发任何新 COMMENT（连 STATUS 探测都不发）；状态保持 `Ready`，下一轮再检查。**例外**：5 分钟一轮的**重排队检查**在「有到期候选」时仍可发 1 条 `STATUS-REQUEST`（见 §6） |
| `active >= 窗口` 且无待发（插入闸门） | **不插入**新丢弃记录（`HandleNone` 保持 `None`）；本轮插入条数 = `min(need, 窗口 - active, 候选条数)` |
| 窗口 `<= 0` | 视为**不限**（保持旧行为：不做窗口判断、插入条数 = `need`） |

> 📌 **按架排序（`DisposeByRack`，默认开启）不改窗口**：窗口上限仍是 `MaxOnetimeSelectDiscardCount`（默认 **10**）。
> 本开关只决定"候选的**顺序**"（优先取同一架的样本），见 §2.1。

> 💡 **开启 `EnableFastFill` 后**（默认关闭）：窗口内**连续发送**（一个 tick 内连发 `COMMENT` 直到窗口满，
> 每条间隔 `FastFillIntervalMs`，默认 1000ms），确认改为**批量**（`FastFillConfirmIntervalSeconds`，默认 5 秒一条 SQL）；
> 窗口上限仍是 `MaxOnetimeSelectDiscardCount`（默认 10），`窗口 <= 0`（不限）时**不走**快速填窗，仍按一条一条发
> （此时确认也仍走原来的**逐条确认**，`MaxWaitDiscardCount` 继续生效）。

> 🔒 **范围由代码硬钳制**（页面 `max` 只是输入提示，改 `appsettings.json` 也会被钳制）：
> `FastFillIntervalMs` → `0~5000`（**上限 5000ms，防止单 tick 长时间阻塞**：写成 600000 会让一个 tick 卡约 100 分钟）；
> `FastFillConfirmIntervalSeconds` → `1~60`（0 按 1 秒；999 按 60 秒）。
> 未知节点 `skip` 单轮最多 **200** 条（`FastFillMaxSkipsPerTick`）：达到上限打 Warning
> 「本轮跳过 N 条后暂停」并结束本轮，避免节点号配错时一个 tick 遍历整个待发队列。

> ⚠️ 窗口**只拦插入，不拦发送**：若窗口里正好是 10 条 `send=0` 待发（`active=10`、`pendingSend=10`、`inFlight=0`），
> 必须照常进入 `Ready` 把它们发出去；否则会复现「窗口塞满就不发」的老毛病。
> 真正的发送闸门是 `HandleReady` 的**在途数**（`inFlight`）——发一条在途 +1，因此稳态最多 10 条在途。

**窗口满时的释放路径**（否则会永久卡住）：

1. **补查确认丢弃**（每 60 秒，最多 200 条，轮转窗口）：位置已变 `&3-{node}-%` → `disposed=1` → 退出在途计数 → 窗口释放；
2. **重发次数用尽**（`resend_count >= MaxResendCount`，默认 24 次）→ 标记 `send=3`（失败）→ 退出在途计数 → 窗口释放；
3. **未知节点跳过**（`SkipOnUnknownNode=true`）→ `send=2` → 退出在途计数；
4. 页面可对 `send=3` 记录执行**重新入队**（`ResetRecordsAsync` → `send=0`，重新计入窗口）或**删除**。

**重排队（不是直接重发）**：每 **5 分钟**一轮（`ResendIntervalMinutes`，默认 `5`）。命中「节点 `ON` + 状态 `G/Y` + 错误码允许」的记录
只会被**放回待发队列**（`send=0`），**绝不直接下发丢弃命令**；真正发送仍由状态机按窗口一条条进行（详见 §6）。
不在允许丢弃的时间段 → 只查库、等下一个时间窗口；窗口满（`inFlight >= 窗口`）→ **仍然执行重排队**（放回 `send=0` 才能释放窗口），但**没有到期候选时连 `STATUS-REQUEST` 都不发**。

> ⚠️ **重排队死锁修复对默认现场（`EnableFastFill=false`）同样生效**（不受快速填窗开关控制）：
> 窗口满（`inFlight >= 窗口`）**且有到期候选**时，会发 **1 条** `STATUS-REQUEST` 并把到期记录放回 `send=0` 以释放窗口
> —— 这**可能比 1.0.6.7 更早重发未确认记录**（即出现**重复 `COMMENT`**）。
> 现场启用前请先在**测试环境**验证「慢冰箱 / 节点未就绪」场景下重复 `COMMENT` 是否可接受。

### 4. 确认口径（**没有 ACK**）

- 厂商手册 DCAU-H00.816.12.01.02.pdf 第 498 页 §8.59：`ACK` / `NAK` 是 **Dream→GUI 的通知**，
  只对应 `ADVANCED-CONFIG-CHANGE`、`CLEAR-TUBES`、`COMMENT I021~I024` 这 **6 条 GUI 命令**；
  §9.3：`COMMENT S002` **没有任何回复/ACK 定义**。
- 因此 **Host LIS 发出的 `COMMENT S002`（TRASH / DELIVER）/ `ORDER` 没有 ACK**。
  正确语义是「**写成功 = 已投递**」：socket 写入并 Flush 成功即视为投递成功（返回标记 `OK:SENT`）。
- **丢弃是否真的成功**由数据库判定：`t_sample.t_location` 变为 `&3-{node}-%` 即视为已丢弃，
  等待 `MaxWaitDiscardCount` 个循环确认（见 §3）。

### 5. 后期补查（确认丢弃）

- 每 **60 秒**（节流，与当前状态机状态无关）对 `send=1 AND disposed=0` 的记录复查一次，**单次最多 200 条**。
- **只查库、不发任何指令**（不探测、不下发丢弃命令）；与 5 分钟一轮的「重排队检查」是两件事。
- **轮转窗口**：只查 `ID > 游标` 的一批（`ORDER BY ID LIMIT 200`），返回本批最大 ID 作为下一轮游标；
  查完（返回 0）→ 游标归零，下一轮从头。**保证超过 200 条时尾部记录最终也会被复查到**。
- 对 `Aptio.SrmNodeIds` 中每个节点各查一次：
  `SELECT sample_id FROM t_sample WHERE t_location LIKE '&3-{node}-%' AND sample_id IN (<本批条码>)`
- 命中者批量置 `disposed=1, disposed_time=<当前时间>`（仍限定 `send=1 AND disposed=0`），返回命中条数；
  **仅在有命中时记 Information 日志**。
- 目的：覆盖「已投递、但当时未观测到丢弃（超时/对端繁忙/状态机切走）」的样本，使其最终被确认并纳入 10 天历史清理。

### 5.1 队列的三个计数（页面展示口径）

| 计数 | DB 方法 | SQL | 含义 |
|------|---------|-----|------|
| **待发** | `CountPendingSendAsync()` | `SELECT COUNT(*) FROM sam_dispose_status WHERE send=0` | 排队中、尚未下发丢弃命令 |
| **待确认** | `CountPendingConfirmAsync()` | `SELECT COUNT(*) FROM sam_dispose_status WHERE send=1 AND disposed=0` | 已下发但未观测到丢弃 |
| **失败** | `CountFailedAsync()` | `SELECT COUNT(*) FROM sam_dispose_status WHERE send=3 AND disposed=0` | 重发次数用尽，等待人工处置 |
| （合计） | `CountPendingAsync()` | `SELECT COUNT(*) FROM sam_dispose_status WHERE send=0 OR (send=1 AND disposed=0)` | 待发 + 待确认（**不含失败**） |

> 列表接口 `GetPendingRecordsAsync` 的口径是 `send IN (1,3) AND disposed=0 AND resend_count >= 阈值`
> （即「待确认 + 失败」），与上面的三个计数不一一对应 —— 页面应按「待发 / 待确认 / 失败」三个数字展示。

### 6. 重排队与失败处置（`send` 取值）

| `send` | 含义 | 后续动作 |
|--------|------|----------|
| `0` | 待投递（排队中） | 每轮取队首下发丢弃命令 |
| `1` | 已投递，等待确认丢弃 | 未确认 → 超过 `ResendIntervalMinutes`（默认 **5 分钟**）进入**重排队检查**：命中条件则**放回待发队列（`send=0`）**，由状态机按窗口一条条重新发送（`resend_count+1`、`send_time` 刷新）；`resend_count` 达 `MaxResendCount` → 标记 `send=3` |
| `2` | 跳过（样本所在节点不在 `Aptio.SrmNodeIds`，且 `SkipOnUnknownNode=true`） | 不再重发；按 10 天历史清理 |
| `3` | **失败**（重发次数用尽） | 不再自动重发；页面可查看 / 重新入队（`ResetRecordsAsync` → `send=0`）/ 删除（`DeleteRecordsAsync`） |

- **重排队检查触发条件**：`send=1 AND disposed=0 AND send_time <= now - ResendIntervalMinutes`；
  `MaxResendCount > 0` 时再要求 `resend_count < MaxResendCount`。
- **重排队检查频率**：**每 5 分钟一轮**（`ResendIntervalMinutes`，默认 `5`；`<=0` = 关闭重排队）。
  60 秒一轮的**补查**（确认丢弃，见 §5）保持不变，且**只查库、不发任何指令**。
- **重排队流程**（`DisposeSampleWorker.MaybeRequeueForResendAsync`）：

  | 步骤 | 条件 | 动作 |
  |------|------|------|
  | 1 | 不在允许丢弃的时间段（`RunDays` / `TimeRanges`） | **只查库，不发任何指令**，等下一个可丢弃时间窗口 |
  | 2 | 发送窗口满（`inFlight = send=1 且 disposed=0` ≥ `MaxOnetimeSelectDiscardCount`） | **仍然执行本轮重排队检查**（窗口满时更需要重排队把记录放回 `send=0` 才能释放窗口）；**无到期候选时依旧不发任何指令**（连 `STATUS-REQUEST` 都不发） |
  | 3 | 在工作时间且**有到期候选**（窗口满也照样走这一步） | 发 **1 条** `STATUS-REQUEST`（每轮仅此一条）；无候选则连 `STATUS-REQUEST` 都不发 |
  | 4 | 记录所在节点满足 `Mode=ON` + `Status ∈ {G,Y}` + `ErrorCode` 在允许列表 | **放回待发队列**（`send=0`；不改 `send_time`、不增 `resend_count`） |
  | 5 | 节点不满足上述条件 | **一律等待，不重排队**（日志带 `mode=STB → wait`，10 分钟节流）。判定口径受测试开关 `AllowStandbyDispose` 影响，见下方「6.1 可丢弃判定与测试开关」 |
  | 6 | 位置里取不到节点号 | 按 `SkipOnUnknownNode` 处理：`true` → 标记 `send=2`（skip sentinel）；`false` → 跳过 |

- **只重排队、不直接发**：重排队环节**绝不下发 `COMMENT S002 TRASH`**，也**绝不批量直接发**；
  记录只是回到 `send=0` 队列，真正的发送仍由正常状态机按**发送窗口一条条**进行（见 §3.1）。
- **重发次数只在真正发送成功时 +1**：`UpdateStatusAsync(barcode, "send")` 写
  `resend_count = resend_count + IF(send_time IS NULL, 0, 1), send=1, send_time=@Now, checkcount=0`
  （新记录 `send_time IS NULL` → `+0`；被重排队的记录 `send_time` 非空 → `+1`；
  页面 reset 把 `send_time` 置 `NULL` → 视为新周期 → `+0`）。
  达到 `MaxResendCount`（默认 **24**）→ 标记 `send=3`（失败）→ 退出在途计数 → 窗口释放。
- **`STB` 的默认口径**：手册 Appendix F 明确 `STB`（Stand-by）= carriers **NOT processed**、Automation Map **OFF**，
  即「待命/未处理」；默认（`AllowStandbyDispose=false`）下 `STB/GTO/PA/OFF/FR/SH/EXE` 一律等待，只有 `ON` 才可能重排队。
  `GetNodeMode` / `DescribeNode` 仅用于日志说明，不参与判定。
- **`resend_count` 列不可用**：旧表迁移（`ALTER TABLE … ADD COLUMN resend_count`）失败时，
  `IDisposeDatabaseService.ResendColumnReady` 为 `false` → 重排队与失败标记**自动停用**，
  并每 10 分钟告警一次；页面接口应据此返回 503。
- **队首不卡死**：`HandleNone` 先看 `send=0` 的数量，`> 0` 直接回到 Ready 继续发（不再依赖「本轮新插入 > 0」），
  因此候选条码已存在（去重导致 `inserted=0`）时也不会停止发送。
- **队列硬上限**：`MaxPendingRecords > 0` 时，待处理数达到上限后本轮不再插入新记录（只 Warning）。

### 6.1 可丢弃判定与测试开关 `AllowStandbyDispose`

判定实现：`SrmStatusDecoder.IsNodeReady(message, nodeId, allowedErrors, allowStandby)`（`Decode` 同口径）。

| `AllowStandbyDispose` | 可丢弃条件 |
|------------------------|------------|
| `false`（**默认**） | `Mode == "ON"` 且 `Status ∈ {G,Y}` 且 `ErrorCode` 在允许列表 |
| `true`（测试开关） | `Mode ∈ {ON, STB}` 且 `Status ∈ {G,Y,P}` 且 `ErrorCode` 在允许列表 |

- **`GTO` / `PA` / `OFF` / `FR` / `SH` / `EXE` 始终不允许**（与开关无关，开关打开也不放行）。
- **业务含义**：`STB` **不允许进新样本**，但允许执行丢弃；本开关只控制「是否允许 `STB`/`P` 参与丢弃判断」，
  **不涉及进样本逻辑**。
- **开关热生效**（`[Restart(RestartLevel.Hot)]`）：页面「允许 Stand-by 丢弃（测试）」保存即生效，无需重启。
- **现场口径**：厂商手册对 `STB` 的结论保持不变（见 `docs/APIO-PROTOCOL.md`）；
  本开关是**现场临时测试手段**，现场测试后再决定是否长期开启。
- 开关放行时日志会打一条 `STB/P allowed by AllowStandbyDispose=true (node … mode=… status=…)`（10 分钟节流），便于确认开关生效。

---

## WorkListCleaners（数组，每个仪器一个配置，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `false` | 该仪器清理开关 |
| `InstrumentName` | string | `Immulite2000` | 仪器名称（作为日志模块名） |
| `MdbFilePath` | string | `` | MS Access 数据库路径（Windows 需要） |
| `CentralinkIp` | string | `` | Centralink 服务器 IP |
| `CentralinkPort` | int | `0` | Centralink 端口 |
| `CentralinkDriver` | string | `AUTO` | Centralink 驱动模式 |
| `EnableCentralink` | bool | `false` | 是否上报 Centralink |
| `LoopIntervalSeconds` | int | `60` | 清理循环间隔(秒) |
| `TimeSpanMinutes` | int | `5` | 无样本判定时间阈值(分钟) |
| `Prefix` | string | `ERR` | 无样本标记前缀 |
| `NaPrefix` | string | `NA` | NA 结果标记前缀 |
| `SampleIdLenMin` | int | `0` | 样本号最小长度（0=不校验） |
| `SampleIdLenMax` | int | `0` | 样本号最大长度（0=不校验） |
| `CheckSampleIdLen` | bool | `false` | 是否校验样本号长度 |
| `NoSampleLasFlag` | string | `NS` | 无样本 LAS 标志 |
| `NaResultLasFlag` | string | `NA` | NA 结果 LAS 标志 |
| `NaResultSqlWhere` | string | `` | NA 结果 SQL 过滤条件 |

> 需开启 `Features.Immulite.WorkListCleaner=true`；MS Access 依赖仅 Windows 可用。

---

## WebAdmin（Web 管理界面，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `true` | 是否启用 Web 管理界面 |
| `BindIp` | string | `0.0.0.0` | 绑定地址（0.0.0.0=所有网卡） |
| `Port` | int | `5100` | Web 端口（防火墙需放行） |
| `ErrorRetentionDays` | int | `30` | 错误记录保留天数 |
| `HealthCheckIntervalSeconds` | int | `60` | 健康检查间隔(秒) |
| `AdminPassword` | string | `Advia1` | **管理密码**：Web 管理界面破坏性动作的鉴权口令。**热生效**（每次动作从 appsettings.json 读取，改完立即生效，无需重启）。页面**不显示、不可改**；只能后台改本文件。置空（`""`）→ 所有破坏性端点一律拒绝（403 未配置管理密码） |
| `AuthMemoryMinutes` | int | `5` | **管理验证记忆（分钟）**：密码验证成功后服务端发一个**短令牌**（`X-Admin-Token`），默认 5 分钟内后续破坏性动作不再重复弹密码。**热生效**；取值 0~60（0 = 关闭，每次动作都弹密码）。详见下方「管理验证记忆」小节 |

### 管理密码（`WebAdmin:AdminPassword`）

| 事项 | 口径 |
|------|------|
| 默认值 | `Advia1`（首次部署请立即改成现场口令） |
| 修改方式 | **只能后台改 `appsettings.json`**（页面不显示、不可改；`PUT /api/config`、`PUT /api/config/batch` 均拒绝写入该路径，返回 400） |
| 生效方式 | 热生效：每次破坏性动作都重新读配置文件，后台改完立即生效，**无需重启服务** |
| 置空 | `""` → 所有破坏性端点返回 **403 `Admin password not configured`**（页面提示「未配置管理密码」） |
| 页面可见性 | `GET /api/config` 会把本字段打码为 `"***"`（键名含 `Password`，与其他密码字段一致） |
| 备份可见性 | `GET /api/config/download`（**需鉴权**）返回**完整原文**，含本字段与数据库密码——下载件等同明文口令，按密件保管 |

**鉴权模型（每次动作一次，不建会话 token）**

1. 前端 `GET /api/auth/challenge` → 取一次性 nonce（32 字节随机，**60 秒有效、一次性、仅存内存**，进程重启即失效）；
2. 前端用**纯 JS** 计算 `hash = SHA-256(密码 + nonce)` → **十六进制小写**（64 字符）；
3. 请求带两个头：`X-Admin-Nonce: <nonce>`、`X-Admin-Hash: <hash>`；
4. 后端用同一 nonce 重算 `SHA256.HashData(UTF8(密码 + nonce))`（十六进制小写）并**常量时间**比较；
5. nonce **无论成败都消费掉**（重放必然 401）；密码与哈希**不写日志**。

### 管理验证记忆（`WebAdmin:AuthMemoryMinutes`）

| 事项 | 口径 |
|------|------|
| 默认值 | `5`（分钟）；取值范围 0~60，超范围按 0/60 截断；配置缺失/非法 → 回落 5 |
| 生效方式 | **热生效**（每次破坏性动作重新读配置，改完立即生效，无需重启） |
| `0` = 关闭 | 不发短令牌 → **每次破坏性动作都重新弹密码**（回到「每次动作一次鉴权」的原始口径）。已签发的旧令牌最长在其自身有效期内（≤5 分钟）仍可用 |
| 令牌 | 服务端生成 **32 字节随机 → 64 位十六进制小写**；响应头 `X-Admin-Token` |
| 有效期 | **固定** `AuthMemoryMinutes` 分钟（从密码验证成功起算），**不滑动续期**（用一次不重置计时） |
| 服务端存储 | **只存进程内存**（令牌 + 过期时刻 + 当前管理密码的 SHA-256 指纹）；**不保存明文密码**；进程重启 → 全部失效 |
| 浏览器存储 | **只存内存**（页面 JS 变量），**不落 localStorage/sessionStorage** → **刷新页面即失效**，下一个动作重新弹密码 |
| 失效条件 | 令牌过期 / 管理密码被改（指纹不匹配 → 令牌立即作废）/ 服务端进程重启 / 页面刷新 |
| 覆盖范围 | **所有挂 `AdminAuthFilter` 的端点**（保存配置、批量保存、重启、进程重启、上传/应用升级、清错误、下载配置原文、丢弃队列重排队/删除） |
| 用法 | 后续请求带 `X-Admin-Token: <token>` 即可（可重复使用，直到过期）；仍可继续用 `X-Admin-Nonce` + `X-Admin-Hash` |
| 失效后的行为 | 服务端返回 **401 `Admin authentication failed`**；页面清掉内存令牌并重新弹密码 |

**安全边界（务必知悉）**

- 短令牌是 **bearer 凭证**：**谁拿到谁就能用**，不需要再输密码。令牌只在其有效期内有效（默认 5 分钟），过期/改密码/重启即作废。
- 当前 Web 管理走 **HTTP 明文**：抓包者拿到 `X-Admin-Token` 后，可在**其剩余有效期内（最长 5 分钟）重放**破坏性动作 —— 这比原来「60 秒一次性 nonce」的重放窗口**更长**。**强烈建议上 HTTPS（反向代理终止 TLS）后再开放管理界面**。
- 令牌**不落盘**（服务端内存 + 浏览器内存）：既不会随配置文件/备份泄漏，也无法在换浏览器/换人时复用；但也意味着**页面一刷新就要重新输密码**（这是刻意的收敛）。
- 建议在**不熟悉的现场**把 `WebAdmin:AuthMemoryMinutes` 设为 `0`（每次都弹密码），或缩短到 1~2 分钟。

**HTTP 明文环境下的局限（务必知悉）**

- 当前 Web 管理走 **HTTP 明文**（`http://<host>:5100`），哈希只解决「不直接传明文口令」，**不防中间人（MITM）**：抓包者拿到 `X-Admin-Hash` 即可在 60 秒窗口内重放该动作（nonce 一次性只能防「同一次动作重复提交」，不能防「抓到包后立刻用」）。
- 开启「管理验证记忆」后，抓包者拿到 `X-Admin-Token` 的**重放窗口更长**（最长 5 分钟）→ 更加需要 HTTPS。
- 局域网可信是当前前提；要进一步收紧，请：① 上 HTTPS（反向代理终止 TLS）后再开放；② 或限制 `BindIp` 只监听管理网段；③ 或在防火墙上只放行运维终端。
- 传输层明文时，**管理密码不要与其他系统共用**。

**需要管理鉴权的端点**（缺头/错哈希/令牌无效 → **401 `Admin authentication failed`**）：

| 方法 | 路径 | 说明 |
|------|------|------|
| PUT | `/api/config` | 单条保存配置 |
| PUT | `/api/config/batch` | 多字段一次保存（一次请求、一次鉴权） |
| GET | `/api/config/download` | 下载 appsettings.json 原文（含密码） |
| POST | `/api/update/upload` | 上传升级包 |
| POST | `/api/update/apply` | 应用升级 |
| POST | `/api/restart` | 内部重启 |
| POST | `/api/restart/process` | 进程级重启 |
| DELETE | `/api/errors` | 清空错误记录 |
| POST | `/api/dispose/pending/reset` | 重排队 |
| POST | `/api/dispose/pending/delete` | 删除待处置记录 |

> 以上端点**同时接受** `X-Admin-Nonce` + `X-Admin-Hash`（一次性 nonce 口径）与 `X-Admin-Token`（管理验证记忆短令牌口径）。带有效令牌 → 直接放行；令牌无效但同时带了 nonce/hash → 仍走 nonce/hash 校验（旧令牌不会挡住「用新密码重新鉴权」）。

**免鉴权的端点**：`GET /api/health`、`GET /api/health/history`、`GET /api/errors`、`GET /api/errors/summary`、`GET /api/logs/*`、`GET /api/config`（打码）、`GET /api/dispose/pending`、`POST /api/dispose/pending/recheck`、`POST /api/config/test-db`、`GET /api/auth/challenge`、`GET /api/version`、`GET /api/restart/status`、`GET /api/update/status`。

---

## DMS（DMS 自动下单，需先开对应 `Features.Dms.*` 开关，需重启）

### 公共配置（三个功能共用）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `false` | DMS 功能总开关 |
| `DbHost` | string | `127.0.0.1` | DMS 数据库地址 |
| `DbPort` | int | `3306` | DMS 数据库端口（支持自定义） |
| `DbUser` | string | `root` | DMS 数据库用户名 |
| `DbPassword` | string | `` | DMS 数据库密码 |
| `DbName` | string | `dms` | DMS 数据库名 |
| `LoopIntervalSeconds` | int | `60` | 循环间隔(秒) |

### DMS.PitStop（PitStop 监控，对应 `Features.Dms.PitStopMonitor`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `PitStop.TimeoutMinutes` | int | `1` | PitStop 超时判定(分钟)；监控启停由功能开关 `Features.Dms.PitStopMonitor` 控制 |

### DMS.StatusCorrection（状态修正，对应 `Features.Dms.StatusCorrection`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `StatusCorrection.AutoModifyTestStatus` | string | `1` | 修正模式：1=不修正，2=同步，3=强制F |
| `StatusCorrection.IgnoreFlags` | JSON 数组 | `null` | 忽略标志列表（仪器结果报告这些标志时自动置 V）；**新格式（表单保存写这里）** |
| `StatusCorrection.IgnoreFlagList` | string | `` | 忽略标志列表（旧格式，逗号分隔）；仅在 `IgnoreFlags` 为 null 时使用（兼容） |

> 注：清理 `reqtestresult` 中的错误/空结果（`flgstatus` E/R/P 或 `valresult1` 空）已独立为 `DMS.EmptyResultCleanup` 功能，不再随状态修正执行。

> **复合值 JSON 化说明**（v1.0.6.0）：多值配置已改用 JSON 结构（表单保存写新格式）；旧字符串格式仍兼容（新键为 null 时才使用旧值）。PUT /api/config 校验将持续执行（类型/键不匹配返回 400 + 英文错误）。

### DMS.EmptyResultCleanup（空结果清理，对应 `Features.Dms.EmptyResultCleanup`）

清理 `reqtestresult` 中 `flgstatus` 为 `E`/`R`/`P` 或 `valresult1` 为空的记录。

- 无独立子配置字段（使用公共 `LoopIntervalSeconds`）
- 被清理的记录（`codsid` / `codtest`）逐条写入日志

### DMS.SampleCleanup（样本清理，对应 `Features.Dms.SampleCleanup`）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `SampleCleanup.TriggerRules` | JSON 数组 | `null` | 工单删除规则；**新格式（表单保存写这里）**；示例：`[{"TestName":"RMSMP","TimeoutMinutes":1}]`；超时 0=到期即删 |
| `SampleCleanup.TestTriggerSampleDeletion` | string | `` | 触发样本删除的测试名（旧格式，`测试名:超时分钟;`）；仅在 `TriggerRules` 为 null 时使用（兼容） |
| `SampleCleanup.AptioAction` | 枚举 | `Cancel` | 删除工单后向 Aptio 发送的联动动作：`None`=不发 / `Cancel`=发 `ORDER…C` 取消报文（Test-Request 以 `|` 分隔，索引 17 起）/ `Complete`=发 `COMMENT S002^{sid}\COMPLETE^S`（全部测试完成）；未设置时回退下方兼容开关 |
| `SampleCleanup.SendCancelMessageToAptio` | bool | `false` | 旧版兼容开关：仅当 `AptioAction` 未设置时生效，决定是否向 Aptio 发送 `ORDER…C` 取消报文 |

> 联动报文发送后会以 **Information** 级完整记录到日志（含帧内容，便于排查）。
> 删除表列表由程序动态扫描 `information_schema`（含 `codsid`/`codoid` 列的表）自动生成，无需配置（每天重建模板）。

---

## Web 管理 API

所有的 Web 管理与远程集成以 HTTP API 交互（均在 **/api** 前缀下），支持 GET 的接口可浏览器直接打开，效果同功能配置页标注。

### 配置路径约定

- **层级分隔符只认 `:`**（.NET 配置约定），`"."` 属于键名的一部分（如 `Logging:LogLevel:Microsoft.Hosting.Lifetime`）
- 示例：`Features:Aptio:DisposeSample`、`DMS:SampleCleanup:TriggerRules`

### PUT /api/config 的校验

值与配置属性类型不匹配或 path 未知时，返回 **HTTP 400 + 英文原因**（如 `Value must be a boolean (true/false)`）。
校验通过后值将**规范化**（`"true"`→`true`、`"3306"`→`3306`）后落盘。响应的 `restart.level` 指示生效方式：Hot=保存即生效 / InnerRestart=点页面「重启服务」按钮 / ProcessRestart=需操作系统级重启进程。

**该端点需要管理鉴权**（`X-Admin-Nonce` + `X-Admin-Hash`，见上文「管理密码」）。
密码类路径（键名含 `Password`/`Pwd`）的值若为 `"***"` 或空串 → **保持现有值不变**（页面回显的掩码不会覆盖真值），响应 `changed=false`。

**段节点保护（防配置结构损坏）**：写入前会比对该 path **当前值的 JSON 类型**——
若当前是**对象/数组**（段节点）而新值不是同类，则返回 **HTTP 400**：

```
Configuration path 'Logging:LogLevel' is an object/array node;
cannot replace an object/array node with a scalar; edit a leaf path instead
```

即：`Logging:LogLevel`（对象）**不能**写成 `"Warning"`，必须写叶子 `Logging:LogLevel:Default`；
否则整个 `Logging:LogLevel` 子树会被替换成字符串，重启后子键全部丢失。
叶子路径照常可写（字符串 / 数字 / 布尔 / 数组字段，如 `Aptio:SrmNodeIds`）。

### PUT /api/config/batch —— 多字段一次保存（需鉴权）

一次请求、一次鉴权，把多个字段一起保存（页面「保存全部」只弹一次密码框）。

请求体：

```json
{
  "changes": [
    { "path": "Aptio:Ip", "value": "192.168.1.10" },
    { "path": "Aptio:Port", "value": 2055 },
    { "path": "Aptio:Database:Password", "value": "***" }
  ]
}
```

- **全量校验、一次落盘**：先对全部 changes 做路径合法性（`RestartLevelResolver`）与值类型校验；**任一非法 → 400，且一个字段都不写**（不会落半截状态）；全部合法 → 一次读盘 + 一次写盘。
- **段节点保护**：与单条 PUT 同一套规则 —— 当前值是对象/数组而新值不是同类 → 400（`cannot replace an object/array node with a scalar; edit a leaf path`）。
- **密码字段规则**：值为 `"***"` 或空串 → **保持现有值不变**（跳过，不计入 `count`，计入 `skipped`）；给新值才写。
- **受保护路径**：`WebAdmin:AdminPassword` 不可经 API 修改 → 400（只能后台改 appsettings.json）。
- 响应：`{ "success": true, "count": <实际写入条数>, "skipped": <保持不变的条数>, "restart": { "level": "Hot|InnerRestart|ProcessRestart", "message": "..." } }`；`level` 取所有变更里**最强**的生效方式。

### POST /api/config/test-db —— 数据库连通性测试（免鉴权）

`POST /api/config/test-db?target=aptio|dms`（`target` 默认 `aptio`）

- **只测【已保存配置】**：`Aptio:Database:*` 或 `DMS:Db*`；**不读取请求体里的新值**，**不修改任何配置**。
- 连接方式：`MySqlConnection` + **5 秒连接超时**，`OpenAsync` + `PingAsync`。
- 响应：`{ "success": true|false, "target": "aptio|dms", "elapsedMs": 123, "message": "Connection succeeded | Connection failed: ..." }`。
- 失败时 `message` 里**不含密码**（密码字段被替换为 `***`）。
- 免鉴权的原因：它只读已保存配置、不产生副作用；但**错误信息会回显主机/库名**，请只在受控网络内使用。

### GET /api/config/download —— 配置原文下载（需鉴权）

需管理鉴权（`X-Admin-Nonce` + `X-Admin-Hash`）。鉴权通过后返回 **appsettings.json 完整原文**（**含所有明文密码**：数据库密码、`WebAdmin:AdminPassword`），
用于现场备份/取证 —— **下载件等同明文口令文件，按密件保管**。
对照：`GET /api/config`（免鉴权）返回**打码**后的配置（所有 `Password`/`Pwd` 键 → `"***"`）。

### GET /api/auth/challenge —— 取一次性 nonce（免鉴权）

响应：`{ "nonce": "<64 位十六进制小写>", "expiresInSeconds": 60 }`。nonce **一次性、60 秒有效、仅存内存**。

### 可配置项一览

通过 Web 界面「API 说明」页查看最新的 51 项可配置项列表（含分组、路径、类型、生效方式、示例值、说明），
是与源码模型 `[Restart]` 特性同步维护的，未来新增配置会一并更新。

> 例外：`WebAdmin:AdminPassword` **故意不在该列表里**——管理密码不通过页面展示/修改（只能后台改 appsettings.json），
> 故「API 说明」页的可配置项数量**不包含**它（模型里新增该属性不会让页面数量 +1）。

---

## 错误记录说明

错误日志有**两条存储路径**（同时生效）：
1. **文件日志**：ERROR 及以上级别写入 `logs/<日期>/UniFlow_Error_<序号>.log`
2. **SQLite 管理库**：错误写入 `uniflow_admin.db`，通过 Web 界面"错误日志"页 / `GET /api/errors` 查询

记录增删改操作会写入**条码/样本号等特征**；SQL 语句仅在**失败时**记录。