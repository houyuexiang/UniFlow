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
响应: 无（Dream 不回复；写成功 = 已投递）
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

##### 确认口径 — 没有 ACK

**Host LIS 发出的 `COMMENT S002`（TRASH / DELIVER）与 `ORDER` 没有 ACK 响应。**

- 依据厂商手册 DCAU-H00.816.12.01.02.pdf 第 498 页 §8.59：`ACK` / `NAK` 是 **Dream→GUI 的通知**，
  只对应 `ADVANCED-CONFIG-CHANGE`、`CLEAR-TUBES`、`COMMENT I021~I024` 这 **6 条 GUI 命令**，
  与 Host LIS 的 COMMENT / ORDER 无关；§9.3：`COMMENT S002` 没有任何回复/ACK 定义。
- 因此成功判定口径是「**写成功 = 已投递**」：socket 写入并 Flush 成功即视为投递成功
  （传输层返回标记 `OK:SENT`），**不要**用「回复是否以 ACK 开头」判断成败。
- **丢弃是否真的成功**由数据库判定：`t_sample.t_location` 变为 `&3-{node}-%` 即视为已丢弃，
  等待 `MaxWaitDiscardCount` 个循环确认；另有每 60 秒一次的补查
  （复查 `send=1 AND disposed=0`，命中即置 `disposed=1`）。
- 唯一期待回复的命令是 `STATUS-REQUEST 1`（Dream 回 STATUS 状态行），用于探测连接是否可用。

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
    // 业务命令（COMMENT / ORDER）写成功即返回 "OK:SENT"（已投递，无 ACK）；
    // STATUS-REQUEST 返回 STATUS 回复行或 null；1h 超时返回 null；写失败抛异常。
    Task<string?> SendAsync(string command, CancellationToken ct = default);
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
    // 补查（轮转窗口）：只查 ID > afterId 的一批；返回 (命中数, 本批最大 ID)，
    // LastId=0 表示本轮查完 → 调用方把游标归零，下一轮从头开始
    Task<(int Hits, long LastId)> RecheckPendingDisposalsAsync(int max = 200, long afterId = 0);
    // 重发计数字段（sam_dispose_status.resend_count）是否就绪：建表/迁移成功 → true；
    // 旧表 ALTER 失败 → false（此时依赖 resend_count 的查询都会失败，Worker 跳过重发/失败标记）
    bool ResendColumnReady { get; }

    // ===== 队列查询 / 重发 / 失败处置（Web 管理页调用）=====
    Task<int> CountPendingSendAsync();                                  // send=0 的数量（待发）
    Task<int> CountPendingAsync();                                       // send=0 或 (send=1 且 disposed=0)
    Task<int> CountPendingConfirmAsync();                                // send=1 且 disposed=0（待确认）
    Task<int> CountFailedAsync();                                        // send=3 且 disposed=0（失败）
    Task<List<DisposeStatus>> GetPendingRecordsAsync(int minResendCount, int limit);
    Task<List<DisposeStatus>> GetResendCandidatesAsync(string cutoff, int maxResendCount, int limit);
    Task<List<DisposeStatus>> GetExhaustedCandidatesAsync(int maxResendCount, int limit);
    Task MarkResendAsync(string barcode);                                // send=1, send_time=now, checkcount=0, resend_count+1（兼容保留；Worker 不再调用）
    Task<int> RequeueForResendAsync(IReadOnlyCollection<string> barcodes); // 重排队：send=1 & disposed=0 → send=0（不改 send_time、不增 resend_count）
    Task<int> MarkFailedAsync(IReadOnlyCollection<string> barcodes);    // send=3（失败）
    Task<int> ResetRecordsAsync(IReadOnlyCollection<string> barcodes);   // send=0, resend_count=0, checkcount=0（重新入队）
    Task<int> DeleteRecordsAsync(IReadOnlyCollection<string> barcodes);   // 删除记录
    Task<bool> RecheckRecordAsync(string barcode);                      // 单条复查 → 命中则置 disposed=1
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

所有配置项可通过环境变量覆盖，层级用 `__`（双下划线）分隔，**无 `UniFlow` 前缀**
（应用使用 `WebApplication.CreateBuilder` 的默认环境变量源，未调用 `AddEnvironmentVariables("UniFlow")`，
带前缀会变成死变量）：

```bash
# 覆盖数据库密码（配置根 Aptio:Database）
export Aptio__Database__Password=newpassword

# 覆盖 Aptio IP
export Aptio__Ip=192.168.1.100

# 覆盖功能开关（Features 分组结构）
export Features__Aptio__DisposeSample=true
export Features__Dms__StatusCorrection=true
```

---

