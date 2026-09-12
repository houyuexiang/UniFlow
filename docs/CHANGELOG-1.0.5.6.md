# UniFlow v1.0.5.6 改动记录

- 日期：2026-09-10
- 版本：1.0.5.5 → **1.0.5.6**
- 部署包：`uniflow-1.0.5.6-linux-x64.tar.gz` / `uniflow-1.0.5.6-win-x64.zip`
- 概述：测试触发任务重构为「生产者-消费者」架构、Aptio 通信改为队列模式、修复服务崩溃等缺陷、配置结构分组化。

---

## 一、测试触发任务重构（生产者-消费者）

原先 Delivery / Priority / TestNameDispose 各自扫描数据库、各自连接 Aptio，存在重复扫描、并发访问共享 socket、扩展需改多处的问题。

### 新增
- **`AptioBatchScannerWorker`（生产者）**：定时（`Aptio.BatchScan.LoopIntervalSeconds`）一次扫描 `t_sample`：
  - SQL 初筛：`CONCAT_WS('^', test_1..test_80) LIKE '%测试名%'`（各功能测试名并集，参数化）
  - 内存精确匹配：按 `test` 列第 5 段**完全匹配**（对齐原始 `test.Split(';')[4]`），跳过 `7` 开头的已处理列
  - 按功能开关决定是否推送，未开启的功能不推送
- **`AptioTaskRouter`（注册中心）**：可插拔，新增任务类型**只需新增 Worker 并注册，扫描器零改动**
  - `Register(name, testName, matcher, queue, enabled)`
  - **精确去重**：样本只要仍出现在扫描结果中（= 仍待处理）就视为「已安排」，不重复推送；样本消失（Aptio 已处理）才清理记录；保留 1 小时兜底重试期
- **`DeliveryFileWorker`**：文件递送从 Delivery 中分离为**独立功能**（独立开关 `Features.Aptio.DeliveryFile`）

### 改造
- `PriorityWorker` / `DeliveryWorker` / `TestNameDisposeWorker` 改为**消费者**：自持 `Channel`，构造函数向 Router 注册委托，从队列阻塞读取（无轮询）
- `SampleRecord` 增加 `TestName` 字段
- `IDisposeDatabaseService.GetAllScanableSamplesAsync`：一次查询替代原三个独立查询

---

## 二、Aptio Socket 通信改为队列模式

### `AptioSocketClient` 重写
- 所有命令通过 `Channel` 入队，**单一消费循环**统一处理连接、重连与收发（天然串行，无需锁竞争）
- **预连接**：服务启动即连接
- **梯度重试**：连接失败 `3s × 10 次 → 30s × 10 次 → 之后 60s`，连接成功后重置
- **命令队列保留 1 小时**：连接中断期间命令排队等待，恢复后继续发送
- `Host` / `Port` 统一从 `IOptions<AptioConfig>` 读取（移除各 Worker/Service 的冗余设置）
- 接口方法 `SendAndReceiveAsync` → `SendAsync`

---

## 三、配置结构分组化

### Features（功能开关）
```
Features.Aptio.{DisposeSample, SrmExport, Delivery, DeliveryFile, Priority, TestNameDispose}
Features.Immulite.WorkListCleaner
Features.Dms.{PitStopMonitor, StatusCorrection, SampleCleanup}
```

### Aptio 段
```
Aptio.{Ip, Port, SrmNodeIds, Database}
Aptio.DisposeSample.*        Aptio.SrmExport.*
Aptio.Delivery.TestName      Aptio.DeliveryFile.{DeliveryListFilePath, LoopIntervalSeconds}
Aptio.Priority.TestName      Aptio.TestNameDispose.DisposeTestName
Aptio.BatchScan.{LoopIntervalSeconds, MaxOnetimeScanCount}   ← 新增
```

### DMS 段
```
DMS.{Enabled, DbHost, DbPort, DbUser, DbPassword, DbName, LoopIntervalSeconds}
DMS.PitStop.{EnableMonitor, TimeoutMinutes}
DMS.StatusCorrection.{AutoModifyTestStatus, IgnoreFlagList}
DMS.SampleCleanup.{TestTriggerSampleDeletion, DeleteTableList, SendCancelMessageToAptio}
```

