# UniFlow v1.0.6.6 改动记录

- 日期：2026-11-07
- 版本：1.0.6.5 → **1.0.6.6**
- 部署包：`UniFlow-1.0.6.6-linux-x64.tar.gz` / `UniFlow-1.0.6.6-win-x64.zip`
- 概述：**安全 / 稳定性 / 正确性修复版，无新功能**。本版关闭了「局域网内任意主机可上传替换二进制」的管理面 RCE、修复了丢弃链路因误判 ACK 而永不推进的协议性死锁、加固了 Aptio socket 客户端的队列/超时/连接语义、修正了 DMS/PitStop 的数据一致性缺陷，并补齐 Docker 配置结构与前端 XSS。

> 完整的问题原理、影响面与逐条证据见 `docs/Fix-Report-2026-11-07.md`（本文件只做发布口径摘要，不整篇复制）。
> **本版尚未在真实 MySQL / 真实 Aptio / Windows Service / Docker 容器上做过端到端验收，必须按第十节「现场验收清单」逐项确认后再投产。**

---

## 一、安全（P0 / P1）

### 1.1 P0-1：局域网 RCE —— 无鉴权升级端点已关闭

**原理**

- `appsettings.json` 默认 `WebAdmin:BindIp=0.0.0.0`，管理面直接暴露在局域网；
- 旧版 `/api/update/upload` 只校验 zip 内是否含 `UniFlow` / `UniFlow.exe`，**无签名、无来源、无鉴权校验**；
- `/api/update/apply` 用 staging 二进制替换运行中的主程序，`/api/restart/process` 触发 `Environment.Exit(1)` 由 supervisor 拉起；
- 复核阶段已在 `/tmp` 副本上**实际复现**：上传伪造二进制 → apply → restart，进程被替换。

**影响面**：同网段任意主机可上传自定义二进制 → 替换现场服务 → 完全接管（数据窃取/篡改实验室指令）。

**修法**：所有破坏性端点统一挂 `AdminAuthFilter`（`code/src/UniFlow/WebAdmin/Services/AdminAuthFilter.cs`）：

| 端点 | 作用 | 1.0.6.6 |
|---|---|---|
| `POST /api/update/upload` | 上传升级包 | **需鉴权** |
| `POST /api/update/apply` | 应用升级包 | **需鉴权** |
| `POST /api/restart` | 重启业务宿主 | **需鉴权** |
| `POST /api/restart/process` | 重启整个进程 | **需鉴权** |
| `PUT /api/config` | 改单项配置 | **需鉴权** |
| `PUT /api/config/batch` | 批量保存配置 | **需鉴权（一次动作一次密码）** |
| `GET /api/config/download` | 下载完整配置 | **需鉴权** |
| `DELETE /api/errors` | 清空错误日志 | **需鉴权** |
| `POST /api/dispose/pending/reset` | 重置丢弃队列 | **需鉴权** |
| `POST /api/dispose/pending/delete` | 删除丢弃记录 | **需鉴权** |

**残余风险**：升级包**仍未做签名校验**，本版只是把「谁能升级」收敛到「知道管理密码的人」。签名列入后续版本。

### 1.2 P1-1：管理 API 鉴权口径（一次性 nonce + SHA-256）

**原理**：旧版 `/api/config/download` 直接返回原文 `appsettings.json`（含数据库密码与管理密码）；`PUT /api/config`、`DELETE /api/errors`、dispose reset/delete 均可未授权调用。

**修法**（`code/src/UniFlow/WebAdmin/Services/AdminAuthService.cs`）：

1. 前端 `GET /api/auth/challenge` 取一次性 nonce（32 字节随机、十六进制小写、**60 秒有效**、**一次性**，成功失败都消费掉）；
2. 前端用纯 JS 计算 `hash = SHA-256(密码 + nonce)`（小写十六进制）；
3. 请求带 `X-Admin-Nonce` / `X-Admin-Hash` 两个头；
4. 后端用同一 nonce 重算哈希并做**常量时间**比较；
5. **一次动作一次密码**：点一次「保存全部」只弹一次密码；多请求动作每个请求取新 nonce。

