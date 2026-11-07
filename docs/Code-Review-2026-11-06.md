# UniFlow .NET 项目代码审查报告

- **日期**：2026-11-06
- **仓库**：`/root/opencode_project/UniFlow`，HEAD `6d684fa`，工作区 32 个变更文件（含未提交改动）
- **审查方式**：只读 + 重跑构建/测试；未修改任何业务代码/配置/测试；仅写本报告。
- **审查范围**：`code/src/UniFlow/**`、`code/tests/**`、`deploy/**`、`docs/**`（未审 `archives/**` 老 Delphi）
- **规模**：约 7,400 行 C# + 70KB 前端 JS（app.js 1386 行）

## 0. 验证命令与结果（实测）

```bash
export HOME=/root
/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release
/usr/share/dotnet/dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release
node --check code/src/UniFlow/wwwroot/app.js
```

| 命令 | 结果 |
|---|---|
| build | **成功** 0 Error / 2 Warning（均 `NU1903: SQLitePCLRaw.lib.e_sqlite3 2.1.11` 高危漏洞） |
| test | **186 通过 / 0 失败 / 0 跳过**（4s） |
| `node --check app.js` | **OK**（语法通过） |

---

## 1. 严重度总览

| 严重度 | 数量 | 主题 |
|---|---|---|
| P0 | 1 | 无鉴权升级端点 → 局域网远程代码执行（RCE） |
| P1 | 4 | 管理 API 无鉴权（含密码泄露/破坏性操作）、Docker 配置失效、Aptio 命令队列无界+陈旧重放 |
| P2 | 6 | 存储型 XSS、SQL 优先级、PitStop 整表清空、静默 DB 写失败、DMS 删除无事务、状态机队首阻塞边缘 |
| P3 | 9 | 死代码/回绕/性能/脆弱性/部署脚本路径等 |

---

## 2. P0 —— 阻塞合并/上线

### P0-1 无鉴权的在线升级端点 = 局域网任意代码执行（RCE）

- **位置**：`code/src/UniFlow/WebAdmin/ApiEndpoints.cs:481-534`（`/api/update/upload`）、`:537-604`（`/api/update/apply`）、`:31-42`（`/api/restart/process`）
- **证据**：
  - `appsettings.json:134-139` `WebAdmin.BindIp="0.0.0.0"`（默认监听所有网卡），`Enabled=true`。
  - `ApiEndpoints.cs:15` 注释明示“当前内网明文环境暂不启用”鉴权（`TODO(鉴权预留)`）。
  - `upload` 端点对请求体仅做「zip 里必须含 `UniFlow`/`UniFlow.exe`」这一条校验（`:496-503`），**不校验签名/来源**。
  - `apply` 端点把 staging 里的二进制 `File.Move` 到运行中的主程序路径（`:570-571`），覆盖 wwwroot（`:578-591`），随后可 `POST /api/restart/process` 触发 `Environment.Exit(1)`（`:39`）由 supervisor 拉起新二进制。
- **影响**：同一局域网内任何主机（或已沦陷的任意一台）可上传自定义二进制并替换现场服务，实现**完全接管**（代码执行、数据窃取、篡改实验室指令）。
- **最小修法**：
  1. 所有 `/api/update/*`、`/api/restart*`、`/api/config`（写）、`/api/dispose/pending/delete`、`/api/errors`（DELETE）统一走鉴权中间件（`X-Api-Token` 头或 Basic Auth，token 从 `appsettings.json` 或环境变量读，密码打码不落到日志）。
  2. 升级包**校验数字签名**（至少校验上传包 md5 与预期一致，理想为代码签名证书）。
  3. 默认 `WebAdmin.BindIp` 改为 `localhost`（除非显式配置对外）。
- **是否阻塞**：**阻塞**。这是本次改动范围之外的历史遗留，但在上线前必须处理（现场为 LAN 环境，0.0.0.0 + 无鉴权是默认态）。

---

## 3. P1 —— 高风险，上线前应修复

### P1-1 管理 API 全量无鉴权 + 明文密码下载

