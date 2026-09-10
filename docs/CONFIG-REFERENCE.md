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
| `Aptio.Delivery` | bool | `false` | 标本递送 |
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

> Web 仪表盘页提供滑块开关，与这些字段一一对应（路径格式 `Features:Aptio.DisposeSample` 等）。

---

## Aptio（仪器连接，需重启）

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

---

## AptioAutoProcess.Dispose（样本丢弃参数，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `CommandType` | int | `0` | 命令类型（0=视图，其他=存储过程） |
| `CommandName` | string | `view_overtimestoragesample` | 查询命令名/视图名 |
| `DiscardRunDate` | string | `1,2,3,4,5,6,7` | 运行日期（1-7 周一~周日，逗号分隔） |
| `DiscardTimeRange` | string | `01:30-05:45,3800;18:00-07:00,7500;` | 丢弃时间段与阈值：`开始-结束,阈值;` 分号分隔 |
| `LoopIntervalSeconds` | int | `3` | 循环间隔(秒) |
| `MaxWaitDiscardCount` | int | `2` | 最大等待确认次数 |
| `MaxOnetimeSelectDiscardCount` | int | `8` | 单次最大选择丢弃数 |
| `AllowSrmErrorCode` | string | `0000,0F0A,0E59,AAAA` | 允许的 SRM 错误码（逗号分隔） |
| `EnableTestNameDispose` | bool | `false` | 是否启用按测试名丢弃 |
| `DisposeTestName` | string | `` | 测试名（配合 EnableTestNameDispose） |
| `SkipOnUnknownNode` | bool | `true` | 未知节点是否跳过 |

---

## AptioAutoProcess.Deliver（标本递送，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `false` | 递送功能开关 |
| `TestName` | string | `` | 递送测试名 |
| `DeliveryListFilePath` | string | `` | 递送清单文件目录路径 |

> 需同时开启 `Features.Delivery=true` 才生效。

---

## AptioAutoProcess.Priority（优先级，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `false` | 优先级功能开关 |
| `TestName` | string | `` | 优先级测试名 |

> 需同时开启 `Features.Priority=true` 才生效。

---

## AptioAutoProcess.Export（SRM 导出，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `ExportTime` | string | `08:30` | 每日导出时间(HH:mm) |
| `LoopIntervalSeconds` | int | `60` | 导出检查循环间隔(秒) |
| `LogRetentionDays` | int | `30` | 导出文件保留天数 |
| `OutputPath` | string | `DisposeFile` | 导出文件目录（相对安装目录） |

> v1.0.5.2 修复：导出文件直接写到 `<OutputPath>/` 单一目录（不再有 `DisposeFile/DisposeFile` 双重嵌套）。

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

> 需开启 `Features.ImmuliteWorkOrderClean=true`；MS Access 依赖仅 Windows 可用。

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

## DMS（DMS 自动下单，需先开 `Features.DmsAutoOrder`，需重启）

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Enabled` | bool | `false` | DMS 功能总开关 |
| `DbHost` | string | `127.0.0.1` | DMS 数据库地址 |
| `DbPort` | int | `3306` | DMS 数据库端口（支持自定义） |
| `DbUser` | string | `root` | DMS 数据库用户名 |
| `DbPassword` | string | `` | DMS 数据库密码 |
| `DbName` | string | `dms` | DMS 数据库名 |
| `LoopIntervalSeconds` | int | `60` | 循环间隔(秒) |
| `EnablePitStopMonitor` | bool | `false` | 是否启用 PitStop 监控 |
| `PitStopTimeoutMinutes` | int | `1` | PitStop 超时判定(分钟) |
| `AutoModifyTestStatus` | string | `1` | 状态修正模式：1=不修正，2=同步模式，3=强制F模式 |
| `IgnoreFlagList` | string | `` | 忽略标志列表（逗号分隔） |
| `TestTriggerSampleDeletion` | string | `` | 触发样本删除的测试名（`测试名:超时分钟;`） |
| `DeleteTableList` | string | `` | 删除涉及的表列表 |
| `SendCancelMessageToAptio` | bool | `false` | 删除时是否发送取消消息到 Aptio |

---

## 错误记录说明

错误日志有**两条存储路径**（同时生效）：
1. **文件日志**：ERROR 及以上级别写入 `logs/<日期>/UniFlow_Error_<序号>.log`
2. **SQLite 管理库**：错误写入 `uniflow_admin.db`，通过 Web 界面"错误日志"页 / `GET /api/errors` 查询

记录增删改操作会写入**条码/样本号等特征**；SQL 语句仅在**失败时**记录。