**批量保存**：`PUT /api/config/batch` 一次性提交全部变更，**全部校验通过才写盘**（一次读盘 + 一次原子写：写临时文件再 `File.Move` 覆盖）；密码类字段填 `***` 或留空表示保持原值。

**配置下载**：`GET /api/config/download` 需鉴权，返回**完整原文**（不再打码，因为下载者已通过密码校验）；页面上的回显仍然打码。

### 1.3 P1-2：配置递归打码（修复核发现的 P0 绕过）

**原理**：旧版只打码**已知路径**的密码，追加 `?path=WebAdmin` / `?path=Aptio:Database` / `?path=DMS` 这类**父节点**参数即可拿到明文口令。

**修法**（`code/src/UniFlow/WebAdmin/Services/ConfigRedactor.cs`）：`GET /api/config` 对**对象 / 数组递归**打码，键名**大小写不敏感**；负向用例覆盖 `?path=webadmin:adminpassword` 等小写写法。

### 1.4 管理密码只能后台改

- `WebAdmin:AdminPassword` 默认 **`Advia1`**（`code/src/UniFlow/Common/Models/ConfigShared.cs:197`）；
- **页面不展示、不可改**，`GET /api/config` 一律返回 `***`，`PUT /api/config` / `PUT /api/config/batch` 均拒绝写入该路径（`RestartLevelResolver.IsProtectedPath`）；
- **只能后台改** `appsettings.json` 的 `WebAdmin:AdminPassword`，改完**热生效**（每次动作实时读盘，无需重启）；
- 置空（`""`）→ 所有破坏性端点返回 **403「未配置管理密码」**。

### 1.5 局域网升级 / 密码泄露面关闭

- 升级链路（upload / apply）已鉴权；
- 配置明文下载已鉴权；
- 前端不再把口令回显到页面，密码字段永不进入 URL 与日志。

> **残余**：明文 HTTP 环境下 nonce/hash 仍可能被同网段嗅探重放（60 秒窗口）。这是「防误改 / 防静默改配置」的轻量闸门，**不等价于 HTTPS 级安全**；需要更强防护请在前面加反向代理 TLS。

---

## 二、Dispose / Aptio：丢弃状态机重构

### 2.1 根因：老版 ACK 死锁（无 ACK 口径）

**原理**

- 厂商手册 `DCAU-H00.816.12.01.02.pdf` §8.59：ACK/NAK 是 **Dream → GUI 通知**，只对应 ADVANCED-CONFIG-CHANGE、CLEAR-TUBES、COMMENT I021~I024 六条 GUI 命令；§9.3 的 COMMENT S002 **没有任何回复定义**；
- 老 Delphi `Main.pas:91-96` 只在收到 `ACK` 时才置 `send=1` 并进 Ack 状态 → 服务端从不回 ACK → **永远停在 Ready**，每 4 秒重发同一条 COMMENT，数据库确认逻辑一次都走不到；
- .NET 初版沿用了 ACK 判定，而 socket 层对非 STATUS 命令一律返回 null → **必然死锁**。

**影响面**：定时丢弃实际不推进；同一 barcode 反复下发，后续样本永远轮不到；`sam_dispose_status.send` 永远为 0，10 天后被历史清理直接删除。现场表现即「每 4 秒一条 DisConnect/COMMENT」。

**决定**：协议以厂商手册为准 —— **写成功 = 已投递**（进入对端 TCP 缓冲区即算发出），丢弃是否真正完成**只认数据库 `t_location` 变 `&3-{node}-%`**。

**修法**：`AptioSocketClient` 非 STATUS 命令返回 `DeliveredMarker="OK:SENT"`；`AptioCommandService` 四个命令方法改为 `delivered != null` 判成功；`DisposeSampleWorker.HandleReadyAsync` 成功即 `send=1` 并进 Ack。

