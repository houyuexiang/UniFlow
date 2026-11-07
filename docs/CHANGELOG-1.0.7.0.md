# UniFlow v1.0.7.0 改动记录

- 日期：2026-11-07
- 版本：1.0.6.7 → **1.0.7.0**
- 部署包：`UniFlow-1.0.7.0-linux-x64.tar.gz` / `UniFlow-1.0.7.0-win-x64.zip`
- 概述：**功能版（次版本）**。本版给 SRM 样本丢弃链路加了**可选的「快速填满窗口」**（默认关闭），把「一条一条发、逐条等确认」改为「一个 tick 内连续发到窗口满、确认改批量」；同时修复了 1.0.6.7 的**重排队死锁**（窗口满时重排队永不执行 → 窗口永不释放）。

> 本版**新增 3 个配置字段**（全部热生效），**默认全部关闭 / 保持原值** → 不开启快速填窗时，行为与 1.0.6.7 基本一致。
> ⚠️ **唯一的默认行为变化是「重排队死锁修复」**（不受快速填窗开关控制）—— 见第四节与第八节。
> 需求与原始问题描述见 `docs/Maintenance-Order-2026-11-07.md`（维护单）；独立复核记录见 `docs_20261103/Review-2026-11-07-fastfill.md`。
> **本版尚未在真实 Aptio 2055 / 真实 SRM / 真实冰箱 / 真实 MySQL / Windows Service / Docker 容器上做过端到端验收** —— 第九节「已知问题 / 延后项」与其中的「现场验收清单」必须逐项确认后再投产。

---

## 一、需求与行为：快速填满窗口（可选，默认关闭）

### 1.1 旧行为（1.0.6.7）为什么慢

1.0.6.7 的丢弃状态机是 `None → Ready → Ack → Dispose` 的**串行逐条**推进：

| 状态 | 动作 | 耗时 |
|---|---|---|
| `Ready` | 发 1 次 `STATUS-REQUEST` + 1 条 `COMMENT S002 … TRASH` → 转 `Ack` | 1 个 tick |
| `Ack` | 查 `t_sample.t_location` 是否已变成 `&3-{node}-%` → 命中转 `Dispose`；未命中则 `checkcount++`，最多等 `MaxWaitDiscardCount`（默认 2）轮 | 1~3 个 tick |
| `Dispose` | 再发 1 次 `STATUS-REQUEST` → 转 `None` | 1 个 tick |

- **每条记录至少 3 个 tick**（`LoopIntervalSeconds=3` 秒）→ 至少 **9 秒**；等确认时最多 5 个 tick → 最多 **15 秒**；
- **10 条记录 ≈ 90 ~ 150 秒**，且**串行**：第 2 条必须等第 1 条确认（或放弃）才开始。

### 1.2 新行为（`EnableFastFill=true`）

| 项 | 1.0.6.7 | **1.0.7.0（`EnableFastFill=true`）** |
|---|---|---|
| 发送节奏 | 一条一条发，**每条都等确认** | **一个 tick 内连续发** `COMMENT` 直到发送窗口满（窗口上限仍是 `MaxOnetimeSelectDiscardCount`，默认 **10**） |
| 每条间隔 | 无（等确认） | `FastFillIntervalMs`（默认 **1000ms**，**代码硬钳 0~5000ms**） |
| 确认方式 | 逐条查（`Ack` 状态） | **批量**：每 `FastFillConfirmIntervalSeconds`（默认 **5** 秒）一条 SQL 确认全部在途记录 |
| 状态机 | `Ready → Ack → Dispose` | **保持 `Ready`**，**不进入 `Ack`**（队列取空才转 `None`） |
| 窗口上限 | 10 | **仍是 10**（不会无限发） |

实现位置：`code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:716`（分支判定）、`:811`（`HandleReadyFastFillAsync`）。

> `FastFillIntervalMs = 0` 只表示「两条 `COMMENT` 之间**不等待**」，**不是关闭快速填窗** —— 开关**只有** `EnableFastFill` 一个。

### 1.3 逐条判定口径不变

