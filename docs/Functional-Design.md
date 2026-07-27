# UniFlow 功能设计手册

## 1. 概述

UniFlow 是一个统一的实验室自动化工作流服务，基于 .NET 8 跨平台运行（Windows Service / Linux systemd）。它将多个独立的实验室自动化工具整合为一个可插拔的服务框架，各业务模块通过功能开关独立控制。

### 1.1 设计原则

- **模块化**：每个业务功能独立目录，独立 Worker，独立配置
- **可插拔**：通过 `Features` 开关控制模块启停，无需修改代码
- **跨平台**：同一二进制部署在 Windows 和 Linux
- **统一日志**：所有模块使用统一日志框架，分文件记录 + 错误汇总

### 1.2 系统架构

```
Program.cs
  ├── Common/                 公用基础层
  │   ├── Services/           AptioSocketClient, ASTM, SrmStatusDecoder, WorkTimeChecker
  │   └── Models/             公用配置模型
  ├── DisposeSample/          标本丢弃 + 递送 + 优先级
  ├── SrmExport/              丢弃记录导出
  ├── WorkListCleaner/        工作列表清理（多模块）
  └── DMSAutoOrder/           DMS 自动工单
```

---

## 2. 模块功能详述

### 2.1 DisposeSample — 标本全流程控制

**来源**：Delphi DisposeSample + Service_AptioAutoProcess.AptioProcess

**功能列表**：

| 功能 | 命令 | 说明 |
|------|------|------|
| 标本丢弃 | `COMMENT S002^{barcode}\TRASH^S` | 将标本从 SRM 丢弃至 IOM 输出口 |
| 标本递送 | `COMMENT S002^{barcode}\DELIVER^S` | 将标本递送至指定位置 |
| 优先标记 | `COMMENT S010^{barcode}^S` | 标记标本为 STAT（优先处理） |
| 取消测试 | `ORDER {barcode}...C...{test}` | 通过 GUI 消息取消指定测试 |

**状态机**：

```
None → (样本数>阈值) → Ready → (STATUS OK) → 发送丢弃命令 → Ack → (确认丢弃) → Dispose → 循环
                                        ↑                                                        |
                                        └──────────────────── STATUS-REQUEST ─────────────────────┘
```

**批次扫描**：在每个 Tick 周期内，先扫描 Deliver / Priority 命令队列批量执行，再进入 Dispose 状态机。

**配置项**：

| 参数 | 说明 |
|------|------|
| `SrmNodeId` | SRM 冰箱节点编号 |
| `CommandType` | 0=视图SQL, 1=存储过程 |
| `CommandName` | 视图/存储过程名称 |
| `DiscardRunDate` | 运行日期(1=周一..7=周日) |
| `DiscardTimeRange` | 时间段+阈值 |
| `EnableDeliver` | 启用递送功能 |
| `EnableUpdatePriority` | 启用优先级标记 |
| `DeliveryListFilePath` | 文件递送列表路径 |

### 2.2 SrmExport — 丢弃记录导出

**来源**：Delphi SRM_ExportDisposeSample

**功能**：每天定时（默认 08:30）查询前一日通过 SRM 丢弃的标本记录，导出为 TXT 文件。

**SQL 查询条件**：
- `t_location LIKE '&3-{NodeID}-%'` — IOM 输出位置
- `update_time` 为前一日范围

**输出文件格式**：
```
Export Time: 2026-07-27 08:30:00
Total: 42
────────────────────────────────────────────────────────────────────────────────
Barcode                        Location                       UpdateTime
────────────────────────────────────────────────────────────────────────────────
SAMPLE001                      &3-09-000001                   20260726
SAMPLE002                      &3-09-000002                   20260726
────────────────────────────────────────────────────────────────────────────────
```

### 2.3 WorkListCleaner — 工作列表清理

**来源**：ImmuliteWorkListCleaner

**架构**：多模块清理器模式

```
IWorkListCleaner (接口)
    ↑
    ├── ImmuliteCleaner        ← Immulite 工作列表清理
    ├── SampleCleaner           ← 未来可扩展
    └── ...                     ← 更多清理器
```

**清理器注册方式**：实现 `IWorkListCleaner` 接口，在 DI 中注册，Worker 自动发现并运行。

**ImmuliteCleaner 功能**：

| 功能 | 说明 |
|------|------|
| NoSample 标记 | 检测超时未测试的标本，加 `ERR` 前缀标记 |
| NA 结果处理 | 检测不可用结果，加 `NA` 前缀标记 |
| LAS 标志推送 | 通过 ASTM 协议向 Centralink 发送标志消息 |

**数据源**：
- MS Access (.mdb) — Worklist 表 + Result Information 表
- 通过 `System.Data.OleDb` 连接（仅 Windows 可用）

### 2.4 DMSAutoOrder — DMS 自动工单

**来源**：DMSAutoOrder (AutoTestTools)

**功能群组**：

| 服务 | 功能 | 说明 |
|------|------|------|
| PitStopMonitorService | PitStop 表监控 | 监控 stuck 记录，超时自动清理 |
| StatusCorrectionService | 状态修正 | 清理错误状态，修正 flgtohost 标志 |
| SampleCleanupService | 标本自动删除 | 测试触发 + 按表列表批量清理 |

**PitStop 监控**：
- 扫描 `pitstop%` 表
- 检测 `flgrunning <> ''` 的记录
- 超过配置超时时间（默认 1 分钟）自动删除

**状态修正模式**：

| 模式 | 行为 |
|------|------|
| 1 | 不修改（仅清理 E/R/P 状态） |
| 2 | 将 V/X/Y/Z 结果的 flgtohost 归零，重新发送 |
| 3 | 将 V/X/Y/Z 结果置为 F（Final） |

**测试触发删除**：
- 配置格式：`TestName:TimeoutMinutes;TestName:TimeoutMinutes`
- 超时后自动删除该标本在 DMS 中所有关联表数据

---

## 3. 公用基础设施

### 3.1 AptioSocketClient

TCP 客户端，连接 Aptio GUI 端口（默认 2055），发送命令并接收响应。

**协议**：ASCII 文本，`\r\n` 分隔，每条命令一个响应。

### 3.2 SrmStatusDecoder

解析 SRM 状态消息：

```
输入：\18^SRM^1^ON^0000^G^^\
解析：Mode=ON, Error=0000, Status=G
判断：ON + (G/Y) + (0000或允许错误码) → 就绪
```

### 3.3 WorkTimeChecker

工作时间判断：
- 星期：1=周一 .. 7=周日，逗号分隔
- 时间段：`HH:MM-HH:MM,threshold;`，支持跨天
- 多个时间段按顺序匹配，首次命中返回

### 3.4 ASTM 协议栈

完整 ASTM 协议实现：
- 帧控制：ENQ/ACK/NAK/STX/ETX/EOT/ETB
- 消息结构：H/P/O/C/R/L 记录类型
- 校验和：2 字符十六进制
- 帧构建与解析

### 3.5 统一日志模块

- 按模块分文件：`{Module}_{yyyy-MM-dd}.log`
- 错误汇总：`UniFlow_Error_{yyyy-MM-dd}.log`
- 按大小轮转（默认 10MB）
- 按天数保留（默认 30 天）

---

## 4. 功能开关

通过 `appsettings.json` 的 `Features` 节控制：

```json
"Features": {
  "DisposeSampleEnabled": true,
  "SrmExportEnabled": true,
  "WorkListCleanerEnabled": false,
  "DmsAutoOrderEnabled": false
}
```

所有模块默认关闭，按需启用。