**残余**：写成功只代表进入对端缓冲区，真正的丢没丢仍靠 `&3` 轮询 + 24 次上限兜底。

### 2.2 发送窗口 = 10

- `active`（`send=0` 待发 + `send=1 且 disposed=0` 未确认）`≥ 10` 时不再插入新记录；
- `inFlight`（`send=1 且 disposed=0`）`≥ 10` 时**不发任何指令**（含 STATUS）；
- 存在 `send=0` 时**先发待发**（否则 10 条待发会永远卡在 None 状态）；
- 不在 `RunDays` / `TimeRanges` 时间窗口内时**只查库、不发指令**，等下一个时间窗口；
- 窗口满时**只告警/等待，不丢记录**。

### 2.3 5 分钟批量检查 + 条件重排队（不是直接重发）

- 每 `ResendIntervalMinutes`（默认 **5** 分钟）批量检查一次 `send=1 且未确认` 的记录；
- 命中条件（节点 ON + 状态 G/Y + 错误码在允许列表）→ **放回 `send=0` 队列**，由状态机按窗口一条条下发，**本环节绝不直接下发丢弃命令**；
- 重排队只改 `send=0`，**不改 `send_time`、不增 `resend_count`**；重发次数只在**真正发送成功**时 +1；
- `RecheckPendingDisposalsAsync` 每 60 秒轮转补查（ID 游标），窗口外/夜间也能补确认。

### 2.4 24 次失败上限

单条记录最多重发 `MaxResendCount`（默认 **24**）次；用尽后标记 `send=3`（失败、不再自动重发）并释放窗口。

### 2.5 STB / Pink 测试开关（默认关闭）

- 新增 `Aptio:DisposeSample:AllowStandbyDispose`（默认 `false`，热生效）；
- `false`（默认）= 可丢弃条件仍为 `Mode=="ON" && Status ∈ {G,Y} && 错误码在允许列表`；
- `true` = 额外允许 `Mode=="STB"`（Stand-by）与 `Status=="P"`（Pink）参与丢弃判断；
- **GTO / PA / OFF / FR / SH / EXE 始终不允许**，与本开关无关；
- 业务含义：本开关只管「STB/Pink 是否参与丢弃判断」，**不涉及进样本逻辑**。

### 2.6 `UpdateStatusAsync` 静默失败修复（P1-4）

旧实现 catch 后只 LogError、不返回失败，状态机仍进 Ack、DB 仍 `send=0` → 下一轮重复下发同一实验指令。现改为 `Task<bool>` + 所有调用点处理：`send` 写失败**不进 Ack**（置 Error / degraded）；`disposed` / `checkcount` / `skip` 失败不再静默。

**残余**：命令已投递但 DB 写失败时保持 Ready 会重复下发（协议幂等 + Error 告警），后续可加「内存已投递标记 / DB 重试」。

### 2.7 队首阻塞修复（`SkipOnUnknownNode=false`）

未知节点且 `skip=false` 时，旧代码「attempt anyway」之后仍走 `IsNodeReady`（未知节点恒 false）→ **永久停队首**。现改为：未知节点且 skip=false → 跳过 `IsNodeReady` 直接发送；只有已知节点才检查。

### 2.8 补查游标

`RecheckPendingDisposalsAsync` 使用 ID 游标轮转，避免每次都从头扫描导致长列表下查询退化。

---

## 三、AptioSocketClient 加固