快速填窗只改「发完一条是否等确认」，**每条记录的判定逻辑与 1.0.6.7 逐条路径一致**：未知节点按 `SkipOnUnknownNode` 处理、节点未就绪（`OFF` / `STB` / `GTO` / `PA` 等）等待、错误码白名单、`AllowStandbyDispose` 语义全部不变。

---

## 二、批量确认

**位置**：`DisposeSampleWorker.cs:264`（`MaybeFastConfirmAsync`）、`:223-225`（主循环调用点）、`:266`（开关闸门）

- 触发：**仅 `EnableFastFill=true`**。开关关闭 → 方法首行 `if (!_dc.EnableFastFill) return 0;`（在任何 `await` 之前）→ **不查库**，行为与 1.0.6.7 完全一致；
- 频率：每 `FastFillConfirmIntervalSeconds`（默认 **5** 秒，**代码硬钳 1~60 秒**）才真正查一次库（节流）；
- 口径：**一条 SQL** —— `RecheckPendingDisposalsAsync(200, 0)`，一次性覆盖所有 `send=1 & disposed=0` 的在途记录；
- 命中判定：位置已变为 `&3-{node}-%` → 置 `disposed=1`（复用既有 SQL：`UPDATE sam_dispose_status SET disposed=1, disposed_time=@Now WHERE barcode IN @Hits AND send=1 AND disposed=0`，`DisposeDatabaseService.cs:354`）→ **记录退出在途计数 → 发送窗口释放** → 下一轮可继续补发；
- **不推进** 60s 维护补查的轮转游标 `_recheckCursor`（那套是「按 ID 分批轮转全表」，本机制要的是「一次覆盖全部在途」，`max=200` 已足够覆盖窗口 10）；
- **实际延迟**：默认配置 5 秒，但实际受 `LoopIntervalSeconds`（默认 3 秒）影响 → **约 5 ~ 8 秒**。

日志关键字：`Fast-fill confirm: N in-flight disposal(s) confirmed`。

---

## 三、失败 / 边界处理

以下任一情况 → **立即停止本轮**（不再继续发下一条），**队列顺序保持不变**（不跳过队首），下一轮重试：

| 场景 | 处理 | 证据（单测） |
|---|---|---|
| `COMMENT` 投递失败 | `break`，记 Warning | `FastFill_ThirdNotDelivered_StopsAfterTwoSuccesses` |
| `UpdateStatus(barcode,"send")` 失败 | `break`，记 Error + 健康 degraded（命令**已投递**但 DB 仍是 `send=0`，下一轮可能重发同一条） | `FastFill_SendStatusUpdateFails_StopsAndReportsFailure` |
| 节点未就绪 | `break`，**保持队首顺序**（不跳过，否则打乱顺序） | `FastFill_NodeNotReady_SendsNothing_StaysReady` |
| 空条码 | `break`（否则会反复取到同一条 → 死循环） | `FastFill_EmptyBarcode_StopsWithoutSending` |
| 未知节点 `skip` 写入失败 | `break`，记 Error + 健康 degraded（记录会卡在队首，队列可能被阻塞） | `FastFill_SkipUpdateFails_StopsLoopAndReports` |
| `STATUS` 探测失败 | `break`，本轮不再取记录 | `FastFill_StatusCheckFails_StopsBeforeSelectingRecord` |

**未知节点 skip 成功 → 继续下一条**（不是停止）：skip 会推进队首（标记 `send=2` sentinel），否则该记录永远停在队首阻塞后续所有记录。

> **单轮上限 200 条**（`FastFillMaxSkipsPerTick`，`DisposeSampleWorker.cs:81`）：节点号配错（如 `18` vs `018`）时队首会连续是未知节点，无上限会让**一个 tick 遍历整个待发队列**（每条 1 次 `STATUS` 往返 + 间隔）→ 长时间占用 tick，期间健康心跳 / 补查 / 重排队全部停摆。达到上限 → 打 Warning「本轮跳过 N 条后暂停」并结束本轮，下一轮继续。
> 单测：`FastFill_UnknownNodeSkip_StopsAfterMaxSkipsPerTick`、`FastFill_UnknownNodeSkip_ContinuesBelowLimit`。

