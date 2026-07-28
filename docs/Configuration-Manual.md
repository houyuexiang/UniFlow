# UniFlow 配置手册

## 顶层配置结构

```
Logging/          ← 日志配置
Features/         ← 功能开关
Aptio/            ← 基础设施：连接与数据库
AptioAutoProcess/ ← 核心业务：标本处理
WorkListCleaners/ ← 工作列表清理器（多实例数组）
WebAdmin/         ← Web 管理控制台
DMS/              ← DMS 基础设施与自动工单
```

---

## Logging — 日志配置

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Logging:LogLevel:Default` | string | `"Information"` | 默认日志级别 |
| `Logging:LogLevel:Microsoft.Hosting.Lifetime` | string | `"Warning"` | 框架日志级别 |
| `Logging:LogLevel:UniFlow` | string | `"Information"` | 应用日志级别 |
| `Logging:Console:FormatterName` | string | `"simple"` | 控制台格式器 |
| `Logging:UniFlowFile:FileLoggingEnabled` | boolean | `true` | 是否启用文件日志 |
| `Logging:UniFlowFile:LogDirectory` | string | `"logs"` | 日志输出目录（相对/绝对路径） |
| `Logging:UniFlowFile:MaxFileSizeMb` | integer | `10` | 单个日志文件最大大小(MB)，超限自动拆分 |
| `Logging:UniFlowFile:RetentionDays` | integer | `30` | 日志保留天数，超期自动删除 |
| `Logging:UniFlowFile:LogLevel` | string | `"Information"` | 文件日志级别 |

**日志级别（LogLevel）：**

| 值 | 说明 |
|------|------|
| `Trace` | 最详细的调试信息 |
| `Debug` | 调试信息 |
| `Information` | 常规信息（默认） |
| `Warning` | 警告 |
| `Error` | 错误 |
| `Critical` | 严重错误 |

**日志文件结构：**
```
logs/
├── DisposeSampleWorker_2026-07-27.log    ← 按模块分文件
├── SrmExportWorker_2026-07-27.log
├── ImmuliteCleaner_2026-07-27.log
├── DmsAutoOrderWorker_2026-07-27.log
├── ...                                    
└── UniFlow_Error_2026-07-27.log          ← 所有 ERROR 及以上汇总
```

---

## Features — 功能开关

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Features:AptioAutoProcess` | boolean | `true` | 启用 Aptio 标本全流程处理（丢弃/递送/优先级/导出） |
| `Features:ImmuliteWorkOrderClean` | boolean | `false` | 启用 Immulite 工作列表清理 |
| `Features:DmsAutoOrder` | boolean | `false` | 启用 DMS 自动工单 |

---

## Aptio — 基础设施

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Aptio:Ip` | string | `"127.0.0.1"` | Aptio 服务器 IP 地址 |
| `Aptio:Port` | integer | `2055` | Aptio Socket 通信端口 |
| `Aptio:SrmNodeIds` | string[] | `["09", "16"]` | SRM 冰箱节点编号列表，支持多冰箱 |
| `Aptio:Database:Host` | string | `"127.0.0.1"` | MySQL 数据库地址 |
| `Aptio:Database:Port` | integer | `3306` | MySQL 端口 |
| `Aptio:Database:User` | string | `"root"` | MySQL 用户名 |
| `Aptio:Database:Password` | string | `"root"` | MySQL 密码 |
| `Aptio:Database:Database` | string | `"flexlab"` | 数据库名（Aptio FlexLab） |

---

## AptioAutoProcess — 核心业务

### Dispose — 定时丢弃

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Dispose:CommandType` | integer | `0` | 查询方式：`0`=SQL 视图, `1`=存储过程 |
| `Dispose:CommandName` | string | `"view_overtimestoragesample"` | 视图/存储过程名称 |
| `Dispose:DiscardRunDate` | string | `"1,2,3,4,5,6,7"` | 运行日，逗号分隔，1=周一 .. 7=周日 |
| `Dispose:DiscardTimeRange` | string | `"01:30-05:45,3800;..."` | 时间段+阈值，分号分隔多段，详见下方格式说明 |
| `Dispose:LoopIntervalSeconds` | integer | `3` | 状态机轮询间隔（秒） |
| `Dispose:MaxWaitDiscardCount` | integer | `2` | 最大等待丢弃确认次数，超限强制丢弃 |
| `Dispose:MaxOnetimeSelectDiscardCount` | integer | `8` | 单次最多选取丢弃数 |
| `Dispose:AllowSrmErrorCode` | string | `"0000,0F0A,0E59,AAAA"` | 允许的 SRM 错误码，逗号分隔，匹配视为 `0000` |
| `Dispose:EnableTestNameDispose` | boolean | `false` | 启用按测试名称丢弃 |
| `Dispose:DisposeTestName` | string | `""` | 按测试名称丢弃时匹配的测试名称 |
| `Dispose:SkipOnUnknownNode` | boolean | `true` | `true`=标本位置不在配置节点中时跳过, `false`=忽略节点检查继续丢弃 |

