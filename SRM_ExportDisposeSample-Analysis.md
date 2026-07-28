# SRM_ExportDisposeSample 项目分析报告

## 概述

- **开发语言**: Delphi 7 (Object Pascal)
- **数据库**: MySQL (通过 ZeosDBO 组件连接)
- **项目类型**: Windows 桌面应用程序(系统托盘常驻)
- **功能定位**: 标本丢弃记录导出工具，每天自动导出前一天通过 SRM 丢弃的标本记录到 TXT 文件

## 项目文件结构

| 文件 | 说明 |
|------|------|
| `SRM_ExportDisposeSample.dpr` | 项目入口文件 |
| `Unt_DM.pas` / `Unt_DM.dfm` | 数据模块(唯一可用源码单元) |
| `Unt_Main.pas` | 主窗体单元(源码缺失，仅存在于 .dpr 引用中) |
| `Unt_PubFun.pas` | 公共函数单元(源码缺失) |
| `SRM_ExportDisposeSample.ini` | 配置文件 |
| `SRM_ExportDisposeSample.cfg` | Delphi 编译器配置 |
| `libmySQL.dll` | MySQL 客户端库 |
| `Doc.docx` | 使用说明文档 |

> **注**: `Unt_Main.pas` 和 `Unt_PubFun.pas` 源码未包含在压缩包中，仅有编译后的 `.exe`。以下分析基于现有的 `Unt_DM.pas`、`.dpr`、INI 配置及使用文档。

## 核心架构

### 总体功能

程序每天在指定时间(如 08:30)自动执行一次，从 Aptio 的 MySQL 数据库中查询前一天被 SRM 丢弃的标本记录，导出为 TXT 文件保存。

### 数据模块 (Unt_DM.pas)

**配置结构体 `TConfig`**:
- `IP` — Aptio 服务器地址
- `Port` — MySQL 端口(默认 3306)
- `SRM_Nodeid` — SRM 节点编号
- `Export_Time` — 每日导出时间
- `UserName` / `Password` — 数据库认证
- `DataBaseName` — 数据库名称(默认 flexlab)

**核心方法**:
- `ReadConfig` — 从 `SRM_ExportDisposeSample.ini` 读取配置
- `ConnectToMysql` — 连接 MySQL 数据库
- `MySql_Ping` — 保持数据库连接心跳
- `Get_Dispose_Sample` — 执行查询并返回结果

**查询 SQL**:
```sql
SELECT sample_id COLLATE latin1_swedish_ci AS barcode,
       t_location COLLATE latin1_swedish_ci AS location,
       update_time
FROM t_sample
WHERE update_time >= DATE_FORMAT(curdate()-1, '%Y%m%d')
  AND update_time < DATE_FORMAT(curdate(), '%Y%m%d')
  AND t_location LIKE '&3-{NodeID}-%'
```

查询条件说明:
- 查询前一天的数据 (`curdate()-1` 到 `curdate()`)
- 位置编码以 `&3-{NodeID}` 开头 — 即 IOM (输入输出模块) 的输出位置，表示标本已被 SRM 丢弃至输出口

### 配置文件 (SRM_ExportDisposeSample.ini)

| 参数 | 说明 |
|------|------|
| `SRM_Nodeid` | SRM 节点 ID(如 04) |
| `Export_Time` | 每天自动导出时间(如 08:30) |
| `IP` | Aptio 服务器 IP(默认 127.0.0.1) |
| `Port` | MySQL 端口(默认 3306) |
| `UserName` | 数据库用户名(默认 root) |
| `Password` | 数据库密码(默认 root) |
| `DataBaseName` | 数据库名(默认 flexlab) |

### 输出文件

- **保存位置**: exe 同目录下的 `DisposeFile` 文件夹
- **文件命名**: `Dispose_YYYYMMDDHHMMSS_DisposeCounts.txt`
- **日志位置**: exe 同目录下的 `Logs` 文件夹

## 与 DisposeSample 的关系

SRM_ExportDisposeSample 是 DisposeSample 的配套工具:

| 项目 | 角色 | 功能 |
|------|------|------|
| **DisposeSample** | 执行者 | 主动连接 SRM，发送丢弃命令，控制标本丢弃过程 |
| **SRM_ExportDisposeSample** | 记录者 | 被动查询数据库，导出前一天的丢弃记录用于审计分析 |

两者协同工作: DisposeSample 负责丢，SRM_ExportDisposeSample 负责记。

## 业务逻辑总结

该项目是一个轻量级的数据导出工具，每天定时从 Aptio 数据库查询已被 SRM 丢弃至 IOM 输出口的标本记录，生成文本文件用于后续的丢弃记录审计和分析。程序以系统托盘方式运行，无需人工干预。