**`MaxOnetimeSelectDiscardCount <= 0`（窗口不限）→ 不走快速填窗**，回退 1.0.6.7 的单条路径（一条一条发、逐条确认仍生效，`MaxWaitDiscardCount` 继续有效）。
> 实现：`DisposeSampleWorker.cs:716` 的 `if (_dc.EnableFastFill && window > 0)`；`Ack` 兜底同条件 `:938`。
> 单测：`FastFill_WindowUnlimited_KeepsSingleRecordPath`、`Ack_FastFillEnabledWindowZero_KeepsPerRecordConfirm`、`Ack_FastFillEnabledWindowPositive_SkipsPerRecordConfirm`。

---

## 四、重排队死锁修复（**默认现场也生效**）

**位置**：`DisposeSampleWorker.cs:375-376`（窗口满不再提前返回）、`:435`（日志）

### 4.1 1.0.6.7 的死锁

```
inFlight >= MaxOnetimeSelectDiscardCount  →  return 0（连 STATUS-REQUEST 都不发）
```

- 重排队是**释放窗口的唯一手段**（把 `send=1` 的记录放回 `send=0`）；
- 但窗口满时重排队**直接返回** → **窗口里那 10 条一直未确认时，重排队永远不执行** → 窗口**永不释放** → **死锁**（丢弃链路彻底停摆，只能人工干预）。

### 4.2 1.0.7.0 的修复

| 项 | 1.0.6.7 | **1.0.7.0** |
|---|---|---|
| 窗口满 + **有到期候选** | `return 0`，不发任何指令 | **仍然执行本轮重排队**：发 **1 条** `STATUS-REQUEST` → 节点满足条件 → 记录放回 `send=0` → **窗口释放** |
| 窗口满 + **无到期候选** | `return 0`，不发任何指令 | 依旧**不发任何指令**（保持「没有候选不发 STATUS」的原行为） |
| 重排队是否直接下发丢弃命令 | 否 | **仍然否** —— 只放回 `send=0`，真正发送仍由状态机按窗口一条条进行 |

单测：`Requeue_WindowFull_StillRequeuesEligibleRecords`（有候选 → 发 1 条 STATUS + requeue 1 条）、`Requeue_WindowFull_NoCandidates_DoesNotSendStatus`（无候选 → 一条指令都不发）。

### 4.3 ⚠️ 该修复**不受 `EnableFastFill` 控制** —— 默认现场行为会变化

**即使 `EnableFastFill=false`（默认），重排队死锁修复也生效。**

- 窗口满（10 条在途且 5 分钟未确认）时，现在会发 1 条 `STATUS-REQUEST` 并把到期候选放回 `send=0` → **立即重发**（**重复 `COMMENT`**，在 `MaxResendCount=24` 次上限内）；
- 这**可能比 1.0.6.7 更早重发未确认记录**。1.0.6.7 在这个场景下是「卡死」，所以严格说「更早重发」是把「永不重发」变成「按 5 分钟周期重发」；
- **现场需在测试环境验证「慢冰箱 / 节点未就绪」场景下重复 `COMMENT` 是否可接受**；
- 如果现场不接受重复 `COMMENT`，唯一的规避手段是**不要让窗口长期停在满状态**（保证 `&3-{node}-%` 的确认能及时回写）。

> 文档同步：`docs/CONFIG-REFERENCE.md` §3 / §6、`docs/Configuration-Manual.md`「快速填窗」节、`docs/dispose-state-machine.html` / `.lifecycle.json` 均已改为新语义。

---

## 五、配置

### 5.1 三个新字段（**全部热生效 `Hot`**）

| 路径 | 类型 | 默认值 | 范围 | 说明 |
|---|---|---|---|---|
| `Aptio:DisposeSample:EnableFastFill` | bool | **`false`** | — | 快速填窗总开关。**默认关闭** |
| `Aptio:DisposeSample:FastFillIntervalMs` | int | **`1000`** | **`0~5000`**（代码硬钳） | 每条 `COMMENT` 之间的间隔（毫秒）；`0` = 不等待（连发）。**`0` 不是关闭开关** |
| `Aptio:DisposeSample:FastFillConfirmIntervalSeconds` | int | **`5`** | **`1~60`**（代码硬钳） | 批量确认间隔（秒）：每该秒一条 SQL 确认所有在途记录 |

