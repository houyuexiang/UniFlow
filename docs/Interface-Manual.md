# UniFlow 接口手册

## 1. 外部通信接口

### 1.1 Aptio Socket 协议

UniFlow 通过 TCP Socket 与 Siemens Aptio 自动化流水线系统通信。

#### 连接参数

| 参数 | 值 |
|------|-----|
| 协议 | TCP |
| 端口 | 2055 |
| 编码 | ASCII |
| 消息终止 | `\r\n` (CR+LF) |

#### 命令列表

##### STATUS-REQUEST — 查询 SRM 状态

```
发送: STATUS-REQUEST 1\r\n
响应: STATUS\18^SRM^1^{MODE}^{ERROR}^{STATUS}^{...}\
```

**响应解析**：

| 字段索引 | 名称 | 说明 | 示例 |
|---------|------|------|------|
| [3] | Mode | 运行模式 | ON / OFF |
| [4] | Error | 错误码 | 0000 / 0F0A / E001 |
| [5] | Status | 状态指示 | G(Green) / Y(Yellow) / R(Red) |

**状态判断逻辑**：
- `Mode == "ON"` 且 `Status ∈ {"G", "Y"}` 且 `Error ∈ {"0000"} ∪ allow_srm_error_code` → 就绪

##### COMMENT — 标本操作命令

```
格式: COMMENT S{cmd}^{barcode}\{action}^S\r\n
响应: ACK...\r\n
```

| Action | 命令码 | 说明 |
|--------|--------|------|
| TRASH | `COMMENT S002^{barcode}\TRASH^S` | 丢弃标本至废料口 |
| DELIVER | `COMMENT S002^{barcode}\DELIVER^S` | 递送标本至指定位置 |
| STAT | `COMMENT S010^{barcode}^S` | 标记为 STAT（优先处理） |

##### ORDER — 测试取消

```
格式: ORDER {barcode}||||||||||||C|||||{test}\r\n
说明: 通过 Aptio GUI 消息取消指定测试
```

##### ACK — 确认响应

所有成功执行的命令返回以 `ACK` 开头的响应。

---

### 1.2 ASTM 协议

用于与实验室分析仪和中间件（如 Centralink）通信。

#### 控制字符

| 字符 | 十六进制 | 说明 |
|------|---------|------|
| ENQ | 0x05 | 查询（请求通信） |
| ACK | 0x06 | 确认 |
| NAK | 0x15 | 否认 |
| STX | 0x02 | 帧起始 |
| ETX | 0x03 | 帧结束 |
| ETB | 0x17 | 块结束（多帧消息） |
| EOT | 0x04 | 传输结束 |
| CR | 0x0D | 回车 |
| LF | 0x0A | 换行 |
| FS | 0x1C | 字段分隔 |
| GS | 0x1D | 记录分隔 |

#### 帧格式

```
STX + 消息体 + ETX + 校验和(2字符Hex) + CR + LF
```

#### 记录类型

| 类型 | 说明 | 示例 |
|------|------|------|
| H | 消息头 | `H|\^&||||||||||P|1|20260727120000` |
| P | 患者信息 | `P|1|SAMPLE001||||||||||||||||||F` |
| O | 测试工单 | `O|1|SAMPLE001||^^^TEST|R||20260727120000||||N||||S||U||||||||O` |
| C | 注释 | `C|1|I|S004^SAMPLE001\NS\DMS_Immulite_1-TSH\No Sample\20260727120000|S` |
| R | 结果 | `R|1|^^^TEST|7.5||N||F||20260727120000` |
| L | 终止 | `L|1|N` |

#### 校验和计算

```
对 STX 和 ETX 之间的所有字节（不含 STX/ETX）求和，取模 256。
checksum = sum(bytes) % 256
转为 2 字符大写十六进制。
```

---

## 2. 内部编程接口

### 2.1 IWorkListCleaner — 工作列表清理器接口

```csharp
namespace UniFlow.WorkListCleaner.Services;

public interface IWorkListCleaner
{
    /// <summary>清理器名称（用于日志标识）</summary>
    string Name { get; }

    /// <summary>执行一次清理任务</summary>
    Task CleanAsync(CancellationToken ct);
}
```

**实现说明**：
- 任何类实现此接口后，在 `Program.cs` 中注册为 Singleton
- `WorkListCleanerWorker` 自动发现并执行所有注册的清理器
- 每个清理器独立运行，互不干扰

**注册示例**：
```csharp
services.AddSingleton<IWorkListCleaner, ImmuliteCleaner>();
services.AddSingleton<IWorkListCleaner, MyCustomCleaner>();
```

### 2.2 IAptioSocketClient — Socket 客户端接口

```csharp
namespace UniFlow.Common.Services;

public interface IAptioSocketClient
{
    string Host { get; set; }
    int Port { get; set; }
    bool Connected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task<string?> SendAndReceiveAsync(string command, CancellationToken ct = default);
    void Disconnect();
}
```

