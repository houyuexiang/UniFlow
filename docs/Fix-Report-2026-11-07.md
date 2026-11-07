# UniFlow 问题修复统一报告

- **日期**：2026-11-07
- **仓库**：`/root/opencode_project/UniFlow`
- **基线**：HEAD `6d684fa`，版本号仍 `1.0.6.5`
- **状态**：所有改动**未提交**（工作区 63 个变更/新增文件，含本报告）；老 Delphi 程序（`archives/**`）未动
- **审查依据**：`docs/Code-Review-2026-11-06.md`（A 档全项目审查）+ 多轮独立复核（GLM-5.3-Flash / DeepSeek-Flash）+ Playwright 49 用例
- **最终验证**：`Build 0 Error`、`380/380` 单测、`49/49` Playwright、`node --check` 通过

---

## 0. 执行摘要

本轮从“老版 DisposeSample 每隔 4 秒 Disconnect、随后彻底无反应”出发，完成了：

1. **协议语义纠正**：确认 Aptio Host LIS 的 COMMENT/ORDER **没有 ACK**，成功判据改为“写成功=已投递 + 数据库 `&3-{node}-%` 确认”。
2. **丢弃状态机重构**：发送窗口 10、5 分钟批量检查/重排队、24 次失败、STB/Pink 测试开关、时间窗口。
3. **全项目审查**：发现 1 个 P0、4 个 P1、多个 P2/P3；**P0/P1 全部修复**，P2 绝大部分修复，少量按决策延后。
4. **Web 管理面加固**：升级/重启/改配置/删除/下载全部鉴权；配置密码递归打码；批量保存一次鉴权；数据库连通性测试；前端 XSS 修复。
5. **AptioSocketClient 加固**：有界队列、请求过期、队列年龄、连接总超时/冷却、探测后 `Connected`、单飞、慢链路、Stop 结算。
6. **DMS/PitStop 数据正确性**：PitStop 只删超时 id；DMS 多表删除事务；回滚不再发 Aptio 报文。
7. **回归体系**：单测 45 → **380**；Playwright UI+API **49**；状态机图 `docs/dispose-state-machine.html`。

> 结论：**当前代码可以进入合并/测试包阶段**；剩余项主要是“真实环境验证”和少量低风险 P3，见 §7/§8。

---

## 1. 验证结果

