# AutoTestTools 项目分析

## 概述

AutoTestTools.zip 中包含 4 个子项目，全部为 .NET Framework Windows 应用/服务，属于实验室自动化测试与 DMS 辅助工具集：

| 子项目 | 类型 | 功能 |
|--------|------|------|
| **AutoTestTools** | WinForms | 自动化测试主工具：ASTM通信、仪器驱动、CSV处理、SQLite本地库 |
| **DMSAutoOrder** | WinForms | DMS 自动工单：ASTM下单、PitStop监控、标本自动删除、状态修正 |
| **DMSAssistInstaller** | WinForms | DMS 辅助工具的安装配置程序 |
| **Service_DMSAssist** | Windows Service | DMS 辅助服务的后台版本（封装 DMSAutoOrder 的核心逻辑） |

---

## 功能点清单

### 1. AutoTestTools（自动化测试主工具）

| # | 功能点 | 文件 | 说明 |
|---|--------|------|------|
| 1.1 | ASTM 协议通信 | `ClassLib/Community/ASTM.cs` | 完整的 ASTM 协议实现（ENQ/ACK/EOT/STX/ETX/NAK），支持 ASCII 帧传输 |
| 1.2 | TCP Socket 客户端 | `ClassLib/Community/SocketClient.cs` | TCP 客户端封装 |
| 1.3 | TCP Socket 服务端 | `ClassLib/Community/SocketServer.cs` | TCP 服务端封装 |
| 1.4 | TCP 连接管理 | `ClassLib/Community/TCPConnect.cs` | 连接生命周期管理 |
| 1.5 | 仪器驱动接口 | `ClassLib/Interface/InstrumentDriver.cs` | 抽象仪器驱动接口 |
| 1.6 | Advia 2400 驱动 | `ClassLib/InstrumentDrivers/Advia2400.cs` | Siemens Advia 2400 分析仪驱动 |
| 1.7 | Centaur XP 驱动 | `ClassLib/InstrumentDrivers/CentaurXP.cs` | Siemens Centaur XP 化学发光分析仪驱动（422行完整实现） |
| 1.8 | LIS 工单生成 | `ClassLib/InstrumentDrivers/LIS_ORDER.cs` | LIS 下单格式生成 |
| 1.9 | LIS 结果解析 | `ClassLib/InstrumentDrivers/LIS_RESULT.cs` | LIS 结果数据解析 |
| 1.10 | SQLite 数据库 | `ClassLib/DataBaseSQLite.cs` | SQLite 本地数据存储 |
| 1.11 | 全局值工具 | `ClassLib/GlobalValue.cs` | 全局常量（ACK/ENQ/CR/LF 等） |
| 1.12 | CSV 处理 | `Tools/CSVProcess.cs` | CSV 文件导入导出 |
| 1.13 | 注册表工具 | `Tools/RegeditSet.cs` | Windows 注册表读写 |
| 1.14 | 通信参数配置 | `Windows/CommParmSet.cs` | 串口/TCP 通信参数配置界面 |
| 1.15 | 通信测试面板 | `Windows/CommTestControl.cs` | 通信交互测试界面 |
| 1.16 | 仪器管理列表 | `Windows/InstrumentList.cs` | 仪器列表管理界面 |
| 1.17 | 汇总统计 | `Windows/Sum.cs` | 数据汇总统计界面 |
| 1.18 | 测试项目选择 | `AutoValTestSelect/` | 自动化验证测试选择界面 |
| 1.19 | 数据库创建 | `Database/DatabaseCreate.cs` | 本地 SQLite 库结构创建 |
| 1.20 | MySQL 数据库 | `Database/DatabaseMYSQL.cs` | MySQL 数据库访问封装 |
| 1.21 | 参考数据 CSV | `z/*.csv` | 编码系统、正常值范围、测试项目等参考数据 |

### 2. DMSAutoOrder + Service_DMSAssist（DMS 自动工单系统）

| # | 功能点 | 说明 |
|---|--------|------|
| 2.1 | ASTM 下单 | 通过 ASTM 协议向 DMS 发送工单（Orders/Reqtest） |
| 2.2 | 无工单标本处理 | 自动识别"UNKNOWN"患者工单，生成 PARK 测试 |
| 2.3 | PitStop 表监控 | 监控 MySQL 中 pitstop 表的 stuck 记录，自动清理（超过1分钟） |
| 2.4 | 错误状态结果修正 | 清理 E/R/P 状态的结果记录 |
| 2.5 | 结果状态批量修正 | 支持 3 种模式：不修改 / 重发(HostFlag归零) / 置为F(Final) |
| 2.6 | 标志位忽略处理 | 根据配置的 IgnoreFlagList 自动 Val 特定标志的结果 |
| 2.7 | 测试触发标本删除 | 收到特定测试结果后，超时自动删除 DMS 中的标本 |
| 2.8 | 标本自动清理 | 按表列表批量删除标本相关数据（orders/reqtest/reqtube等 30+ 表） |
| 2.9 | 取消消息发送到 Aptio | 通过 Socket 向 Aptio 发送取消命令 |
| 2.10 | INI 文件配置管理 | 所有配置通过 INI 文件管理 |