- **位置**：`ApiEndpoints.cs:263-318`（`/api/config` GET/PUT、`/api/config/download`）、`:120-206`（dispose 队列操作）、`:256-260`（`/api/errors` DELETE）
- **证据**：
  - `/api/config/download`（`:311-318`）直接 `Results.File(File.ReadAllBytes(path), ...)` 返回**含明文 DB 密码**的完整 `appsettings.json`，注释自认“含敏感字段”（`:311`）。`RedactPasswords` 只作用于 `/api/config`（GET）内存对象（`:269`），**不作用于 download 端点**。
  - `/api/config` PUT（`:320-341`）无鉴权可改任意配置（含 `Aptio:Database:Password`、`DMS:DbPassword`、`Aptio:Ip/Port` 等）。
  - `/api/dispose/pending/delete`（`:181-206`）无鉴权可删除丢弃队列记录。
- **影响**：密码泄露 → 直接访问 flexlab/DMSCN 数据库；配置篡改 → 把 Aptio 指向错误主机、关闭/打开功能、清空队列。
- **最小修法**：同 P0-1 的鉴权中间件覆盖；`/api/config/download` 改为返回**打码后**配置或要求 token。
- **是否阻塞**：**阻塞**（与 P0-1 同根因）。

### P1-2 Docker 部署配置仍是旧平铺结构 → 容器连错库/功能错配

- **位置**：`deploy/appsettings.docker.json`、`deploy/Dockerfile:18`（`COPY deploy/appsettings.docker.json /app/appsettings.default.json`）、`deploy/docker/entrypoint.sh:5-7`（复制为 `config/appsettings.json`）
- **证据**（`deploy/appsettings.docker.json`）：
  - `"Features": { "AptioAutoProcess": false, "ImmuliteWorkOrderClean": false, "DmsAutoOrder": false }` —— 当前代码读 `Features.Aptio.DisposeSample`、`Features.Immulite.WorkListCleaner`、`Features.Dms.*`（`Program.cs:209-335`）。
  - 顶层 `"Database": {...}`（`:25-31`）、`"Dispose": {...}`（`:32-49`）、`"Export": {...}`（`:50-54`）—— 当前绑定 `Aptio:Database`、`Aptio:DisposeSample`、`Aptio:SrmExport`（`ConfigShared.cs`、`Program.cs:228`）。
  - `"SrmNodeId": "09"`（字符串）vs 当前 `Aptio:SrmNodeIds`（数组）。
  - `ConfigService.MigrateLegacyKeys`（`ConfigService.cs:53-148`）**只会**迁移 `Features.AptioAutoProcess`（bool→分组开关）与**嵌套** `AptioAutoProcess` 字典（`:88-105`），**不会**迁移顶层平铺的 `Database`/`Dispose`/`Export` 键。且迁移只在调用 `ReadAll()`（即 `/api/config` GET）时才触发，业务宿主启动时 `BindAptioConfig`（`Program.cs:177-183`）直接 bind `Aptio` 段，不触发迁移。
  - `deploy/docker-compose.yml:18-23` 环境变量 `UniFlow__Database__Host` 等映射到不存在的 `UniFlow:Database` 配置根（实际根是 `Aptio:Database`），属**死变量**。
- **影响**：Docker 容器启动后，`Aptio.Database` 走默认值（127.0.0.1:3306 flexlab root/root），`DisposeSample/SrmExport` 用默认阈值；`Features` 未正确映射。**容器内连不到真实 MySQL**（PingAsync 失败 → 所有 Worker degraded），或误连错误库。容器部署形态实际不可用/误导。
- **最小修法**：把 `deploy/appsettings.docker.json` 重写为当前结构（与 `appsettings.json` 对齐），修正 docker-compose 环境变量名为 `Aptio__Database__*`；或在 entrypoint 启动前跑一次强制迁移。
- **是否阻塞**：Docker 形态**阻塞**；Linux systemd / Windows 现场（直接 copy `appsettings.json`）不受影响。

### P1-3 AptioSocketClient 命令队列无界 + 1h 超时后陈旧重放 + 无限重连阻塞单消费者