| 项 | 结果 | 命令/证据 |
|---|---|---|
| 构建 | **0 Error / 2 Warning**（均 NU1903 SQLitePCLRaw 既有依赖告警） | `/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release` |
| 单元/集成测试 | **380 / 380** | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` |
| Playwright API+UI | **49 / 49** | `run-test/playwright/`（API 32 + UI 17；含 XSS/401/403/取消/favicon） |
| 前端语法 | OK | `node --check code/src/UniFlow/wwwroot/app.js` |
| 独立复核 | PASS（P0/P1 全部成立；P2/P3 最终批次 PASS） | GLM-5.3-Flash / DeepSeek-Flash 多轮；报告见 `docs_20261103/Review-*.md` |
| 负向验证 | 关键闸门均能变红 | 见各节“负向” |

**测试环境说明**：Playwright 用冻结副本 `dotnet publish` + 测试配置（`127.0.0.1:5199`、`AdminPassword=Advia1`、Features 全 false），不连真实 MySQL/Aptio。

> 说明：DMS 事务测试（F-1）与“回滚仍发 Aptio 报文”（F-2）是最终复核后追加的跟进项，已用 7 个新增用例 + 负向对照验证（380 全绿）；未再单独跑一轮 reviewer，建议合并前做一次 10 分钟 spot-check。

---

## 2. 丢弃链路（DisposeSample / Aptio）

### 2.1 老版 ACK 死锁（根因）

**原理**

- 厂商手册 `DCAU-H00.816.12.01.02.pdf` §8.59（`/tmp/dcau.txt:24869-24881`）：ACK/NAK 是 **Dream→GUI 通知**，只对应 ADVANCED-CONFIG-CHANGE、CLEAR-TUBES、COMMENT I021~I024 六条 GUI 命令；§9.3（`/tmp/dcau.txt:26761-26780`）COMMENT S002 无任何回复定义。
- 老 Delphi `Main.pas:91-96` 只在收到 `ACK` 时置 `send=1`、进 Ack；服务端不回 ACK → 永远停在 Ready，每 4 秒重发同一条 COMMENT，`max_wait_discard_count` 的数据库确认逻辑一次都走不到。
- 新 .NET 初版沿用 ACK 判定，而 socket 层 304d1b4 后对非 STATUS 命令一律返回 null → **必然死锁**。

**影响面**

- 定时丢弃功能实际上不推进：同一 barcode 反复下发，后续样本永远轮不到；
- `sam_dispose_status.send` 永远 0，10 天后历史清理直接删记录；
- 现场表现为“每 4 秒一条 DisConnect/COMMENT”，与用户最初现象一致。

**决定**：修。协议以厂商手册为准；**写成功 = 已投递**，丢弃成功只认数据库 `t_location` 变 `&3-{node}-%`。

**修法**

- `AptioSocketClient` 非 STATUS 命令返回 `DeliveredMarker="OK:SENT"`；
- `AptioCommandService` 四个命令方法改为 `delivered != null`；
- `DisposeSampleWorker.HandleReadyAsync` 成功即 `send=1` 并进 Ack；
- 测试：`HandleReady_StatusOk...`、`HandleReady_NotDelivered...`、`AptioCommandServiceTests`；全仓 `StartsWith("ACK")` 0 命中。

**残余**：协议无 ACK，写成功只代表“进入对端 TCP 缓冲区”；真正的丢没丢仍靠 `&3` 轮询 + 24 次上限。

### 2.2 发送窗口 = 10（避免冰箱收到过多指令）

**原理**

- 旧程序 `max_onetime_select_discard_count`（现场 10；代码兜底 10；归档 INI 8/5）限制单轮选取；状态机一次只处理一个 `FPreBarcode`。
- 新代码把窗口显式化：
  - `active = send=0 待发 + send=1&disposed=0 未确认`；`active≥10` 不再插入新记录；
  - `inFlight = send=1&disposed=0`；`inFlight≥10` 不发任何指令（含 STATUS）；
  - 有 `send=0` 时先发（否则 10 条待发会永远卡在 None）；
  - 不在 `RunDays/TimeRanges` 内只查库、不发指令，等下一个时间窗口。

**影响面**

- 防止 SRM/冰箱被大量 COMMENT 冲击；窗口满时等待，后续样本排队；
- 若没有“先发待发”分支，会出现“10 条 send=0 永远不发”的老毛病（已用测试锁定）。

**决定**：`MaxOnetimeSelectDiscardCount=10` 同时作为窗口；页面/配置可调；窗口满只告警/等待，不丢记录。

**修法/证据**

- `DisposeSampleWorker.HandleNoneAsync`、`HandleReadyAsync`；`ConfigShared.cs` 默认 10；
- 测试：`HandleNone_PendingSendWithActiveAtWindow...`、`HandleNone_WindowFull...`、`HandleReady_WindowFull...`；
- 负向：去掉窗口判断 → 用例红。

### 2.3 5 分钟批量检查 + 条件重排队（不是直接重发）

**原理**

- 原“60 分钟重发、50 条一批”会绕过窗口，坏链路下形成指令突发；
- 用户口径：每 5 分钟批量检查；未丢弃且冰箱状态正常/错误码允许 → **放回 `send=0` 队列**，由状态机按窗口一条条发；不直接批量发 COMMENT。

**影响面**

- 避免重发绕过窗口；重发次数只在真正发送成功时 +1；
- 冰箱 Stand-by（STB）默认不算可丢弃；`AllowStandbyDispose=true` 测试开关才放行 STB/Pink；GTO 始终不允许。

**决定/修法**

- `ResendIntervalMinutes=5`、`MaxResendCount=24`、`AllowStandbyDispose=false`；
- `MaybeRequeueForResendAsync`：工作时间 → 窗口 → 候选 → 1 条 STATUS → 逐节点 `IsNodeReady` → `RequeueForResendAsync`（只 `SET send=0`，不改 send_time/resend_count）；
- `UpdateStatus("send")`：`resend_count = resend_count + IF(send_time IS NULL,0,1), send=1, send_time=@Now, checkcount=0`；首次 +0、重排队 +1、页面 reset +0；
- 24 次后 `send=3` 释放窗口；`RecheckPendingDisposalsAsync` 每 60s 轮转补查（ID 游标，201 条用例）。

**证据**：`DisposeSampleWorkerRequeueTests`、`DisposeSampleWorkerResendTests`、`DisposeSampleWorkerStatusUpdateFailureTests`；Playwright 丢弃队列页。

### 2.4 `UpdateStatusAsync` 静默失败（P1-4）

**原理**：旧实现 catch 后只 LogError，不返回失败；`HandleReadyAsync` 仍 `_state=Ack`，DB 仍 `send=0` → 下一轮重复下发。

**影响面**：DB 死锁/断连/超时时，状态机与 DB 不一致；重复实验指令。

**决定**：修。`Task<bool>` + 所有调用点处理；`send` 失败不进 Ack + Error/degraded；`disposed/checkcount/skip` 失败不静默。

**残余**：命令已投递但 DB 写失败时，保持 Ready 会重复下发（协议幂等 + Error 告警）；后续可加“内存已投递标记/DB 重试”。

### 2.5 队首阻塞边缘（`SkipOnUnknownNode=false`）

**原理**：未知节点 + skip=false 时原代码“attempt anyway”后仍 `IsNodeReady`（未知节点恒 false）→ 永久停队首。
**修法**：未知节点且 skip=false → 跳过 `IsNodeReady` 直接发送；已知节点才检查。
**证据**：`DisposeSampleWorkerUnknownNodeTests`。

---

## 3. AptioSocketClient（P1-3）

### 3.1 原问题与影响

| 问题 | 原理 | 影响 |
|---|---|---|
| 队列无界 | `Channel.CreateUnbounded` | 断链数小时内存增长 |
| 1h 超时后陈旧重放 | `SendAsync` 超时返回 null，但请求仍在 channel；恢复后仍发送 | 1h 前的 COMMENT/ORDER 被补发，可能重复丢弃/取消 |
| 无限重连阻塞单消费者 | `EnsureConnectedAsync` 的 `while(!Connected)` 无总上限，`SendCoreAsync` 每条命令前调用 | Aptio 宕机时单消费者永久卡住，所有模块排队 |
| `Connected` 不可靠 | `TcpClient.Connected` 只反映上次 I/O | 半开/假死链路被误判在线 |

### 3.2 修复

- **有界队列 1000**（`FullMode=Wait`）；
- **入队超时 30s**（独立于 `CommandTimeout=1h`）；队列满 → 标记过期、返回 null；
- **排队年龄上限 120s**：消费者取到超龄请求直接丢弃，不发送；已进入写 socket 的无法撤回（明确边界）；
- **连接总超时 120s + 失败冷却 30s**；冷却内毫秒级 `TimeoutException("Aptio connect cooldown")`；
- **`Connected` 只在探测成功后 true**；代际 + writer 判空双保险；
- **单飞**：公开 `ConnectAsync` 与内部发送走同一 `_ensureLock` 连接+探测流程；慢链路（STATUS 3.5s > 3s 轮询）不再被 worker 拆链；实测 3.5s 连上、accept/probe 各 1；
- **预连接持续重试**：预算到期不退出，等冷却后继续；端口恢复自动连上；
- **StopAsync 结算 pending**：队列/在途请求 `TrySetResult(null)`，停止后 `SendAsync` 不抛未处理异常；
- **`failReason` 并入日志**；**删除 `MarkResendAsync` 死代码**。

### 3.3 证据/残余

- 测试：`AptioSocketClientQueueTests`、`AptioSocketClientStopTests`、`AptioSocketClientTests`；全量 380；Connected 用例孤立 20/20、全量 10/10。
- 负向：去掉 Expired/冷却/有界队列/慢链路单飞 → 用例红。
- 残余：已进入写 socket 的请求无法撤回；`CommandTimeout` 仍 1h（但单条预算 120s 先生效）；真实 Aptio 断链未现场压测。

---

## 4. WebAdmin 鉴权 / 配置安全

### 4.1 P0-1 无鉴权升级端点（局域网 RCE）

**原理**

- `appsettings.json` 默认 `BindIp=0.0.0.0`；
- `/api/update/upload` 只校验 zip 内含 `UniFlow`/`UniFlow.exe`，无签名/来源校验；
- `/api/update/apply` 将 staging 二进制替换运行中的主程序；`/api/restart/process` 触发 `Environment.Exit(1)` 由 supervisor 拉起。

**影响面**

- 同网段任意主机可上传自定义二进制 → 替换现场服务 → 完全接管（数据窃取/篡改实验室指令）。
- 复核时已在 `/tmp` 副本**实际复现**：假二进制 upload → apply → restart，进程被替换/退出。

**决定**：修。所有破坏性端点加管理鉴权；升级包仍建议后续加签名（本轮未做签名，风险降低但未消除）。

### 4.2 P1-1 管理 API 无鉴权 + 明文密码下载

**原理**

- `/api/config/download` 直接返回原文 `appsettings.json`（含 DB 密码/管理密码）；
- `/api/config` PUT、`/api/errors` DELETE、dispose reset/delete 均可未授权调用。

**影响面**：密码泄露 → 直连 flexlab/DMSCN；配置篡改 → 指向错误主机/关闭功能/清空队列。

**修法**

- **一次性 nonce + SHA-256(密码+nonce)**：`GET /api/auth/challenge`（60s、一次性）；`X-Admin-Nonce`/`X-Admin-Hash`；
- 每次动作弹一次密码（一次“保存全部”只算一次）；多请求动作每请求新 nonce；
- `PUT /api/config/batch` 一次提交、全部校验通过才写；密码 `***`/空保持原值；
- `GET /api/config/download` 需鉴权、返回完整原文；页面显示仍打码；
- `POST /api/config/test-db` 免鉴权、只测已保存配置、不返回密码；页面提示“要测修改后的配置请先保存”；
- `WebAdmin:AdminPassword` 默认 `Advia1`，页面不可改，只能后台改；
- **`GET /api/config?path=<父节点>` 递归打码**（复核发现的 P0 绕过：`?path=WebAdmin`/`Aptio:Database`/`DMS` 曾泄露明文；已修 `ConfigRedactor`，对象/数组递归，大小写不敏感；负向 12 用例）。

**证据**：`AdminAuthServiceTests`、`AdminAuthApiTests`（41 例真实 Kestrel）、`ConfigRedactionApiTests`、`ConfigServiceBatchTests`、Playwright 401/403/取消/下载。

**残余**：HTTP 明文环境下 nonce/hash 仍可能被同网段嗅探重放（60s 窗口）；这是“防误改/防静默改配置”的轻量闸门，不是 HTTPS 级安全；升级包签名未做。

### 4.3 前端 XSS / UX

**原理**：dispose 表格用内联 `onclick` 拼条码（仅转义单引号），`escapeHtml` 不转义双引号 → 属性逃逸；健康/错误/日志动态字段未统一转义。

**修法**

- `escapeHtml` 转义 `& < > " '`；
- dispose 行改 `data-act/data-i` + 事件委托，条码从 `_disposeData` 取原始值，不再拼内联 JS；
- 健康卡片/错误列表/日志列表/图表/编辑框统一转义；
- 下载 401 重输重试一次、403 不重试；上传 403 不重传；取消文案“已取消，未执行/未保存/未下载”；
- favicon data URI；`refreshAll` 不再预取 dispose pending（减少 DB 不可达时 console 500 噪音）。