### 2.3 IDisposeDatabaseService — 丢弃数据库服务接口

```csharp
namespace UniFlow.DisposeSample.Services;

public interface IDisposeDatabaseService
{
    Task<bool> PingAsync();
    Task<int> CheckSrmSampleCountAsync(string nodeId);
    Task<List<SampleRecord>> GetDisposeSamplesAsync(int cmdType, string cmdName, int maxCount);
    Task<int> InsertDisposeRecordsAsync(int needCount, List<SampleRecord> samples);
    Task<DisposeStatus?> SelectOneSendRecordAsync();
    Task UpdateStatusAsync(string barcode, string field);
    Task<bool> CheckDisposedAsync(string barcode, string nodeId);
    Task<int> GetCheckCountAsync(string barcode);
    Task DeleteUnsendAsync();
    Task DeleteHistoryAsync();
    Task<List<SampleRecord>> GetDeliverRecordsAsync(string deliverTestName, int maxCount);
    Task<List<SampleRecord>> GetPriorityRecordsAsync(string priorityTestName, int maxCount);
    Task SetPriorityDoneAsync(string barcode);
}
```

### 2.4 其他数据访问接口

| 接口 | 所在模块 | 方法数 |
|------|---------|--------|
| IExportDatabaseService | SrmExport | 2 |
| IExportFileService | SrmExport | 2 |

---

## 3. 数据库接口

### 3.1 FlexLab 数据库（Aptio）

所有 DisposeSample 操作基于 `flexlab` 数据库。

#### t_sample — 标本主表

| 字段 | 类型 | 说明 | 示例 |
|------|------|------|------|
| sample_id | varchar | 标本条码 | SAMPLE001 |
| patient | varchar | 患者 ID | P001 |
| s_type | varchar | 标本类型 | S(血清) |
| t_location | varchar | 位置编码 | &1-09-000001-02-03-05 |
| t_status | varchar | 状态 | C(冷藏) |
| test | text | 测试列表 | ...;test1:desc;... |
| update_time | datetime | 更新时间 | 20260726120000 |
| res_1, res_2 | varchar | 预留字段 | |

**位置编码规则**：

| 前缀 | 说明 |
|------|------|
| `&1-{NodeID}-%` | SRM 存储位置（冰箱内） |
| `&3-{NodeID}-%` | IOM 输出位置（已丢弃） |

**关键查询**：
```sql
-- 统计 SRM 中标本数
SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE '&1-09-%';

-- 查询已丢弃标本（前一天）
SELECT sample_id FROM t_sample
WHERE t_location LIKE '&3-09-%'
  AND update_time >= DATE_FORMAT(curdate()-1,'%Y%m%d')
  AND update_time < DATE_FORMAT(curdate(),'%Y%m%d');
```

#### sam_dispose_status — 丢弃记录表

| 字段 | 类型 | 说明 |
|------|------|------|
| barcode | varchar | 标本条码（PK） |
| patient | varchar | 患者 ID |
| stype | varchar | 标本类型 |
| location | varchar | 原始位置 |
| rack | varchar | 架号 |
| send | int | 是否已发送(0/1) |
| disposed | int | 是否已确认丢弃(0/1) |
| checkcount | int | 检查次数 |
| send_time | datetime | 发送时间 |
| disposed_time | datetime | 确认丢弃时间 |

### 3.2 DMS 数据库

用于 DMSAutoOrder 模块。

#### 核心表

| 表名 | 说明 |
|------|------|
| reqtest | 测试请求 |
| reqtestresult | 测试结果 |
| reqtube | 标本管 |
| orders | 工单 |
| orders_details | 工单明细 |
| anagpatient | 患者信息 |
| instrument | 仪器 |
| testinstrument | 测试与仪器映射 |
| pitstop% | PitStop 监控表（动态） |

#### 常用状态标志

| 字段 | 值 | 说明 |
|------|-----|------|
| flgstatus | H | 持有（Hold） |
| | V | 有效（Valid） |
| | F | 最终（Final） |
| | E/R/P | 错误/拒绝/进行中 |
| flgtohost | 0/1 | 是否已发送到 Host |

---

## 4. 配置接口

配置通过 `appsettings.json` 文件管理，支持的配置节见《部署配置手册》。

### 配置热加载

UniFlow 使用 `Microsoft.Extensions.Configuration` 的 `reloadOnChange: true`，修改配置文件后**无需重启服务**即可生效（部分模块受限于 BackgroundService 循环周期，最长等待一个 `LoopIntervalSeconds` 周期）。

### 环境变量覆盖

所有配置项可通过环境变量覆盖，层级用 `__`（双下划线）分隔：

```bash
# 覆盖数据库密码
export UniFlow__Database__Password=newpassword

# 覆盖 Aptio IP
export UniFlow__Aptio__Ip=192.168.1.100

# 覆盖功能开关
export UniFlow__Features__DmsAutoOrderEnabled=true
```