- **位置**：`Common/Services/AptioSocketClient.cs:31`（unbounded channel）、`:129-147`（`SendAsync` 1h `WaitAsync`）、`:166-197`（单消费者 `ProcessLoopAsync`）、`:203-241`（`EnsureConnectedAsync` 无限重连）
- **证据**：
  - `_queue = Channel.CreateUnbounded<SocketRequest>()`（`:31`）——无上限。
  - `SendAsync` 用 `req.Tcs.Task.WaitAsync(CommandTimeout, ct)`（`:135`），1h 后抛 `TimeoutException` 返回 `null`，**但 `req` 仍留在队列中**（未取消、未移除），消费者随后仍会执行它（`:181-182` `SendCoreAsync` 后 `TrySetResult`，此时已无人 await，结果被丢弃但**命令已被发往 Aptio**）。
  - `EnsureConnectedAsync`（`:203`）`while(!Connected)` 内按梯度重试（3s→30s→60s）**永不放弃**；它被 `SendCoreAsync`（`:294`）在每个命令处理前调用，因此单消费者在 Aptio 宕机期间**永久阻塞**在此，队列持续堆积。
- **影响**：
  1. **陈旧重放**：DisposeSampleWorker 在 1h 超时后认为「未投递」（`AptioCommandService.SendDisposeAsync` 返回 false，`HandleReadyAsync:633` 不置 send=1），但命令在恢复后被重放 → 重复丢弃/取消指令；对 `ORDER … C`（取消测试）可能取消掉期间重新下单的测试。
  2. **无界内存增长**：长时间中断（如数小时）期间队列无上限堆积。
  3. 单消费者被无限重连阻塞，期间所有模块（Dispose/SrmExport/DMS/WorkListCleaner 共用同一个 singleton socket 的？否——仅 Aptio 相关）共享该队列的都受影响。
- **最小修法**：
  1. 超时时用 `CancellationTokenSource.CancelAfter(CommandTimeout)` 驱动，并在 consumer 里对「已超时请求」做**丢弃**（`req.Ct.IsCancellationRequested` 时跳过并 `TrySetCanceled`，见 `:173-177` 已处理取消，需确保 `SendAsync` 的 WaitAsync 用同一 linked token 而非只挂外层）。
  2. 给 queue 设上限（`BoundedChannelOptions` + `FullMode.DropOldest` 或 Wait），或对非幂等命令（ORDER/COMMENT）在超时后移除。
  3. `EnsureConnectedAsync` 增加最大重试/退避上限，避免永久阻塞单消费者。
- **是否阻塞**：**非必须本次阻塞合并**，但现场长期 Aptio 断连场景下是真实数据一致性风险，建议上线前处理或列入首轮 hotfix。

### P1-4 静默 DB 写失败破坏状态机一致性（`UpdateStatusAsync` 吞异常）

- **位置**：`DisposeSample/Services/DisposeDatabaseService.cs:240-256`（`UpdateStatusAsync`），调用点 `DisposeSampleWorker.cs:627`（send）、`:658`（disposed）、`:667`（checkcount）、`:298/602`（skip）
- **证据**：`UpdateStatusAsync` 内 `try { ExecuteAsync } catch (Exception ex) { LogError; /* 不抛 */ }`（`:247-255`）。`HandleReadyAsync:627-629` 无条件 `_state = SysStatus.Ack` 而不检查 UPDATE 是否成功。
- **影响**：UPDATE 失败（死锁/连接断开/超时）时，状态机照常推进，DB 仍为 `send=0`。下轮 `CountPendingSendAsync` 仍看到该记录 → **重复下发**丢弃命令；或 `disposed` 标记失败 → 记录被 requeue/补查反复处理。属「错误静默」型数据一致性缺陷。
- **最小修法**：`UpdateStatusAsync` 失败时**重抛或返回 bool**，调用方据此决定是否推进状态（或至少写 health error 并停留重试）。
- **是否阻塞**：建议上线前修复（低概率但后果是重复实验指令）。

---

## 4. P2 —— 应尽快修复

### P2-1 前端存储型 XSS（多处 innerHTML/内联 onclick）