### 3. 与已有项目的功能交集

| 已有项目 | 重叠功能 |
|---------|---------|
| **Service_AptioAutoProcess** | Socket 通信（2055端口）、STATUS-REQUEST/COMMENT 命令 |
| **DisposeSample** | COMMENT S002 丢弃命令 |
| **ImmuliteWorkListCleaner** | ASTM 协议、Centralink 通信 |
| **UniFlow** | SOckect 通信、MySQL 数据库 |

---

## 合并到 UniFlow 的功能点筛选

按优先级和模块划分：

### Common（公用基础层）— 可直接复用

| 功能 | 来源 | 当前状态 | 建议 |
|------|------|---------|------|
| ASTM 协议 | AutoTestTools/DMSAutoOrder | 不存在 | **新增** `Common/Services/ASTM.cs`，标准 ASTM 帧传输 |
| TCP Server | AutoTestTools | 不存在 | **新增** `Common/Services/TcpServerHost.cs`，WCF替代 |
| 仪器驱动接口 | AutoTestTools | 不存在 | **新增** `Common/Services/InstrumentDriver.cs` |
| 全局常量 | AutoTestTools | 部分存在 | **补充** 到已有 GlobalValue |

### DisposeSample（标本丢弃模块）— 已有，可增强

| 功能 | 来源 | 当前状态 | 建议 |
|------|------|---------|------|
| 基础丢弃 | DisposeSample/Delphi | ✅ 已有 | 保持 |
| 递送(Deliver) | Service_AptioAutoProcess | 不存在 | **新增** `COMMENT S002^{sid}\DELIVER^S`，功能开关控制 |
| 优先级(STAT) | Service_AptioAutoProcess | 不存在 | **新增** `COMMENT S010^{sid}^S`，功能开关控制 |
| 取消测试(GUI消息) | Service_AptioAutoProcess | 不存在 | **新增** ORDER/S004/CANCEL 命令 |

### SrmExport（导出模块）— 已有

| 功能 | 来源 | 当前状态 | 建议 |
|------|------|---------|------|
| 前一天丢弃记录导出 | SRM_ExportDisposeSample | ✅ 已有 | 保持 |

### 新增业务模块

| 模块 | 功能点 | 来源 | 建议优先级 |
|------|--------|------|-----------|
| **WorkListCleaner** | Immulite 工作列表清理 | ImmuliteWorkListCleaner | 中 |
| | MS Access 数据库操作 | 同上 | |
| | Centralink LAS 标志发送(ASTM) | 同上 | |
| | DMS 数据库结果回传 | 同上 | |
| **DMSAutoOrder** | ASTM 自动下单 | DMSAutoOrder | 高 |
| | PitStop 监控 | 同上 | |
| | 标本状态修正 | 同上 | |
| | 测试触发自动清理 | 同上 | |
| **InstrumentDriver** | Advia 2400 / Centaur XP 驱动 | AutoTestTools | 低（需实际仪器对接） |
| **LIS Connector** | 结果接收工单生成 | AutoTestTools | 中 |
| **CentralinkAnalyzer** | Centralink 结果分析 | Service_AptioAutoProcess | 中 |

---

## UniFlow 整合路线图

```
UniFlow/
├── Common/                          ← 已有 + 新增
│   ├── Models/
│   ├── Services/
│   │   ├── AptioSocketClient        ✅ 已有
│   │   ├── SrmStatusDecoder         ✅ 已有
│   │   ├── WorkTimeChecker          ✅ 已有
│   │   ├── ASTM.cs                  🔜 新增（ASTM 协议）
│   │   ├── TcpServer.cs             🔜 新增（TCP 服务端）
│   │   └── InstrumentDriver.cs      🔜 新增（仪器驱动接口）
│   └── ...
├── DisposeSample/                   ← 已有 + 增强
│   ├── Services/
│   │   ├── DisposeDatabaseService   ✅ 已有
│   │   └── DisposeCommandService    🔜 新增（丢弃/递送/优先级命令封装）
│   └── Workers/
│       └── DisposeSampleWorker      ✅ 已有（增加递送/优先级状态）
├── SrmExport/                       ← 已有
├── WorkListCleaner/                 🔜 新增模块
│   ├── Services/
│   │   ├── AccessDatabaseService    🔜 MS Access 操作
│   │   ├── DmsDatabaseService       🔜 DMS MySQL 操作
│   │   └── CentralinkService        🔜 Centralink ASTM 通信
│   └── Workers/
│       └── WorkListCleanerWorker    🔜 定时清理工作列表
├── DMSAutoOrder/                    🔜 新增模块
│   ├── Services/
│   │   ├── DmsOrderService          🔜 ASTM 下单
│   │   ├── PitStopMonitorService    🔜 PitStop 监控
│   │   └── SampleCleanupService     🔜 标本清理
│   └── Workers/
│       └── DmsAutoOrderWorker       🔜 定时工单处理
├── Centralink Analyzer/             🔜 新增模块（迁移现有代码）
└── Program.cs
```
