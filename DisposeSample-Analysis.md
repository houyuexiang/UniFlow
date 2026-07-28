# DisposeSample 项目分析报告

## 概述

- **开发语言**: Delphi 7 (Object Pascal)
- **数据库**: MySQL (通过 ZeosDBO 组件连接)
- **项目类型**: Windows 桌面应用程序(系统托盘常驻)
- **功能定位**: 自动化标本丢弃控制系统，与 Siemens Aptio 自动化流水线系统的 SRM (Sample Recovery Module / 标本冷藏存储模块) 对接

## 项目文件结构

| 文件 | 说明 |
|------|------|
| `DisposeSamples.dpr` | 项目入口文件 |
| `Main.pas` / `Main.dfm` | 主窗体单元，包含核心业务逻辑 |
| `DMUnt.pas` / `DMUnt.dfm` | 数据模块，数据库访问与业务数据操作 |
| `PubUnt.pas` | 公共工具单元：配置读取、日志、字符串处理等 |
| `DisposeSamples.ini` | 主配置文件 |
| `DisposeSamples-SH.ini` | 上海地区专用配置文件 |
| `DisposeSamples.cfg` | Delphi 编译器配置 |
| `Error-Handling.ini` | SRM 错误码参考表(8001~S0120) |
| `libmySQL.dll` | MySQL 客户端库 |
| `Readme.docx` | 使用说明文档 |

## 核心架构

### 主控流程 (Main.pas)

程序通过 `CtrlTimer` 定时器驱动状态机轮询，状态流转如下：

```
None ──(样本超阈值)──> Ready ──(发送STATUS检查)──> ACK
  ^                                                  │
  │                                                  v
  └───────────── DISPOSE <──(丢弃确认)──── disposed
```

**详细流程**:
1. **定时触发** → `CtrlTimerTimer` (可配置间隔，默认 3~6 秒)
2. **连接检查** → 如 `ClientSocket` 未连接则重连 Aptio 服务器(端口 2055)
3. **数据库心跳** → `MySql_Ping` 保持数据库连接
4. **工作时间判断** → `Check_Work_Time` 根据配置的日期和时间范围决定是否执行
5. **状态机**:
   - `None` → 检查 SRM 内样本数是否超过阈值，若是则从视图/存储过程获取待丢弃样本，写入 `sam_dispose_status` 表，状态置为 `Ready`
   - `Ready` → 发送 `STATUS-REQUEST 1` 命令查询 SRM 状态
   - `ACK` → SRM 返回正常状态 → 发送 `COMMENT S002^<barcode>\TRASH^S` 命令丢弃指定样本
   - 等待丢弃确认 → 检查样本位置是否已变为 `&3-{NodeID}-%`(IOM 输出位置)，确认丢弃完成

### 通信协议

通过 `TClientSocket` 与 Aptio 服务器进行 TCP Socket 通信(端口 2055):
- **发送**: `COMMENT S002^<barcode>\TRASH^S` — 丢弃指定条码的标本
- **发送**: `STATUS-REQUEST 1` — 查询 SRM 状态
- **接收**: `STATUS...` — SRM 状态返回，包含模式(ON/OFF)、错误码、状态(G/Y)
- **接收**: `ACK...` — 丢弃命令确认

### 数据库操作 (DMUnt.pas)

- **数据库**: MySQL, 库名 `flexlab`
- **连接参数**: 通过 INI 配置 IP、端口 3306，默认账号 root/root
- **核心表**:
  - `t_sample` — 标本主表，包含条码、位置、状态等
  - `sam_dispose_status` — 丢弃记录表，记录待丢弃/已丢弃/已发送状态
- **关键方法**:
  - `Check_SRM_SampleCount` — 统计 SRM 节点中状态为 'C'(冷藏中)的标本数
  - `Get_Dispose_sample` — 获取待丢弃标本列表(通过 SQL 视图或存储过程)
  - `Insert_Dispose_Record` — 将待丢弃标本插入丢弃记录表
  - `select_one_send_record` — 选取一条待发送的丢弃记录
  - `Update_Dispose_Sample_Status` — 更新丢弃状态(send/disposed/checkcount)
  - `check_t_Sample_Dispose_Status` — 确认标本是否已到达 IOM 输出位置

### 配置说明 (DisposeSamples.ini)

| 参数 | 说明 |
|------|------|
| `command_type` | 0=SQL视图, 1=存储过程 |
| `command_name` | 视图/存储过程名称 |
| `aptio_ip` | Aptio 服务器 IP |
| `srm_nodeid` | SRM 节点编号(如 09, 16) |
| `discard_run_date` | 运行日期(1=周一 ~ 7=周日) |
| `discard_time_range` | 运行时间段及对应阈值，格式: `HH:MM-HH:MM,threshold;...` |
| `loop_interval_time` | 轮询间隔(秒) |
| `max_wait_discard_count` | 最大等待丢弃确认次数 |
| `max_onetime_select_discard_count` | 单次最多选取丢弃数 |
| `allow_srm_error_code` | 允许的 SRM 错误码 |

### 位置编码规则

位置字符串格式: `&{type}-{NodeID}-{RackNo}-...`

- `&1-{NodeID}-%` — SRM 存储位置
- `&3-{NodeID}-%` — IOM(输入输出模块)位置，用于确认丢弃完成

## 业务逻辑总结

该系统自动监控 SRM 冰箱的标本存储量，当标本数量超过配置阈值时，在可配置的时间窗口内自动将最早进入的标本从 SRM 丢弃至 IOM 输出口，实现冰箱容量的自动化管理，避免标本积压。