代码位置：`code/src/UniFlow/Common/Models/ConfigShared.cs:93` / `:95` / `:98`。

### 5.2 范围由**代码硬钳制**（页面 `max` 只是输入提示）

| 字段 | 钳制函数 | 越界行为 |
|---|---|---|
| `FastFillIntervalMs` | `ClampFastFillIntervalMs`（`DisposeSampleWorker.cs:90`） | `<0` → `0`；`>5000` → `5000`。**上限 5000ms 是为了防止单 tick 长时间阻塞**（写成 600000 会让一个 tick 卡约 100 分钟 → 健康心跳 stale） |
| `FastFillConfirmIntervalSeconds` | `ClampFastFillConfirmIntervalSeconds`（`:96`） | `<1` → `1`；`>60` → `60` |

> **直接改 `appsettings.json` 超范围也会被代码钳制** —— 页面上的 `max=5000` / `max=60` 只是输入提示，不是唯一的防线。

### 5.3 配置入口

- **页面**：功能配置 → **「SRM 样本批量丢弃」** 分组可见这 3 个字段（`wwwroot/app.js:713-715`）；
- **文件**：`appsettings.json` 也可直接改（热生效，**不需要重启**）；
- **仓库默认 `appsettings.json` 不含这 3 个键** → 不写即取代码默认值（`false` / `1000` / `5`）。

---

## 六、前端 / 文档

| 项 | 说明 |
|---|---|
| 表单字段 | `wwwroot/app.js:713-715` 新增 3 项：`EnableFastFill`（**switch**）、`FastFillIntervalMs`（number，`max=5000`）、`FastFillConfirmIntervalSeconds`（number，`max=60`），均在「SRM 样本批量丢弃」分组 |
| `EXAMPLES` 提示 | `wwwroot/app.js:633` 的 `EXAMPLES` 增加 `EnableFastFill` / `FastFillIntervalMs` / `FastFillConfirmIntervalSeconds` 三条示例 |
| `docs/CONFIG-REFERENCE.md` | 新增 3 行字段表；§3「发送闸门」补充快速填窗与范围硬钳说明；§6「重排队」改为新语义并加「死锁修复对默认现场同样生效」的警示 |
| `docs/Configuration-Manual.md` | 新增「快速填窗（可选，默认关闭）」整节：配置片段、字段表、开启后 6 条行为、风险提示 |
| `docs/dispose-state-machine.html` / `.lifecycle.json` | 「在途窗口满」文案由「不发 COMMENT/STATUS」改为「**不发 COMMENT（重排队可发 1 条 STATUS）**」；图注补充「5 分钟重排队仍可发 1 条 STATUS 并把到期记录放回 `send=0` 以释放窗口」与「可选快速填窗」条目 |

---

## 七、测试与构建

| 项 | 1.0.6.7 | **1.0.7.0** | 命令 |
|---|---|---|---|
| 构建 | 0 Error / 14 Warning | **0 Error / 14 Warning** | `/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release` |
| 单元 / 集成测试 | 446 / 446 | **473 / 473**（Failed 0 / Skipped 0） | `/usr/share/dotnet/dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` |
| Playwright（API + UI） | 55 / 55（API 32 + UI 23） | **56 / 56**（API 32 + UI 24） | `run-test/playwright/`（`npx playwright test`，对冻结副本执行） |
| 前端语法 | OK | **OK** | `node --check code/src/UniFlow/wwwroot/app.js`（node v24.16.0） |

> 14 条 Warning 全部是**既有**告警，与本版改动无关：`ConfigService.cs` 10 条 CS（nullable / CS0414）+ `ImmuliteCleaner.cs` 2 条 CS8620 + NU1902（`MySqlConnector` 2.4.0）×2 + NU1903（`SQLitePCLRaw.lib.e_sqlite3` 2.1.11）×2。