**证据**：Playwright UI-8（恶意条码 mock、下载 401/403、取消、上传 403、favicon）；49/49。
**残余**：引号转义的“纯属性注入”无独立红闸门（P3）；健康/错误转义只有单测/代码级，无 Playwright mock。

---

## 5. DMS / PitStop 数据正确性

### 5.1 PitStop 单条超时清空整表（P2）

**原理**：旧 `DeletePitstopRecordsAsync(table)` 删除该表所有 `flgrunning <> ''`；一条卡死任务会把同表其它尚未超时的运行中任务一起清掉，并 `_seen[table].Clear()` 重置计时。
**修法**：只收集超时 `idpitstop`，`DELETE ... WHERE idpitstop IN @Ids AND flgrunning <> ''`；`_seen[table]` 只移除对应 id；表名白名单。
**证据**：`PitStopMonitorServiceTests`、`DmsDatabaseServiceDeleteTests`（只删 A、B 保留；B 计时不重置）。
**残余**：无真实 DMS 实例 E2E。

### 5.2 DMS 样本删除无事务（P2）

**原理**：`DeleteSampleAsync` 逐表删除，失败只 `failedTables++`，无回滚 → 半删除（reqtube 没了/orders 还在）。
**修法**：`BeginTransaction/Commit/Rollback`；任一表失败 → 回滚 → 返回 0；`DeleteSampleWithConnectionAsync` 抽为可测的生产事务编排；SQLite 测试真实调用生产事务方法；负向（去掉事务 → 回滚用例红）。
**证据**：`DmsDatabaseServiceDeleteTests` 10 例（回滚后两表行都在、第二连接可见、无残留写锁、Commit 后第二连接不可见）。
**残余**：MyISAM 等非事务引擎不参与回滚（MySQL 限制，已注明）；真 MySQL 未 E2E。