| 项 | 1.0.6.5（问题） | 1.0.6.6（修复后） |
|---|---|---|
| 命令队列 | `Channel.CreateUnbounded`（无界） | **有界 1000**（`FullMode=Wait`） |
| 入队等待 | 无独立上限 | **入队超时 30s**（独立于 `CommandTimeout=1h`）；队列满 → 标记过期并返回 null |
| 陈旧重放 | `SendAsync` 1h 超时后请求仍在 channel，恢复后仍发送（1 小时前的指令被补发 → 可能重复丢弃/取消） | **排队年龄上限 120s**：消费者取到超龄请求直接丢弃不发送（已进入写 socket 阶段的请求无法撤回 —— 明确边界） |
| 连接 | `while(!Connected)` 无限重连，单消费者永久卡死，所有模块排队 | **连接总超时 120s** + **失败冷却 30s**（冷却内毫秒级 `TimeoutException("Aptio connect cooldown")`，不阻塞消费者） |
| `Connected` 语义 | `TcpClient.Connected` 只反映上次 I/O，半开/假死链路被误判在线 | **只在探测成功后置 true**（代际 + writer 判空双保险） |
| 并发连接 | 公开 `ConnectAsync` 与内部发送各自连接 | **单飞**：两者走同一 `_ensureLock` 连接 + 探测流程；慢链路（STATUS 3.5s > 3s 轮询）不再被 worker 拆链 |
| 预连接 | 预算到期即退出 | 预算到期不退出，等冷却后继续重试；端口恢复自动连上 |
| `StopAsync` | 队列/在途请求悬挂 | **结算 pending**（`TrySetResult(null)`），停止后 `SendAsync` 不抛未处理异常 |
| 死代码 | `MarkResendAsync` 未被调用；`failReason` 赋值后未使用 | `MarkResendAsync` **删除**（全仓 0 命中）；`failReason` **并入日志** |

**残余**：`CommandTimeout` 仍为 1h（但单条连接预算 120s 先生效）；真实 Aptio 断链未现场压测。

---

## 四、DMS / PitStop 数据正确性

### 4.1 PitStop 只删超时 id（旧版一条卡死清空整表）

旧 `DeletePitstopRecordsAsync(table)` 删除该表**所有** `flgrunning <> ''` 的行 → 一条卡死任务会把同表其它尚未超时的运行中任务一起清掉，并 `_seen[table].Clear()` 重置计时。现改为：只收集超时 `idpitstop` → `DELETE ... WHERE idpitstop IN @Ids AND flgrunning <> ''`；`_seen[table]` 只移除对应 id；表名走白名单。

### 4.2 DMS 多表删除事务（旧版半删除）

旧 `DeleteSampleAsync` 逐表删除、失败只 `failedTables++`，**无回滚** → 半删除（reqtube 已删、orders 还在）。现改为 `BeginTransaction / Commit / Rollback`：任一表失败即回滚并返回 0；删除编排抽为可测的生产方法（`DeleteSampleWithConnectionAsync`），SQLite 测试真实调用生产事务路径。

> **注意**：MyISAM 等非事务引擎**不参与回滚**（MySQL 限制）——现场 DMS 库应使用 InnoDB。

### 4.3 删除回滚不再发 Aptio 报文

旧 `SampleCleanupService` 无条件 `SendAptioActionAsync` 并清除 `_firstSeenUtc`：删除回滚（返回 0）时 DMS 行还在，但 Aptio 已收到取消/完成报文、计时被清 → 延后重删。现改为：**仅 `affected > 0` 才发 Aptio 报文并移除计时**；`affected == 0` 记 Warning 并保留计时（若样本确实消失，现有 current 清理逻辑会移除）。

**残余**：`affected == 0` 无法区分「回滚」与「本来就没有匹配行」；统一不发报文，属低风险取舍。

---

## 五、前端（WebAdmin UI）

- **XSS**：`escapeHtml` 补齐 `& < > " '`；dispose 表格由内联 `onclick` 拼条码改为 `data-act` / `data-i` + **事件委托**，条码从 `_disposeData` 取原始值；健康卡片 / 错误列表 / 日志列表 / 图表 / 编辑框统一转义。
- **下载鉴权**：下载完整配置遇 **401 重新输入密码后重试一次**；**403 不重试**（避免无谓循环）。
- **上传**：遇 **403 不重传**。
- **取消文案**：统一为「已取消，未执行 / 未保存 / 未下载」。
- **favicon**：内嵌 data URI，消除 404 噪音。
- `refreshAll` 不再预取 dispose pending，减少 DB 不可达时的 console 500 噪音。