## 5. Web 管理 API（管理鉴权）

Web 管理控制台的 HTTP 接口统一在 `/api` 前缀下（默认 `http://<host>:5100`）。
**破坏性动作**（重启 / 改配置 / 升级 / 清错误 / 删队列 / 下载配置）需要**管理鉴权**。

### 5.1 鉴权流程（每次动作一次，无会话 token）

```
① GET  /api/auth/challenge        → { "nonce": "<64 hex>", "expiresInSeconds": 60 }   （免鉴权）
② 前端计算 hash = SHA-256(密码 + nonce)  → 十六进制小写（64 字符，纯 JS 实现）
③ 请求带两个头：
      X-Admin-Nonce: <nonce>
      X-Admin-Hash:  <hash>
```

后端校验：nonce 存在且未过期（60 秒、一次性、仅存内存）→ 重算 `SHA256.HashData(UTF8(密码 + nonce))` → **常量时间**比较。
**无论成功失败都消费掉该 nonce**（重放必然失败）；密码与哈希**不写日志**。

| 情况 | 响应 |
|------|------|
| 管理密码未配置（`WebAdmin:AdminPassword` 为空） | **403** `{"success":false,"error":"Admin password not configured"}` |
| 头缺失 / nonce 过期 / 哈希不匹配 | **401** `{"success":false,"error":"Admin authentication failed"}` |

> `WebAdmin:AdminPassword` 默认 `Advia1`，**热生效**（后台改 appsettings.json 即刻生效）。
> 页面不显示、不可改；`PUT /api/config`、`PUT /api/config/batch` 写该路径一律 400。
> HTTP 明文环境的局限（哈希不防 MITM、上 HTTPS/限网段的建议）见《配置参考》`WebAdmin` 章。

### 5.2 需要鉴权的端点

| 方法 | 路径 | 说明 |
|------|------|------|
| PUT | `/api/config` | 单条保存配置 |
| PUT | `/api/config/batch` | 多字段一次保存（一次请求、一次鉴权） |
| GET | `/api/config/download` | 下载 appsettings.json **原文**（含明文密码，鉴权后） |
| POST | `/api/update/upload` | 上传升级包 |
| POST | `/api/update/apply` | 应用升级 |
| POST | `/api/restart` | 内部重启（业务宿主重建） |
| POST | `/api/restart/process` | 进程级重启（Exit(1)，由服务管理器拉起） |
| DELETE | `/api/errors` | 清空错误记录 |
| POST | `/api/dispose/pending/reset` | 重排队 |
| POST | `/api/dispose/pending/delete` | 删除待处置记录 |

### 5.3 免鉴权的端点

`GET /api/health`、`GET /api/health/history`、`GET /api/errors`、`GET /api/errors/summary`、`GET /api/logs/*`、
`GET /api/config`（**打码**：所有 `Password`/`Pwd` 键 → `"***"`）、`GET /api/dispose/pending`、
`POST /api/dispose/pending/recheck`、`POST /api/config/test-db`、`GET /api/auth/challenge`、
`GET /api/version`、`GET /api/restart/status`、`GET /api/update/status`。

### 5.4 新增接口

**GET /api/auth/challenge**（免鉴权）

```json
{ "nonce": "6f1c…（64 位十六进制小写）", "expiresInSeconds": 60 }
```

**PUT /api/config/batch**（需鉴权）

```jsonc
// 请求
{ "changes": [ { "path": "Aptio:Ip", "value": "192.168.1.10" },
               { "path": "Aptio:Database:Password", "value": "***" } ] }

// 响应（全部合法才写盘；任一非法 → 400 且一个字段都不写）
{ "success": true, "count": 1, "skipped": 1,
  "restart": { "level": "InnerRestart", "message": "Saved. Requires business-worker restart…" } }
```

- 密码类路径的值为 `"***"` 或空串 → **保持现有值不变**（计入 `skipped`，不计入 `count`）。
- `WebAdmin:AdminPassword` 不可经 API 写入 → 400。

**POST /api/config/test-db?target=aptio|dms**（免鉴权）

```json
{ "success": true, "target": "aptio", "elapsedMs": 42, "message": "Connection succeeded" }
```

- **只测已保存配置**（`Aptio:Database:*` / `DMS:Db*`），不读请求体、不改任何配置；
- 5 秒连接超时（`OpenAsync` + `PingAsync`）；
- 失败时 `message` 含错误原因但**不含密码**（密码替换为 `***`）。

**GET /api/config/download**（需鉴权）

- 鉴权通过 → 返回 **appsettings.json 完整原文**（含所有明文密码）；
- 对照：`GET /api/config`（免鉴权）返回**打码**后的配置。