### 7.1 测试规模变化（446 → 473，**+27**）

| 文件 | 状态 | 用例数 | 覆盖点 |
|---|---|---|---|
| `code/tests/UniFlow.Tests/DisposeSample/Workers/DisposeSampleWorkerFastFillTests.cs` | **新增** | **26** | 默认关闭发 1 条进 Ack / 配置默认 `false` / 一个 tick 发到窗口满且保持 Ready / 第 3 条投递失败即停 / `send` 写入失败即停 + 健康 degraded / 节点未就绪不发 / 未知节点 skip 推进队首并继续发 / 窗口不限回退单条路径 / 批量确认关闭时 0 次查库 / 批量确认按间隔节流且不推进游标 / 窗口满仍重排队 / 窗口满无候选不发 STATUS / 配置默认 `false`·`1000`·`5` / 配置路径热生效 / 钳制函数生效 / 600000ms 被钳到 5 秒 / 确认间隔 0 钳到 1 秒 / 999 钳到 60 秒 / skip 达 200 上限暂停 / 未达上限继续 / 窗口≤0 逐条确认仍在 / 窗口>0 跳过逐条确认 / 空条码停止 / STATUS 失败不取记录 / skip 写入失败停止 / 取消时抛 `OperationCanceledException` |
| `code/tests/UniFlow.Tests/DisposeSample/Workers/DisposeSampleWorkerRequeueTests.cs` | 扩写 | **+1**（1 改 2 增） | `Requeue_WindowFull_SkipsStatusAndRequeue` → `Requeue_WindowFull_StillRequeuesEligibleRecords`（死锁修复正向）+ 新增 `Requeue_WindowFull_NoCandidates_DoesNotSendStatus`（无候选仍不发 STATUS） |
| `run-test/playwright/ui.spec.js` | 扩写 | Playwright **55 → 56（+1）** | 「快速填窗 3 个配置项可渲染/可保存」：switch 渲染 + 默认未勾选、两个 number 的 `max=5000`/`max=60`、hint 文案、勾选改值后「保存全部」**恰弹一次密码** + **恰一次** `PUT /api/config/batch` 且请求体含 3 个新键 |

> 另有 `docs/CONFIG-REFERENCE.md`、`docs/Configuration-Manual.md`、`docs/dispose-state-machine.html` / `.lifecycle.json` 随实现同步更新（见第六节）。
>
> **测试环境说明**：Playwright 使用**冻结副本** `dotnet publish` + 测试配置（`127.0.0.1:5199`、`AdminPassword=Advia1`、Features 全 false），**不连真实 MySQL / Aptio**。
> **未做的环境级 E2E**：真 MySQL / 真 Aptio / 真 SRM / 真冰箱 / Windows Service / Docker —— 见第九节。

### 7.2 部署包产物与校验值

| 产物 | 大小 | MD5 | SHA-256 |
|---|---|---|---|
| `release/UniFlow-1.0.7.0-linux-x64.tar.gz` | 46,312,535 B | `c7e6e3eb98f175c8f481881c6c7b85ed` | `b5ee787557dc590db9f7df8845368223c5bd3d39e737683eb5c1ffcdf47acde6` |
| `release/UniFlow-1.0.7.0-win-x64.zip` | 47,140,272 B | `cbafe75d56dca880690f1fa5b55342d4` | `f4bd7e4b95c932321ffae66cce6ecf4443ddbd97874a315d922b7977e4295666` |

**包自检（`docs/Release-Process.md` 第 2 步）**