### 5.3 删除回滚仍发 Aptio 报文（F-2）

**原理**：`SampleCleanupService` 无条件 `SendAptioActionAsync` 并清除 `_firstSeenUtc`；删除回滚（返回 0）时 DMS 行还在，但 Aptio 已收到取消/完成报文，计时被清 → 延后重删。
**修法**：仅 `affected > 0` 才发 Aptio 报文并移除计时；`affected==0` Warning + 保留计时（若样本确实消失，现有 current 清理会移除）。
**证据**：`SampleCleanupServiceTests` 4 例（0 不发/保留计时/下轮重试；>0 发/清计时）。
**残余**：`affected==0` 无法区分“回滚”与“本来就没有匹配行”；统一不发报文，低风险。

---

## 6. 部署 / 工具 / 配置

### 6.1 Docker 配置旧平铺结构（P1-2）

**原理**：`deploy/appsettings.docker.json` 仍是 `Features.AptioAutoProcess`、顶层 `Database/Dispose/Export`；当前模型绑 `Aptio:Database`/`Aptio:DisposeSample`/`Features.Aptio`；`ConfigService.MigrateLegacyKeys` 不迁移这些平铺键；`UniFlow__Database__*` 是死变量（应用默认环境变量源无前缀）。
**影响**：容器启动后连 127.0.0.1 默认库，功能错配，Docker 形态不可用。
**修法**：模板重写为当前结构；compose 环境变量改 `Aptio__Database__*`/`Features__Aptio__*`；build context 修正为仓库根；真实 `ConfigurationBuilder` 绑定测试；文档统一无前缀。
**残余**：Docker 容器未实跑（无 Docker 环境）。