- **位置**：`wwwroot/app.js`
  - `:1302-1315` dispose 队列：`bc` 经 `escapeHtml` 后仍被拼进内联 `onclick="disposeRecheck(['...'])"`，仅做 `bc.replace(/'/g,"\\'")`（**不转义反斜杠/换行**）→ 条码（来自 DB `t_sample.sample_id`）可注入 JS。
  - `:132-134` 健康卡片：`info.message`（来自 health_records，内容可为异常 `ex.Message`）**未转义**直接进 `innerHTML`。
  - `:209` 错误列表：`r.module`/`r.level`/`r.timestamp` 未转义（`message` 已转义）。
- **影响**：存储型 XSS（低可利用率但真实）。DB 条码/异常消息一旦含 `</...>` 或 `onerror` 即执行。鉴于 API 无鉴权（P0/P1），XSS 只是更进一步的横向攻击面。
- **最小修法**：dispose 按钮改用 `data-barcode` + `addEventListener`（去掉内联 onclick）；所有进 innerHTML 的动态字段统一 `escapeHtml`。

### P2-2 `GetUnknownPatientOrdersAsync` SQL 运算符优先级错误（AND/OR 混用）

- **位置**：`DMSAutoOrder/Services/DmsDatabaseService.cs:63-73`
- **证据**：`WHERE (A) AND (B) OR (C)` 无括号，实际求值为 `(A AND B) OR C`，与直觉的「(A AND B) OR C 还是 A AND (B OR C)」不明；`r.codoid NOT IN (SELECT ...)` 单独成为一个 OR 分支，会把**所有无订单的 reqtube** 都纳入。
- **影响**：该方法当前**无调用方**（死代码，旧 DMS 自动下单遗留），故实际无生产影响；若未来启用则结果集错误。
- **最小修法**：若保留，显式加括号明确语义；若废弃，删除。

### P2-3 PitStopMonitor 单条超时 → 清空整表运行中记录

- **位置**：`DMSAutoOrder/Services/PitStopMonitorService.cs:58-67`
- **证据**：某个 `idpitstop` 超时后，`await _db.DeletePitstopRecordsAsync(table)`（`:63`）删除**该表所有 `flgrunning <> ''` 记录**（非仅超时那条），随后 `_seen[table].Clear()`。
- **影响**：一条卡死任务导致同表其它**尚未超时**的运行中任务被一并清除。
- **最小修法**：删除条件加 `idpitstop` 过滤（只删超时的 id），或按 id 逐条删。

### P2-4 DMS 样本删除无事务 → 部分删除导致孤儿数据

- **位置**：`DMSAutoOrder/Services/DmsDatabaseService.cs:98-132`（`DeleteSampleAsync`）
- **证据**：`foreach (template in deleteTableSqlTemplates)` 逐表 `ExecuteAsync`，**未开事务**；失败仅 `failedTables++` 记日志（`:117-122`），无回滚。
- **影响**：跨表删除（reqtube/orders/reqtest/reqtestresult 等）中途失败时样本处于半删除状态（部分表删了、部分没删）。
- **最小修法**：`conn.BeginTransaction()` 包裹整组删除，失败回滚。

### P2-5 状态机队首阻塞边缘（SkipOnUnknownNode=false 且节点未知）

- **位置**：`DisposeSample/Workers/DisposeSampleWorker.cs:596-612`
- **证据**：当 `SkipOnUnknownNode=false` 且记录 location 取不到节点号（或节点不在 `SrmNodeIds`），`:605` 打「attempting dispose anyway」继续，随后 `:608` `IsNodeReady` 对未知节点恒返回 false → 记录永远停在队首（`send=0`），阻塞后续所有记录。
- **影响**：默认 `SkipOnUnknownNode=true` 无影响；配置改为 false 且出现未知节点时队列永久卡住。
- **最小修法**：`SkipOnUnknownNode=false` 时对「取不到节点号」的极端情况也做 skip（或错误标记），不要死循环。

### P2-6 `EnsureConnectedAsync` 无限重连 / `Connected` 属性不可靠（半开连接检测）