**CommandType 枚举：**

| 值 | 说明 |
|------|------|
| `0` | SQL 视图（`SELECT * FROM {CommandName} LIMIT {max}`） |
| `1` | 存储过程（`CALL {CommandName}()`） |

**DiscardTimeRange 格式：**
```
HH:MM-HH:MM,threshold;HH:MM-HH:MM,threshold;...
```

示例：`"01:30-05:45,3800;18:00-07:00,7500;"`

- `01:30-05:45,3800` — 凌晨 1:30 到 5:45 期间，SRM 存量超过 3800 时丢弃
- `18:00-07:00,7500` — 傍晚 18:00 到次日 7:00（跨天），存量超过 7500 时丢弃
- 多个时间段按顺序匹配，首次命中即生效

**DiscardRunDate 星期映射：**

| 值 | 含义 |
|------|------|
| `1` | 星期一 |
| `2` | 星期二 |
| `3` | 星期三 |
| `4` | 星期四 |
| `5` | 星期五 |
| `6` | 星期六 |
| `7` | 星期日 |

### Deliver — 标本递送

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Deliver:Enabled` | boolean | `false` | 启用递送功能 |
| `Deliver:TestName` | string | `""` | 匹配测试名称，扫描 `t_sample.test` 字段 |
| `Deliver:DeliveryListFilePath` | string | `""` | 文件批量递送目录路径，扫描该目录下 TXT 文件（每行一个条码） |

### Priority — 优先级标记

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Priority:Enabled` | boolean | `false` | 启用优先级标记 |
| `Priority:TestName` | string | `""` | 匹配测试名称，发 COMMENT S010 标记为 STAT |