| 检查项 | 结果 |
|---|---|
| 两包各 3 份 `.md`（README / INSTALL / CONFIG-REFERENCE） | ✅ Linux：`uniflow/README.md`、`uniflow/INSTALL.md`、`uniflow/CONFIG-REFERENCE.md`；Windows 同 |
| `INSTALL.md` 两平台逐字节一致 | ✅ `cmp` → `INSTALL_OK` |
| `CONFIG-REFERENCE.md` 两平台逐字节一致 | ✅ `cmp` → `CONFREF_OK` |
| 主程序在包内 | ✅ Linux `uniflow/UniFlow`（ELF 64-bit LSB pie executable, x86-64）；Windows `uniflow/UniFlow.exe`（PE32+ console, x86-64） |
| `appsettings.json` 在包内 | ✅ 两平台均与仓库同名文件 md5 一致（`6be582a5ea8bb7fc384276f683a27344`） |
| `wwwroot/` 在包内 | ✅ `app.js` / `index.html` / `style.css`（含 `.br`/`.gz`）；`app.js` 与仓库同名文件 md5 一致（`c396bfaa60a5543ead23afa3e3ad326a`） |
| README 平台专属（内容不同，符合预期） | ✅ Linux 2,118 B / Windows 1,731 B |

**版本冒烟（沙箱，仅 Linux 包）**

- 解包 → 以 `BindIp=127.0.0.1` / `Port=5197` 启动 → `GET /api/version` 返回 **`{"version":"1.0.7.0","informational":"1.0.7.0+b68994b49fcad59c2fd4decb3cfb166c7089c253"}`** ✅
- `GET /api/health` → `degraded`（无 MySQL / Aptio，模块 `stopped` / `degraded`，符合预期）
- 无鉴权访问破坏性端点 → **401**（`PUT /api/config`、`PUT /api/config/batch`、`GET /api/config/download`、`DELETE /api/errors`、`POST /api/restart` 全部 401）✅
- `GET /api/auth/challenge` + `SHA-256(Advia1+nonce)` → **200**；**同一 nonce 复用 → 401**（一次性生效）✅
- 3 个新配置字段经 `PUT /api/config/batch` 写入 → **200**，`restart.level = "Hot"`，落盘值 `EnableFastFill=true / FastFillIntervalMs=1000 / FastFillConfirmIntervalSeconds=5` ✅
- Windows 包：扫描 `UniFlow.exe` 内嵌版本资源 → 含 **`1.0.7.0`** ✅（**未在 Windows 上实机运行**）

> 冒烟环境已清理（沙箱端口已关闭、临时目录已删），**未对仓库与交付包做任何额外改动**。

### 7.3 独立复核

`docs_20261103/Review-2026-11-07-fastfill.md`（L3 critic，独立会话）结论为 **PASS-with-findings（有条件放行）**，复核方自行重跑构建/单测并做了 4 组**负向对照**（破坏开关 / 破坏窗口停止 / 恢复重排队提前 return / 破坏批量确认开关 → 对应用例全部变红），确认闸门「能红」。
复核提出的 P2-1~P2-4（范围 clamp、skip 迭代上限、窗口≤0 时确认口径、状态机图旧文案）**已在本版实现中全部落实**；P2-5（重排队行为变更不受开关控制）**不改代码**，转为现场验证项（见第四节与第八节）。

---

## 八、升级注意（**升级前必读**）

### 8.1 默认关闭 → 行为与 1.0.6.7 基本一致

- 升级后**不开** `EnableFastFill` → 丢弃链路的行为与 1.0.6.7 **基本一致**；
- ⚠️ **唯一的默认行为变化是「重排队死锁修复」**（第四节）—— 它**不受** `EnableFastFill` 控制，**现场必须先在测试环境验证**「慢冰箱 / 节点未就绪」场景下的重复 `COMMENT` 是否可接受。

### 8.2 开启快速填窗

1. **开启方式（二选一）**：
   - 页面：功能配置 → 「SRM 样本批量丢弃」→ 勾选 **「快速填窗」**（`EnableFastFill`）；
   - 文件：`appsettings.json` 加 `"EnableFastFill": true`（**热生效，不需要重启**）。
2. **建议先用 1000ms 间隔**（默认值）观察日志：
   - `Fast-fill sent N record(s), inFlight=M/10` → 一个 tick 发了 N 条；
   - `Fast-fill confirm: N in-flight disposal(s) confirmed` → 批量确认命中 N 条。