### 6.2 SrmExport 精确分钟（P3）

**原理**：`now.Minute==target.Minute` + 仅 0 点复位；循环延迟/DB 慢会错过当天。
**修法**：`_lastExportDate` + `now.TimeOfDay >= target` 当天一次；DB 失败不标记当天、可补；重启可补。
**证据**：`SrmExportWorkerScheduleTests` 12 例。

### 6.3 其它已修 P3

- `install-service.ps1` 源码路径 `..\..\code\src` + 发布前 csproj 校验；
- `ASTM.cs` `Environment.TickCount` → `TickCount64`；
- `StopAsync` 结算 pending 请求；
- `failReason` 死变量并入日志；
- `MarkResendAsync` 死代码删除（全仓 0 命中）；
- `/api/dispose/pending` DB 不可达 → 503 `Database unavailable`，其它 500；
- 小写密码 path 打码测试（`?path=webadmin:adminpassword` 等）；
- 进程单实例 `SingleInstanceGuard`（`Global\UniFlow`，权限不足回退 `Local`；AlreadyRunning 退出；Unavailable 仅告警继续）；
- favicon data URI；`refreshAll` 去 dispose 预取。

---

## 7. 决策日志（修 / 不修 / 延后）

| 项 | 决定 | 理由 |
|---|---|---|
| P0-1/P1-1 鉴权 | 修 | 局域网 RCE/密码泄露，风险最高；采用 nonce+hash + 每动作一次密码，兼顾可用性 |
| P1-2 Docker 配置 | 修 | 容器形态否则不可用 |
| P1-3 socket 队列 | 修 | 陈旧重放/无限阻塞是数据一致性风险 |
| P1-4 UpdateStatus | 修 | 静默失败会导致重复下发 |
| P2 XSS / PitStop / DMS 事务 / SkipOnUnknownNode | 修 | 存储型 XSS、数据半删除、队首卡死 |
| P2 `GetUnknownPatientOrdersAsync` SQL 优先级 | **延后** | 当前无调用方（死代码），无生产影响；删除/加括号留待清理单，避免本轮扩大 diff |
| P3 install-service `install` 分支已有 exe 时跳过 publish | **延后** | Windows 升级脚本行为，需现场确认是否故意；本轮只修源码路径 |
| P3 `UniFlowFileLogger` 全局锁+每行 Flush | **延后** | 性能优化，不影响正确性；高日志量现场再评估 |
| P3 配置双源（worker 直接读 JSON + IOptions） | **延后** | 语义不一致风险，需统一配置入口；改动面大 |
| P3 `SanitizeSqlName` 允许反引号 | **延后** | 仅本地配置值，低风险；后续收紧 |
| P3 `Decode()` 首节点无条件 return true | **延后** | 生产主路径用 `IsNodeReady`，仅诊断方法；避免不必要改动 |
| P3 DMS 表名插值（来自 information_schema） | **延后** | 非用户输入，低风险；已加白名单的 PitStop 侧 |
| P3 SQLitePCLRaw 2.1.11 / MySqlConnector 2.4.0 漏洞 | **延后** | 依赖升级需回归/兼容验证；现场无直接暴露面，先记录 |
| `send` DB 失败重复下发 | **接受并记录** | 命令已投递但 DB 失败时保持 Ready；协议幂等 + Error/degraded；后续可加内存标记 |
| HTTP nonce/hash 重放 | **接受并记录** | 局域网可信 + 60s 一次性；要更强需 HTTPS |
| MyISAM 不参与事务 | **接受并记录** | MySQL 引擎限制；现场应使用 InnoDB |