---

## 六、部署 / 配置

### 6.1 Docker 配置结构重写（P1-2）

- **原理**：旧 `deploy/appsettings.docker.json` 仍是 `Features.AptioAutoProcess` + 顶层 `Database/Dispose/Export` 的**旧平铺结构**，而当前模型绑定 `Aptio:Database` / `Aptio:DisposeSample` / `Features:Aptio`；`ConfigService.MigrateLegacyKeys` 也不迁移这些平铺键；`UniFlow__Database__*` 是**死变量**（应用默认环境变量源无前缀）。结果：容器启动后连 127.0.0.1 默认库，功能错配，**Docker 形态不可用**。
- **修法**：模板重写为当前结构；compose 环境变量改为**无前缀**写法；build context 修正为仓库根；新增真实 `ConfigurationBuilder` 绑定测试；文档统一无前缀。

**新环境变量名（无 `UniFlow` 前缀）**：

```
Aptio__Database__Host / Port / User / Password / Database
Aptio__Ip / Aptio__Port
Features__Aptio__DisposeSample / SrmExport / Delivery / DeliveryFile / Priority / TestNameDispose
```

### 6.2 SrmExport 精确分钟调度（P3）

旧实现 `now.Minute == target.Minute` + 仅 0 点复位，循环延迟或 DB 慢就会错过当天。现改为 `_lastExportDate` + `now.TimeOfDay >= target` **当天一次**；DB 失败**不标记当天**、可补；重启后可补。

### 6.3 Windows 安装脚本源码路径

`deploy/windows/install-service.ps1` 源码路径由 `..\..\code\src` 修正，并在发布前做 csproj 校验。

### 6.4 单实例保护

新增 `code/src/UniFlow/Common/Services/SingleInstanceGuard.cs`：优先使用 `Global\UniFlow`（跨会话，Windows 服务与控制台互斥）；非管理员账号没有 `SeCreateGlobalPrivilege` → **回退 `Local\UniFlow`**（会话级）；已有实例在跑（`AlreadyRunning`）→ 进程退出；互斥体不可用（`Unavailable`）→ **仅告警并继续**（不因保护机制本身启动失败）。

### 6.5 503 分流

`/api/dispose/pending` 在 DB 不可达时返回 **503 `Database unavailable`**（前端可据此提示「数据库不可达」而不是笼统 500），其它错误仍为 500。

### 6.6 `Environment.TickCount` → `TickCount64`

`code/src/UniFlow/Common/Services/ASTM.cs` 改用 64 位计时器，避免长时间运行后回绕（约 24.9 天）导致计时错误。

---

## 七、测试与构建

| 项 | 结果 | 命令 |
|---|---|---|
| 构建 | **0 Error** / 14 Warning（全量重编 `-t:Rebuild`：10 条既有 nullable/CS 告警 + NU1902/NU1903 两条依赖漏洞告警各重复计入 2 次；增量重建为 4 Warning） | `/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release` |
| 单元 / 集成测试 | **380 / 380 通过**（Failed 0 / Skipped 0） | `/usr/share/dotnet/dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` |
| Playwright（API + UI） | **49 / 49 通过**（API 32 + UI 17） | `run-test/playwright/`（`npm install && npx playwright test`，对 1.0.6.6 代码的冻结副本执行） |
| 前端语法 | OK | `node --check code/src/UniFlow/wwwroot/app.js` |
| 产物自检 | 两包各 3 份 `.md`；共用文档 `cmp` 一致；主程序 / `appsettings.json` / `wwwroot` 齐备 | 见 `docs/Release-Process.md` 第 2 步 |