3. **窗口上限仍是 10**（`MaxOnetimeSelectDiscardCount`）→ **不会无限发**；
4. **出现问题立即把 `EnableFastFill` 改回 `false`** → 热生效，立即恢复 1.0.6.7 的逐条行为。

### 8.3 配置结构变化

- **新增 3 个键**，其余 JSON 键名、层级、默认值全部**不变**；
- 仓库默认 `appsettings.json` **不含**这 3 个键 → 现场 `appsettings.json` **不需要**改；
- 现场**若**要加这 3 个键，注意 `FastFillIntervalMs=0` **只是不等待，不是关闭开关**；
- 页面保存时走 `PUT /api/config/batch`，会把这 3 个键一起写入（**勾选后保存 = 落盘为 `true`**）。

### 8.4 范围硬钳（改 JSON 也会被钳）

| 字段 | 生效范围 | 越界处理 |
|---|---|---|
| `FastFillIntervalMs` | `0 ~ 5000` | `<0` → 0；`>5000` → 5000 |
| `FastFillConfirmIntervalSeconds` | `1 ~ 60` | `<1` → 1；`>60` → 60 |

### 8.5 其它

- 1.0.6.7 的全部安全加固（管理面鉴权、配置打码、上传升级鉴权、XSS 闸门）**继续有效**，本版未削弱；
- 1.0.6.6/1.0.6.7 的配置单源（`IOptionsMonitor`）、SQL 名称白名单、日志按文件独立锁等**全部原样保留**；
- 本版**未改动** DMS 下单链路、SrmExport、递送 / 优先级 / 批量扫码链路的任何业务判定。

---

## 九、已知问题 / 延后项（**不阻塞本版**）

| 项 | 状态 | 说明 |
|---|---|---|
| **真实环境 E2E 未做** | ⚠️ **必须现场验证** | **未在真实 Aptio 2055 / 真实 SRM / 真实冰箱 / 真实 MySQL 上验证过任何本版行为**。以下三点尤其需要现场确认：① 连续 10 条 `COMMENT` 的 **Host LIS / 冰箱承受度**（是否被接受、是否互相干扰）；② **慢 `STATUS`** 响应下的实际 tick 时长（`STATUS` 往返 ~3.5 秒 × 10 条 → 一个 tick 可能 35 秒以上，期间健康心跳 / 补查 / 重排队不执行）；③ **批量确认的实际延迟**（默认 5 秒 + `LoopIntervalSeconds` 3 秒 → 约 5~8 秒，实测值需现场确认） |
| **M-09** `send` 已投递但 DB 写失败时重复下发 | 延后 | 命令已写入 socket，但 DB `send=1` 失败 → 保持 Ready，下一轮可能重发同一条 `COMMENT`。协议幂等（TRASH）通常无害；极端 DB 故障时重复指令 / 日志。**本版未改**（快速填窗放大了触发概率）。 |
| **M-10** 依赖漏洞升级 | 延后 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（NU1903，high）、`MySqlConnector` 2.4.0（NU1902，moderate）。当前使用面为本地 admin SQLite / 内网 MySQL，无直接可利用入口；升级需单独维护窗口 + 全量回归，**不与业务改动混提**。**本版未改**。 |
| 批量确认固定 `max=200` | 接受 | `MaxOnetimeSelectDiscardCount` 若被设成 > 200，超出 200 的在途尾部只能等 60 秒轮转补查兜底（默认窗口 10 无影响）。 |
| 批量确认空闲也查库 | 接受 | `EnableFastFill=true` 时即使没有在途记录，每 5 秒仍查一次库（12 次 / 分钟）。窗口 10 的量级下开销可忽略。 |
| `UpdateStatusAsync` 影响 0 行仍返回 `true` | 接受 | **既有**弱点，非本轮引入；当前 DDL 下 `barcode` 非空 → 不可达，仅记录。 |

### 现场验收清单（本版**未做**真实环境 E2E，必须现场确认）