### 旧配置迁移映射（生产配置已改写）
| 旧字段 | 新字段 |
|--------|--------|
| `Features.AptioAutoProcess` | `Features.Aptio.DisposeSample` + `Aptio.SrmExport` |
| `Features.DmsAutoOrder` | `Features.Dms.{StatusCorrection, SampleCleanup, PitStopMonitor}` |
| `AptioAutoProcess.Dispose.*` | `Aptio.DisposeSample.*` |
| `AptioAutoProcess.Deliver.TestName` | `Aptio.Delivery.TestName` |
| `AptioAutoProcess.Deliver.DeliveryListFilePath` | `Aptio.DeliveryFile.DeliveryListFilePath` |
| `AptioAutoProcess.Priority.TestName` | `Aptio.Priority.TestName` |
| `AptioAutoProcess.Dispose.DisposeTestName` | `Aptio.TestNameDispose.DisposeTestName` |
| `AptioAutoProcess.Export.*` | `Aptio.SrmExport.*` |
| `DMS.EnablePitStopMonitor` | `DMS.PitStop.EnableMonitor` |
| `DMS.PitStopTimeoutMinutes` | `DMS.PitStop.TimeoutMinutes` |
| `DMS.AutoModifyTestStatus` | `DMS.StatusCorrection.AutoModifyTestStatus` |
| `DMS.IgnoreFlagList` | `DMS.StatusCorrection.IgnoreFlagList` |
| `DMS.TestTriggerSampleDeletion` | `DMS.SampleCleanup.TestTriggerSampleDeletion` |
| `DMS.DeleteTableList` | `DMS.SampleCleanup.DeleteTableList` |
| `DMS.SendCancelMessageToAptio` | `DMS.SampleCleanup.SendCancelMessageToAptio` |
| `Aptio.SrmNodeIds: 13`（数字） | `Aptio.SrmNodeIds: ["13"]`（数组） |

---

## 四、Bug 修复

| # | 问题 | 修复 |
|---|------|------|
| 1 | 8+ 个 Worker 循环内 `Task.Delay` 未捕获取消异常，服务停止时抛未处理异常 → **StopHost 崩溃** | 初始/循环尾部 `Task.Delay` 纳入 `try/catch(OperationCanceledException)` |
| 2 | `AptioSocketClient.Dispose()` 与 `StopAsync()` 重复取消 → `ObjectDisposedException` 崩溃 | `Interlocked` 防重入 + `try/catch` |
| 3 | SQL 初筛带 `LIMIT`，匹配样本超上限时**漏掉排后的样本** | 移除 `LIMIT`；单轮上限改由 Router 分发处控制 |
| 4 | Router 去重仅按「最近 10 分钟推送过」，队列积压/中断超 10 分钟会**重复入队** | 改为基于扫描结果的精确去重（见一） |
| 5 | `Delivery` / `TestNameDispose` 越界写 `sam_dispose_status` 表 | 移除；这三个功能**不写库**，数据库由 Aptio 消息驱动更新 |
| 6 | `StatusCorrection` 忽略标志未解析 `jsnflaginstrument`，误将非最终结果置 V | 解析 JSON flags，仅命中 `IgnoreFlagList` 才置 V |

---

## 五、日志增强

- Scanner：每轮打印候选样本数 `Scan found {Count} candidate samples`
- Router：推送/去重统计 `Router pushed {Pushed} tasks, skipped {Skipped} scheduled (scanned {Scanned})`
- Socket：连接、梯度重试、重连、命令超时均有日志

---

## 六、部署说明

1. **Linux**：需保留 `libe_sqlite3.so` 与可执行文件同目录；`systemctl restart uniflow`
2. **Windows**：`UniFlow.exe` 作为服务运行；需保留 `e_sqlite3.dll`
3. **扫描器无独立开关**：任一测试触发功能（Delivery / Priority / TestNameDispose）开启即自动启用
4. **路径注意**：`Aptio.SrmExport.OutputPath` 若为 Windows 路径（如 `F:\...`），部署到 Linux 需改为 Linux 路径
5. **热加载**：功能开关支持运行时通过 Web 界面切换（`IOptionsMonitor`）

---

## 七、验证摘要

- 编译：`Build succeeded, 0 Error(s)`
- 功能测试（测试库 + 伪造 Aptio 2055）：
  - 扫描 → 完全匹配 → 推送 → 消费 → 发送命令，全链路通过
  - 去重：3 个样本首次推送后，连续 4 轮扫描零重复
  - 断线重连：梯度重试 `3s→30s`，恢复后自动继续消费
  - 功能开关：关闭后不推送，开启后恢复