- 单测规模由 45 → **380**（新增鉴权、配置打码、socket 队列/停止、Dispose 重排队/未知节点、DMS 事务、PitStop 定点删除、单实例、配置绑定等用例）；
- 关键闸门均做过**负向对照**（去掉窗口判断 / 去掉过期与冷却 / 去掉有界队列 / 去掉事务 → 对应用例变红）；
- 独立复核：GLM-5.3-Flash / DeepSeek-Flash 多轮，报告见 `docs_20261103/Review-*.md`。

> **测试环境说明**：Playwright 使用冻结副本 `dotnet publish` + 测试配置（`127.0.0.1:5199`、`AdminPassword=Advia1`、Features 全 false），**不连真实 MySQL / Aptio**。

### 7.1 部署包产物与校验值

| 产物 | 大小 | MD5 | SHA-256 |
|---|---|---|---|
| `release/UniFlow-1.0.6.6-linux-x64.tar.gz` | 46,305,354 B | `5fa3daa8be0b0ae0c54b6bad1e2d74be` | `0d65316a7d10d2bc7c13897c1eb88b2369e56767e0b9a1664a45c09cb87b5d4d` |
| `release/UniFlow-1.0.6.6-win-x64.zip` | 47,134,761 B | `c109ef9237a09038a925e25ceba1d9e4` | `87cdcde21202f2b7f76056479c636fc8c2887bf42cf85b77e5cae0136139d8ab` |

包内结构（顶层目录均为 `uniflow/`）：

- **Linux 包**：`UniFlow`（ELF 64-bit，自包含单文件）、`UniFlow.pdb`、`UniFlow.staticwebassets.endpoints.json`、`appsettings.json`、`wwwroot/`（app.js / index.html / style.css 及 .br/.gz）、`README.md`、`INSTALL.md`、`CONFIG-REFERENCE.md`、`install.sh`；
- **Windows 包**：`UniFlow.exe`（PE32+）、`UniFlow.pdb`、`UniFlow.staticwebassets.endpoints.json`、`web.config`、`appsettings.json`、`wwwroot/`、`README.md`、`INSTALL.md`、`CONFIG-REFERENCE.md`、`install.ps1`、`install-enable.ps1`、`uninstall.ps1`。

**包自检（`docs/Release-Process.md` 第 2 步）**：两包各含 3 份 `.md` ✅；`INSTALL.md` / `CONFIG-REFERENCE.md` 两平台逐字节一致（`cmp` → `INSTALL_OK` / `CONFREF_OK`）✅；`README.md` 为平台专属（内容不同，符合预期）；主程序、`appsettings.json`、`wwwroot` 均在包内 ✅；包内 `appsettings.json` / `wwwroot/app.js` 与仓库同名文件 md5 一致 ✅。

**冒烟（沙箱，仅 Linux 包）**：解压后以 `BindIp=127.0.0.1` / `Port=5197` 启动 → `GET /api/version` 返回 `{"version":"1.0.6.6","informational":"1.0.6.6+6d684fa..."}`；`GET /api/health` 返回 `degraded`（无 MySQL / Aptio，符合预期）；未带 nonce 访问破坏性端点 → **401**；`GET /api/auth/challenge` + `SHA-256(Advia1+nonce)` → **200** 并返回完整配置；同一 nonce 复用 → **401**（一次性生效）。

> 冒烟环境已清理，**未对仓库与交付包做任何额外改动**。

---

## 八、行为变化与升级注意事项（**升级前必读**）

### 8.1 管理密码

| 项 | 说明 |
|---|---|
| 默认密码 | **`Advia1`**（`WebAdmin:AdminPassword`） |
| 能否在页面改 | **不能**。页面不展示、API 也不接受写入该字段 |
| 怎么改 | **后台**改 `appsettings.json` 的 `WebAdmin:AdminPassword`，**热生效**（无需重启） |
| 破坏性动作 | 重启 / 改配置 / 升级 / 删错误 / 删队列 / 下载完整配置 → 需 **nonce + hash** 鉴权 |
| 下载完整配置 | **需鉴权**，返回**完整原文**（含库口令） |
| 密码留空 | 所有破坏性端点 **403「未配置管理密码」** |