1. **快速填窗（开）**：测试环境打开 `EnableFastFill`，观察 `Fast-fill sent N` / `Fast-fill confirm: N` 日志，确认冰箱侧收到连续 `COMMENT` 且正常丢弃；
2. **重排队死锁修复（默认即生效）**：构造「窗口 10 条长期未确认」场景，确认窗口**能被释放**（不再死锁），并确认由此产生的**重复 `COMMENT` 可接受**；
3. **慢冰箱 / 节点未就绪**：`STATUS` 慢响应时一个 tick 的实际时长、页面「在途 / 待确认」计数短时间偏高的可接受度；
4. **真 MySQL（flexlab / DMSCN）**：`sam_dispose_status` 的 `ALTER TABLE` 迁移、`RecheckPendingDisposalsAsync(200, 0)` 的实际耗时与锁竞争；
5. **真 Aptio 2055 / SRM**：连续 `COMMENT` 的接受度、无 ACK 语义下的确认口径；
6. **Windows / Docker**：Windows 服务升级路径、Docker `Features__*` 开关与卷 / 日志路径。

### 升级步骤建议

1. 备份现场 `appsettings.json`；
2. 停服务 → 覆盖程序文件 → **保留现场 `appsettings.json`**（安装包内的配置是模板，不要覆盖现场配置）；
3. 启动后查看日志，确认无 `Worker error`；
4. **先不打开 `EnableFastFill`**，按 8.1 确认默认行为（尤其是重排队重复 `COMMENT`）；
5. 在测试环境打开 `EnableFastFill`（建议 `FastFillIntervalMs=1000`），按上面「现场验收清单」第 1、3 条观察；
6. 确认无误后再决定是否在生产开启。

---

## 附：改动文件索引（发布相关）

| 文件 | 说明 |
|---|---|
| `code/src/UniFlow/UniFlow.csproj:5-7` | `Version` / `AssemblyVersion` / `FileVersion` → `1.0.7.0` |
| `deploy/build.sh:7` | `VERSION` → `1.0.7.0` |
| `deploy/package.sh:6` | 用法注释示例 → `1.0.7.0` |
| `docs/Release-Process.md:39,69,87` | 可复制即用的示例版本号 → `1.0.7.0`（历史叙述未改） |
| `docs/CHANGELOG-1.0.7.0.md` | 本文件 |
| `code/src/UniFlow/Common/Models/ConfigShared.cs:93,95,98` | 3 个新配置字段（`Hot`）+ 语义注释 |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:90,96` | 范围钳制函数（`ClampFastFillIntervalMs` / `ClampFastFillConfirmIntervalSeconds`） |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:81` | `FastFillMaxSkipsPerTick = 200` |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:223-225,264-281` | 批量确认（`MaybeFastConfirmAsync`） |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:375-376,435` | **重排队死锁修复**（窗口满不再提前返回） |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:716-719,811-908` | 快速填窗分支 + `HandleReadyFastFillAsync` |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs:938-942` | `Ack` 兜底（窗口≤0 时逐条确认仍生效） |
| `code/src/UniFlow/wwwroot/app.js:633,713-715` | `EXAMPLES` 提示 + 3 个表单字段 |
| `code/tests/UniFlow.Tests/DisposeSample/Workers/DisposeSampleWorkerFastFillTests.cs` | **新增** 26 条闸门 |
| `code/tests/UniFlow.Tests/DisposeSample/Workers/DisposeSampleWorkerRequeueTests.cs` | 重排队死锁修复的 2 条用例（1 改 1 增） |
| `run-test/playwright/ui.spec.js` | 快速填窗 3 个配置项的渲染 / 保存用例（+1） |
| `docs/CONFIG-REFERENCE.md` / `docs/Configuration-Manual.md` | 字段表 + 快速填窗节 + 重排队新语义 + 范围硬钳 |
| `docs/dispose-state-machine.html` / `.lifecycle.json` | 状态机图文案更新（窗口满不发 COMMENT、重排队可发 1 条 STATUS、快速填窗可选） |
| `docs/Maintenance-Order-2026-11-07.md` | 维护单（本版实现依据；**由主会话维护，本版未改**） |
| `docs_20261103/Review-2026-11-07-fastfill.md` | L3 独立复核报告（PASS-with-findings；P2-1~P2-4 已落实、P2-5 转现场验证） |