- **位置**：`AptioSocketClient.cs:94`（`Connected => _tcpClient?.Connected`）、`:203-241`、`:299-308`（idle 重建）、`:347-372`（STATUS 半开检测）
- **证据**：`TcpClient.Connected` 只反映「最近一次操作」状态，不反映对端真实存活；代码已用 idle>30s 重建 + STATUS 连续 2 次未命中断开（`:56-59`）缓解，属部分覆盖。
- **影响**：`EnsureConnectedAsync` 的 `while(!Connected)` 无退出上限（`ct` 取消才停），在单消费者里被调用会永久阻塞（详见 P1-3）。
- **最小修法**：与 P1-3 合并处理。

---

## 5. P3 —— 建议修复 / 待观察

| # | 位置 | 问题 | 说明 |
|---|---|---|---|
| P3-1 | `SrmStatusDecoder.cs:45-59` | `Decode` 首个节点无条件 `return true`，只判第一个 SRM 节点；返回值语义混乱 | 文档标注生产主路径走 `IsNodeReady`，本方法仅诊断/测试用；语义易误导。 |
| P3-2 | `ASTM.cs:147-148` | `Environment.TickCount` 回绕（~24.9 天）；`AstmConnection` 构造同步 `_tcp.Connect` 阻塞 | 单次回绕下减法安全；仅 Centralink 用，风险低。 |
| P3-3 | `UniFlowFileLogger.cs:148-153,157-163` | 单一全局 `_lock` + 每行同步 `Flush()`（与 `AutoFlush=true` 重复） | 高日志量（DisposeSample debug）下所有模块日志串行 + 每行落盘，潜在瓶颈。 |
| P3-4 | `DisposeSampleWorker.cs:78-102` | 每 3s 直接 `File.ReadAllText` 读 appsettings.json，与 DI 的 `IOptions/IOptionsMonitor` 双配置源并存 | `[Restart(InnerRestart)]` 的 Ip/Port/Database 被 worker 热读但 socket/DB factory 冻结，语义不一致；`File.Move` 覆盖在 Windows 上可能撞读锁（异常已被 catch）。 |
| P3-5 | `DisposeDatabaseService.cs:650-656` | `SanitizeSqlName` 允许反引号 `` ` `` | `CommandName` 为本地配置值，理论上可注入（`SELECT * FROM `x`...`），当前低风险。 |
| P3-6 | `DisposeDatabaseService.cs:232` | `UpdateStatus("send")` 的 `resend_count` 自增依赖 MySQL `SET` 左→右求值 | 语义正确且已注释，但脆弱；SQLite 测试用自定义 `if()` 函数模拟，未验证 MySQL 真实排序行为。 |
| P3-7 | `DisposeDatabaseService.cs:393-398` | `MarkResendAsync`/`MarkResendCoreAsync` 死代码 | 无调用方（接口 `IDisposeDatabaseService.cs:53` 也声明）。 |
| P3-8 | `UniFlow.csproj:16` | 传递依赖 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 存在 NU1903 高危漏洞（GHSA-2m69-gcr7-jv3q） | HealthStore 生产用 SQLite；建议升级 `Microsoft.Data.Sqlite`/`SQLitePCLRaw`。 |
| P3-9 | 全局 | 无进程单实例保护（无 Mutex；老 Delphi 程序有） | systemd/SCM 对「服务」形态有单实例保证，但手动二次启动 `UniFlow.exe`/`UniFlow` 会双写同一 DB/同一 socket。 |
| P3-10 | `deploy/windows/install-service.ps1:7` | `$SrcDir = Join-Path $PSScriptRoot "..\src"` 实际指向 `deploy\src`，而源码在 `code\src` | dev 侧脚本 publish 路径错误。 |
| P3-11 | `SrmExportWorker.cs:96` | `_exportedToday` 仅当 `now.Hour==0 && now.Minute==0` 复位；导出条件要求 `now.Minute==target.Minute` 精确命中 | 循环延迟/DB 慢可能错过当日整点分钟 → 当天不导出（内存态，重启可恢复）。 |
| P3-12 | `DmsDatabaseService.cs:41-61,45` | `GetRunningPitstopRecordsAsync`/`DeletePitstopRecordsAsync` 用字符串插值拼 `tableName` | `tableName` 来自 `information_schema`（DB 侧，非用户输入），注入风险低。 |

---

## 6. 近期改动核对结论（独立核实，不抄实施方自报）

| 改动点 | 核实结果 |
|---|---|
| 发送窗口 `MaxOnetimeSelectDiscardCount=10`：`active=send=0 + (send=1&disposed=0)` 达 10 不插新记录 | ✅ `HandleNoneAsync` `:467-496` 正确；且 `pendingSend>0` 先于窗口判断（`:475-484`），修复了「窗口塞满就不发」老毛病。 |
| `inFlight=send=1&disposed=0` 达 10 不发任何指令（含 STATUS） | ✅ `HandleReadyAsync:562-577`、requeue `:264-270` 均正确。 |
| 每 5 分钟批量检查：1 条 STATUS → 符合条件放回 send=0，不直接发 COMMENT | ✅ `MaybeRequeueForResendAsync:251-326` 正确（先 STATUS、逐条 `IsNodeReady`、`RequeueForResendCoreAsync` 置 send=0）。 |
| 重发次数只在真正发送成功 +1；24 次后 send=3 释放 | ✅ `BuildUpdateSetClause("send")`:232 + `MarkExhaustedAsync` + `MarkFailedCoreAsync`（send=3）。requeue 不改 resend_count（`:514-522`）。 |
| `resend_count = resend_count + IF(send_time IS NULL,0,1), send=1, send_time=@Now` 依赖 SET 左→右 | ✅ 语义正确（MySQL 文档规定 SET 左→右），注释到位；脆弱性见 P3-6。 |
| `AllowStandbyDispose` 默认 false/Hot；`ResendIntervalMinutes=5`、`MaxResendCount=24` | ✅ `ConfigShared.cs:93/99/101`，默认值与文档一致。 |
| Aptio 无 ACK（写成功=已投递） | ✅ `AptioSocketClient.cs:53`、`AptioCommandService.cs:16-31` 一致；`SendCoreAsync:336-337` 非 STATUS 返回 `DeliveredMarker`。 |
| `SrmNodeIds` 绑定追加陷阱 | ✅ `Program.cs:177-205` 清空默认列表再绑定 + 显式 `ConfigurationChangeTokenSource`，正确。 |
| Web 丢弃队列页面 | ✅ `ApiEndpoints.cs:60-206` + `app.js:1205-1378` 完整实现。 |

**结论**：本次 Dispose 丢弃链路的窗口/重发/重排队状态机实现**总体正确**，未发现会直接破坏丢弃链路的关键逻辑缺陷；主要风险集中在**外围**（无鉴权 API、Docker 配置、socket 队列、静默写失败）。

---

## 7. Top 10 修复顺序（现场风险 × 成本）

| 序 | 问题 | 严重度 | 本次/历史 | 理由（现场会发生什么） |
|---|---|---|---|---|
| 1 | 无鉴权升级端点 RCE（P0-1） | P0 | 历史遗留 | 局域网任意主机可替换现场二进制并重启，完全接管。 |
| 2 | 管理 API 无鉴权 + 密码下载（P1-1） | P1 | 历史遗留 | flexlab/DMSCN 密码泄露、配置被篡改。 |
| 3 | Docker 配置失效（P1-2） | P1 | 历史遗留 | Docker 形态连错库，容器部署不可用。 |
| 4 | Aptio 队列无界 + 陈旧重放（P1-3） | P1 | 历史遗留 | Aptio 断连恢复后重放陈旧指令（重复丢弃/取消）。 |
| 5 | `UpdateStatusAsync` 静默吞异常（P1-4） | P1 | 历史遗留 | DB 写失败时状态机照常推进 → 重复下发。 |
| 6 | 前端存储型 XSS（P2-1） | P2 | 部分本次 | 条码/异常消息注入 JS。 |
| 7 | DMS 删除无事务（P2-4） | P2 | 历史遗留 | 样本半删除、孤儿数据。 |
| 8 | PitStop 整表清空（P2-3） | P2 | 历史遗留 | 一条卡死清空整表未超时任务。 |
| 9 | `GetUnknownPatientOrdersAsync` 优先级（P2-2） | P2 | 历史遗留（死代码） | 若启用结果集错误。 |
| 10 | 状态机队首阻塞边缘（P2-5） | P2 | 历史遗留 | `SkipOnUnknownNode=false` 时队列卡死。 |

> 「本次改动引入」vs「历史遗留」：经核对，本次（`6d684fa` 相对旧版）的 Dispose 丢弃链路改动**未引入新的 P0/P1**；所有 P0/P1 均为历史遗留（鉴权、Docker 配置、socket 队列设计、DB 写失败静默）。P2 中「前端 XSS」部分与本次 Web 丢弃队列页面（app.js dispose 渲染）相关，属本次新增面。

---

## 8. 测试覆盖缺口与无法验证项

### 8.1 已覆盖（186 用例实测）
- DisposeDatabaseService：队列查询/重发/失败/补查（SQLite 内存，自定义 `if()` 模拟 MySQL IF）。
- DisposeSampleWorker：requeue/resend/window 状态机（NSubstitute）。
- SrmStatusDecoder、WorkTimeChecker、AptioConfigBinding、DisposeSampleConfigDefaults、ExportFileService、AptioCommandService、AptioSocketClient。

### 8.2 缺口（方言/协议/UI）
| 缺口 | 说明 | 谁能补 |
|---|---|---|
| MySQL 方言 | `IF()` 靠 SQLite 自定义函数模拟；`TIMESTAMPDIFF`（`DeleteHistorySql:222`）、`LIMIT @p` 参数化、`information_schema`（`ColumnExistsSql`）、`ALTER TABLE ADD COLUMN` 迁移**未在真实 MySQL 验证**；`resend_count` SET 左→右排序行为未验 | 现场 MySQL（flexlab）集成测试 / 实施方 |
| 真 Aptio 协议 | STATUS 报文解析、COMMENT S002/ORDER 无 ACK 语义、半开连接、断连重放——仅靠 socket 假服务器/单测覆盖，**未连真实 Aptio 2055** | 现场 Aptio（A 档 + 现场工程师） |
| 浏览器 E2E | app.js 无任何浏览器 E2E（XSS、表单保存、重启轮询、升级上传进度） | 前端/QA |
| 配置热更新语义 | `[Restart(Hot/InnerRestart/ProcessRestart)]` 与 worker 直接读文件的双源一致性未测 | 集成测试 |
| DMS 真库 | 多表删除/status 修正/PitStop 依赖 DMSCN 真实 schema，SQLite 未覆盖 | 现场 DMSCN |
| WorkListCleaner | Access .mdb（OleDb）/AccessAgent HTTP 未测 | 现场 Immulite/AccessAgent |
| 目标机部署 | systemd/Windows 服务/升级 rename-replace 未在目标机实测 | 实施方/现场 |

### 8.3 无法在本次验证（需现场）
真 MySQL、真 Aptio、真浏览器、目标机（Windows/Linux systemd/Docker）、AccessAgent、Centralink（ASTM socket）。

---

## 9. 抽样说明

- **全覆盖**：DisposeSample（worker/DB/命令/路由/各 worker）、Common（socket/DB/日志/解码/工作时间/配置模型）、WebAdmin（API/配置/重启/健康/升级/日志）、Program/DI/生命周期、前端 app.js 全文、deploy 全脚本。
- **抽样（未逐行）**：SrmExport 的 `ExportDatabaseService`/`ExportFileService`（短文件已读，逻辑简单）；DMS 四个 worker（62 行标准包装，未逐行）；`docs/**` 多份历史审查报告未采信（按任务要求仅核对其与代码一致性关键点——CONFIG-REFERENCE 与 appsettings.json 对齐）。
- **未审**：`archives/**` 老 Delphi（历史参考，按任务排除）。

---

*报告完。证据以「命令 + 输出片段」或「文件:行号」形式给出；计数（186 用例、32 变更文件、P0/P1/P2/P3 数量）以实测/人工核对为准。*