> **现场建议**：投产前把默认密码 `Advia1` 改成现场专用口令。

### 8.2 Dispose 默认参数（可热改，按现场需要覆盖）

```json
"Aptio": {
  "DisposeSample": {
    "MaxOnetimeSelectDiscardCount": 10,
    "ResendIntervalMinutes": 5,
    "MaxResendCount": 24,
    "AllowStandbyDispose": false
  }
}
```

| 参数 | 1.0.6.6 默认 | 含义 |
|---|---|---|
| `MaxOnetimeSelectDiscardCount` | **10** | 同时发送窗口；`active ≥ 10` 不再插入新记录，`inFlight ≥ 10` 不发指令 |
| `ResendIntervalMinutes` | **5** | 批量检查 / 重排队间隔（分钟）；0 = 关闭重排队 |
| `MaxResendCount` | **24** | 单条记录重发上限；用尽后标记 `send=3` 失败；0 = 不限 |
| `AllowStandbyDispose` | **false** | 测试开关：是否允许 STB / Pink 参与丢弃判断；**GTO 始终不允许** |

> 四项均为**热生效（Hot）**，页面可改。若现场负载较轻/较重，建议按实际丢弃节奏调整窗口与重发上限。

### 8.3 Docker 环境变量改名

`UniFlow__*` → **去掉 `UniFlow` 前缀**：`Aptio__Database__*` / `Aptio__Ip` / `Aptio__Port` / `Features__Aptio__*`。旧变量名**不再生效**（本来就是死变量），升级 compose 时必须同步改。

### 8.4 单实例

进程级互斥体 `Global\UniFlow`；无权限时自动回退 `Local\UniFlow`。已有实例在跑时新进程**直接退出**。Windows 服务账号若无 `SeCreateGlobalPrivilege`，会退化为会话级互斥（不报错，仅保护范围变小）。

### 8.5 数据库 `sam_dispose_status` 自动加列

启动时检查 `information_schema`，若 `resend_count` 列不存在则执行 `ALTER TABLE sam_dispose_status ADD COLUMN resend_count INT NOT NULL DEFAULT 0`（**幂等**，不改其它列）。

- 若 ALTER 失败（例如账号无 DDL 权限）→ **重发与失败标记功能被禁用**（依赖 `resend_count` 的查询会报 Unknown column），并打 Error 日志；
- 此时相关页面接口可能返回 **503**；
- **升级前请 DBA 确认应用账号具备该表的 ALTER 权限**，或手工先加好列。

### 8.6 `SrmNodeIds` 现在可保存

`Aptio:SrmNodeIds`（SRM 节点号列表）此前因缺少 `[Restart]` 标记被当作「未知路径」而**拒绝写入**；本版补上 `[Restart(RestartLevel.InnerRestart)]`，**页面现在可以保存**。

> **生效方式：需内层重启**（点页面「Restart Service」按钮或 `POST /api/restart`）。进程级重启（systemd / Windows 服务）当然同样生效。

### 8.7 其它行为变化

- **配置下载返回完整原文**（旧版明文、旧 1.0.6.5 行为一致，但本版**需鉴权**才能拿到）；
- **丢弃成功判据变化**：不再等 ACK，改为「写成功 = 已投递」+ 数据库 `t_location` 变 `&3-{node}-%` 确认。旧版因等不到 ACK 而**从不推进**的队列，本版会真正开始发送 —— **升级后请关注冰箱侧收到的 COMMENT 数量是否符合窗口 10 的预期**；
- `send=3` 现在表示「重发次数用尽、失败」，不再是其它含义。

---

## 九、已知问题 / 延后项（**不阻塞本版**）

以下为已知并**明确接受**的残余项，详见 `docs/Fix-Report-2026-11-07.md` §7（决策日志）与 §8（残余风险）：