---

## 8. 残余风险与下一步

### 8.1 必须现场/真实环境补验

1. **真 MySQL（flexlab/DMSCN）**：`ALTER TABLE resend_count` 迁移、`IF()`/`TIMESTAMPDIFF`/`LIMIT @p`/`information_schema`、DMS 事务回滚（InnoDB/MyISAM）、PitStop 定点删除。
2. **真 Aptio 2055**：无 ACK、半开连接、慢 STATUS（3.5s）、断链恢复、重发/重排队、STB/Pink 开关（`AllowStandbyDispose`）。
3. **目标机**：Windows Service / Linux systemd / Docker 容器 / 升级 rename-replace / 单实例 `Global\UniFlow`（Windows 服务账号权限）。
4. **浏览器 E2E**：Playwright 已覆盖本地 UI/API；现场浏览器/网络环境再点一遍（保存一次只弹一次密码、下载、测试连接、上传）。
5. **MySQL 依赖漏洞**：评估 SQLitePCLRaw/MySqlConnector 升级。

### 8.2 已知低风险残余

- `SampleCleanupService` 回滚时每轮重试会重复 Warning（可加节流）；
- XSS 引号转义无独立红闸门（代码已修）；
- `install-service.ps1` 已有 exe 时跳过 publish（F-4）；
- `send` DB 失败重复下发（已记录）；
- HTTP 鉴权重放窗口 60s；
- 日志全局锁/Flush、配置双源、`SanitizeSqlName` 反引号、`Decode()` 首节点等 P3。

---

## 9. 交付物与运行方式

| 文件 | 说明 |
|---|---|
| `docs/Code-Review-2026-11-06.md` | 全项目代码审查报告（P0/P1/P2/P3） |
| `docs/dispose-state-machine.html` / `.lifecycle.json` | Dispose 状态机图（窗口/重排队/STB） |
| `run-test/playwright/` | Playwright API+UI 套件（`npm install && npx playwright test`；`node_modules` 不入库） |
| `docs/Fix-Report-2026-11-07.md` | 本报告 |
| `deploy/appsettings.docker.json` / `docker-compose.yml` | 当前结构 + 无前缀环境变量 |

**现场配置要点**

```json
"Aptio": {
  "DisposeSample": {
    "MaxOnetimeSelectDiscardCount": 10,
    "ResendIntervalMinutes": 5,
    "MaxResendCount": 24,
    "AllowStandbyDispose": false
  }
},
"WebAdmin": { "AdminPassword": "Advia1" }
```

- `AllowStandbyDispose=false` 默认；现场测试 STB/Pink 时打开，测完决定是否长期开启；GTO 始终不允许。
- 管理密码只在后台 `appsettings.json` 改；页面不可改；下载完整配置需鉴权。
- Docker 环境变量用 `Aptio__Database__*`/`Features__Aptio__*`（无 `UniFlow` 前缀）。

---

## 10. 最终结论

- **P0-1、P1-1~P1-4 全部修复**，并有独立复核/负向验证；
- **P2 绝大部分修复**（XSS、PitStop、DMS 事务、SkipOnUnknownNode、Aptio 队列等）；
- **P3 修复了可安全落地的项**（SrmExport、install 路径、TickCount、StopAsync、死代码、单实例、favicon、503 等），少数按决策延后并记录；
- **380 单测 + 49 Playwright 全绿**；构建 0 Error；
- 当前改动**未提交**，版本号仍 `1.0.6.5`，老程序未动。

建议下一步：**先提交/打测试包**，按 §8.1 在现场做真 MySQL/Aptio/Windows/Docker 验收；验收通过后再决定是否升级依赖、清理 P3。
