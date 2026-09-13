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
| `SrmNodeIds` | string[] | `["09","16"]` | SRM 节点编号列表 |
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
| `DisposeSample.MaxOnetimeSelectDiscardCount` | int | `8` | 单次最大选择丢弃数 |
| `DisposeSample.AllowSrmErrorCode` | string | `0000,0F0A,0E59,AAAA` | 允许的 SRM 错误码 |
| `DisposeSample.SkipOnUnknownNode` | bool | `true` | 未知节点是否跳过 |

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
| `SampleCleanup.SendCancelMessageToAptio` | bool | `false` | 删除样本时是否发送取消消息到 Aptio（`ORDER…C` 取消整个样本的待处理测试） |

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

### 可配置项一览

通过 Web 界面「API 说明」页查看最新的 51 项可配置项列表（含分组、路径、类型、生效方式、示例值、说明），
是与源码模型 `[Restart]` 特性同步维护的，未来新增配置会一并更新。

---

## 错误记录说明

错误日志有**两条存储路径**（同时生效）：
1. **文件日志**：ERROR 及以上级别写入 `logs/<日期>/UniFlow_Error_<序号>.log`
2. **SQLite 管理库**：错误写入 `uniflow_admin.db`，通过 Web 界面"错误日志"页 / `GET /api/errors` 查询

记录增删改操作会写入**条码/样本号等特征**；SQL 语句仅在**失败时**记录。