### Export — 丢弃记录导出

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Export:ExportTime` | string | `"08:30"` | 每日自动导出时间（HH:MM 格式） |
| `Export:LoopIntervalSeconds` | integer | `60` | 导出检查间隔（秒） |
| `Export:LogRetentionDays` | integer | `30` | 导出文件保留天数 |
| `Export:OutputPath` | string | `"DisposeFile"` | 导出文件输出目录（相对程序目录） |

---

## WorkListCleaners — 工作列表清理器（数组）

支持多个清理器实例，每个实例独立配置。

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `[*]:Enabled` | boolean | `false` | 启用该实例 |
| `[*]:InstrumentName` | string | `""` | 仪器名称，用于日志标识（如 `"Immulite2000"`） |
| `[*]:MdbFilePath` | string | `""` | MS Access 数据库路径（如 `"D:\\Data\\maindata.mdb"`） |
| `[*]:CentralinkIp` | string | `""` | Centralink 服务器 IP |
| `[*]:CentralinkPort` | integer | `0` | Centralink 服务器端口 |
| `[*]:CentralinkDriver` | string | `"AUTO"` | Centralink 通信驱动模式 |
| `[*]:EnableCentralink` | boolean | `false` | 启用 Centralink LAS 标志推送 |
| `[*]:LoopIntervalSeconds` | integer | `60` | 清理循环间隔（秒） |
| `[*]:TimeSpanMinutes` | number | `5` | 无标本标记超时阈值（分钟） |
| `[*]:Prefix` | string | `"ERR"` | 无标本标记前缀 |
| `[*]:NaPrefix` | string | `"NA"` | NA 结果标记前缀 |
| `[*]:SampleIdLenMin` | integer | `0` | 标本 ID 最小长度检查（0=不检查） |
| `[*]:SampleIdLenMax` | integer | `0` | 标本 ID 最大长度检查（0=不检查） |
| `[*]:CheckSampleIdLen` | boolean | `false` | 是否启用标本 ID 长度检查 |
| `[*]:NoSampleLasFlag` | string | `"NS"` | 无标本 LAS 标志 |
| `[*]:NaResultLasFlag` | string | `"NA"` | NA 结果 LAS 标志 |
| `[*]:NaResultSqlWhere` | string | `""` | NA 结果查询附加 WHERE 条件（不包含 `where` 关键字） |

**CentralinkDriver 值：**

| 值 | 说明 |
|------|------|
| `"AUTO"` | 自动模式（ASTM C 记录格式） |
| 其他 | 标准模式（ASTM M 记录格式） |

---

## WebAdmin — Web 管理控制台

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `WebAdmin:Enabled` | boolean | `true` | 启用 Web 管理控制台 |
| `WebAdmin:BindIp` | string | `"localhost"` | 绑定 IP（`"localhost"`=仅本机, `"0.0.0.0"`=所有网卡） |
| `WebAdmin:Port` | integer | `5100` | 监听端口 |
| `WebAdmin:ErrorRetentionDays` | integer | `30` | 错误记录保留天数 |
| `WebAdmin:HealthCheckIntervalSeconds` | integer | `60` | 健康检查间隔（秒） |

---

## DMS — DMS 自动工单

| 路径 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `DMS:Enabled` | boolean | `false` | 启用 DMS 自动工单 |
| `DMS:DbHost` | string | `"127.0.0.1"` | DMS MySQL 数据库地址 |
| `DMS:DbPort` | integer | `3306` | DMS MySQL 端口 |
| `DMS:DbUser` | string | `"root"` | DMS 数据库用户名 |
| `DMS:DbPassword` | string | `""` | DMS 数据库密码 |
| `DMS:DbName` | string | `"dms"` | DMS 数据库名 |
| `DMS:LoopIntervalSeconds` | integer | `60` | 工单处理循环间隔（秒） |
| `DMS:EnablePitStopMonitor` | boolean | `false` | 启用 PitStop 表监控 |
| `DMS:PitStopTimeoutMinutes` | integer | `1` | PitStop stuck 记录超时（分钟） |
| `DMS:AutoModifyTestStatus` | string | `"1"` | 状态修正模式，详见下方枚举 |
| `DMS:IgnoreFlagList` | string | `""` | 忽略的标志列表，逗号分隔 |
| `DMS:TestTriggerSampleDeletion` | string | `""` | 测试触发标本删除，格式 `TestName:TimeoutMin;...` |
| `DMS:SendCancelMessageToAptio` | boolean | `false` | 删除标本时向 Aptio 发送取消命令 |
| `DMS:AptioConnectString` | string | `""` | Aptio 连接字符串（IP:Port），用于取消消息 |

**AutoModifyTestStatus 枚举：**

| 值 | 说明 |
|------|------|
| `"1"` | 不修改状态（仅清理 E/R/P 状态的结果） |
| `"2"` | 将 V/X/Y/Z 结果的 `flgtohost` 归零，让 Host 重新拉取结果 |
| `"3"` | 将 V/X/Y/Z 结果置为 F（Final），不再重新发送 |

**TestTriggerSampleDeletion 格式：**
```
TestName1:TimeoutMinutes1;TestName2:TimeoutMinutes2;...
```

示例：`"PARK:120;UNKN:60"` — 收到 PARK 测试 120 分钟后、UNKN 测试 60 分钟后自动删除该标本。

---

## 环境变量覆盖

所有配置项可通过环境变量覆盖，路径用 `__`（双下划线）分隔：

```bash
# 覆盖数据库密码
UniFlow__Aptio__Database__Password=newpassword

# 覆盖 Aptio IP
UniFlow__Aptio__Ip=192.168.1.100

# 覆盖功能开关
UniFlow__Features__AptioAutoProcess=true
```