| 项 | 状态 | 说明 |
|---|---|---|
| 死代码 | 延后 | `GetUnknownPatientOrdersAsync` 的 SQL 优先级无调用方（无生产影响），本轮不扩大 diff |
| 依赖漏洞 | 延后 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（NU1903，high）、`MySqlConnector` 2.4.0（NU1902，moderate）；升级需回归验证，现场无直接暴露面 |
| Windows install 脚本 | 延后 | `install-service.ps1` 在已有 exe 时跳过 publish（F-4），需现场确认是否有意为之；本轮只修源码路径 |
| 日志性能 | 延后 | `UniFlowFileLogger` 全局锁 + 每行 Flush，高日志量现场再评估 |
| 配置双源 | 延后 | worker 直接读 JSON + IOptions 并存，语义不一致风险，改动面大 |
| HTTP 重放 | 接受 | nonce/hash 在明文 HTTP 下仍有 60 秒重放窗口；需更强防护请上 TLS |
| MyISAM | 接受 | MySQL 非事务引擎不参与回滚，现场应使用 InnoDB |
| `send` DB 失败重复下发 | 接受 | 命令已投递但 DB 写失败时保持 Ready；协议幂等 + Error 告警 |
| `SanitizeSqlName` 反引号 / `Decode()` 首节点 | 延后 | 低风险 P3 |
| XSS 引号转义红闸门 | 延后 | 代码已修，但缺独立 Playwright mock 闸门 |

---

## 十、现场验收清单（本版**未做**真实环境 E2E，必须现场确认）

> 以下项目**未在本仓库验证**，需在目标现场逐项执行：

1. **真 MySQL（flexlab / DMSCN）**：`sam_dispose_status` 的 `ALTER TABLE` 迁移是否成功（账号 DDL 权限）、`IF()` / `TIMESTAMPDIFF` / `LIMIT @p` / `information_schema` 兼容性、DMS 事务回滚（InnoDB / MyISAM 差异）、PitStop 定点删除；
2. **真 Aptio 2055**：无 ACK 语义、慢 STATUS（3.5s）、半开连接、断链恢复、重发/重排队、STB/Pink 开关（`AllowStandbyDispose`）；
3. **目标机**：Windows Service / Linux systemd / Docker 容器、升级包 rename-replace、单实例 `Global\UniFlow`（Windows 服务账号权限）；
4. **浏览器 E2E**：现场浏览器再点一遍「保存全部只弹一次密码」「下载」「测试数据库连接」「上传升级包」；
5. **依赖漏洞**：评估 `SQLitePCLRaw` / `MySqlConnector` 升级。

### 升级步骤建议

1. 备份现场 `appsettings.json`（尤其是数据库口令与管理密码）；
2. 停服务 → 覆盖程序文件 → **保留现场 `appsettings.json`**（安装包内的配置是模板，不要覆盖现场配置）；
3. 确认 `appsettings.json` 中 `WebAdmin:AdminPassword` 已改为现场口令；
4. 启动后查看日志，确认 `sam_dispose_status` 加列成功（无 `Ensure sam_dispose_status.resend_count column failed` 告警）；
5. 用丢弃队列页做一次小批量验证，确认冰箱侧收到 COMMENT 的节奏符合窗口 10 的预期；
6. 确认 `/api/dispose/pending` 未返回 503。

---

## 附：改动文件索引（发布相关）

| 文件 | 说明 |
|---|---|
| `code/src/UniFlow/UniFlow.csproj` | `Version` / `AssemblyVersion` / `FileVersion` → `1.0.6.6` |
| `deploy/build.sh` | `VERSION` → `1.0.6.6` |
| `deploy/package.sh` | 用法注释示例 → `1.0.6.6` |
| `docs/CHANGELOG-1.0.6.6.md` | 本文件 |
| `docs/Release-Process.md` | 示例版本号 → `1.0.6.6` |
| `docs/Fix-Report-2026-11-07.md` | 本版修复的原理 / 影响面 / 证据来源 |
| `docs/dispose-state-machine.html` / `.lifecycle.json` | 丢弃状态机图（窗口 / 重排队 / STB） |
