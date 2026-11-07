# UniFlow v1.0.6.7 改动记录

- 日期：2026-11-07
- 版本：1.0.6.6 → **1.0.6.7**
- 部署包：`UniFlow-1.0.6.7-linux-x64.tar.gz` / `UniFlow-1.0.6.7-win-x64.zip`
- 概述：**维护版，无新功能**。本版关闭了 1.0.6.6 遗留的 8 个维护单代码项（M-01~M-08）：清理死代码、配置单源、SQL 名称白名单、SRM 状态判定、日志并发、Windows 安装脚本安全、回滚告警节流，并补齐前端 XSS 红闸门。

> 本版**不引入任何新功能、不改变配置结构**；主要影响面是**升级注意事项**（见第十节）。
> 逐条原理与原始问题描述见 `docs/Maintenance-Order-2026-11-07.md`（维护单）；独立复核记录见 `docs_20261103/Review-2026-11-07-M01-M08.md`。
> **本版尚未在真实 MySQL / 真实 Aptio / Windows Service / Docker 容器上做过端到端验收** —— 第十一节「已知问题 / 延后项」与其下的「现场验收清单」必须逐项确认后再投产。

---

## 一、M-01：删除死代码 `GetUnknownPatientOrdersAsync`

**位置**：`code/src/UniFlow/DMSAutoOrder/Services/DmsDatabaseService.cs:102`

**原理**

- 该方法的 SQL 把 `AND` / `OR` 混写且**没有括号**：`WHERE (子查询 IN ...) AND (子查询 IN ...) OR r.codoid NOT IN (子查询)`；
- SQL 运算符优先级下，实际语义是 `(A AND B) OR C` —— `codoid NOT IN (...)` 单独成为一个 OR 分支，会把所有**无订单**的 `reqtube` 全量纳入结果集；
- 全仓 **0 调用方**（旧 DMS 自动下单遗留死代码），生产无影响，但保留只会招致未来误用。

**改法**：整方法删除，原位留注释说明「为什么删 / 将来若要恢复该查询必须显式加括号并补 SQL 语义测试」。

**闸门**：`code/tests/UniFlow.Tests/DMSAutoOrder/Services/DmsDatabaseServiceDeadCodeTests.cs` —— ① 该方法不再暴露在公开 API 上；② 全仓源码 **0 调用方**（源码文本扫描，非人工断言）。

---

## 二、M-02 / D3：Windows 安装脚本 —— 默认始终 publish + 失败闸门

**位置**：`deploy/windows/install-service.ps1`

**原理（两个叠加缺陷）**

1. 旧 `install` 分支是 `if (-not (Test-Path $exePath)) { Publish-Service }` —— **目标 exe 已存在就跳过 publish** → 现场升级时装上去的仍是**旧二进制**；
2. `Publish-Service` **不检查 `$LASTEXITCODE`** —— `dotnet publish` 失败时旧 exe 仍在，`Test-Path $exePath` 通过 → 脚本继续 `New-Service` 把旧二进制装成服务，还打印 "Published … version" 看起来像成功（复核项 D3，与缺陷 1 同类）。

**改法**

| 项 | 1.0.6.7 |
|---|---|
| 默认行为 | **始终 publish**（即使 exe 已存在）；`install-service.ps1:127` 的 `else` 分支无条件调用 `Publish-Service` |
| 逃生口 | 新增 `-SkipPublish` 开关（`install-service.ps1:6`）；跳过时打印**版本警告**（二进制 FileVersion vs csproj `<Version>`，不一致时红色告警），且**没有 exe 直接 `exit 1`**（`:110-125`） |
| publish 失败 | 单独捕获 `$LASTEXITCODE`（`:70`）→ 非 0 **立即 `exit 1`**（`:77-82`），并清理暂存目录 |
| 产物校验 | publish 到**临时暂存目录** `release/publish/UniFlow.staging`（publish 前清空）→ 校验 `UniFlow.exe` 存在（`:88-92`）；**通过后才替换正式目录** |
| 失败不装旧版 | 正式目录替换失败（文件被占用）→ 重试 3 次后 `exit 1`（`:151-169`），并提示先停服务；替换后再校验 `UniFlow.exe` 存在（`:170-176`） |

**未验证项**：本机无 Windows / 无 pwsh，**脚本只做了静态审阅，未做 Windows 实机执行**（见第十一节「现场验收清单」第 3 条）。

---

## 三、M-03：日志并发 —— 全局锁改为按文件独立锁

**位置**：`code/src/UniFlow/Common/Services/Logging/UniFlowFileLogger.cs`

**原理**

- 旧实现所有模块共享 provider 的**一把 `_lock`**，且每行 `Flush()`；
- 任意模块写日志都要抢同一把锁 → 高日志量时**跨模块串行化**，Aptio / DMS / DisposeSample 互相拖慢。

**改法**

- 删除 provider 级全局 `_lock`；`LogFileState` 增加**自己的**锁 `public object Lock { get; } = new();`（`UniFlowFileLogger.cs:115`）；
- 模块日志锁该模块的状态锁（`:160`），错误日志锁错误文件的状态锁（`:173`）→ **不同模块 / 错误文件互不阻塞**；
- 轮转（`Rotate`）与写入在**同一把锁内**完成 → 不会出现「半写文件」；
- `Dispose()` 改为逐状态加锁关闭（`:257`/`:259`），避免与并发写入争用同一个 `StreamWriter`。

**语义不变**：Flush 策略、轮转（`MaxFileSizeMb`）、保留期（`RetentionDays`）**均未改动**。

**闸门**：`code/tests/UniFlow.Tests/Common/Services/UniFlowFileLoggerConcurrencyTests.cs` —— ① 多线程写**不同模块**不丢行、不串文件；② 同模块并发不丢行；③ 错误日志同时进错误文件与模块文件；④ **锁是按文件的、不是全局**（结构性断言）。

---

## 四、M-04：配置单源 —— worker 不再自己读 `appsettings.json`

**位置**：`code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs`、`code/src/UniFlow/Program.cs`

**原理**

- 旧 `RefreshConfig()` 每 **3 秒** `File.ReadAllText("appsettings.json")` 自行解析 `Features` / `Aptio` 两段；
- 与 DI 的 `IOptions` / `IOptionsMonitor` 构成**配置双源**：页面显示的值、DI 绑定的值、worker 直读的值可能三者不一致；
- 更危险的是**空数组语义**：`ConfigurationBinder` **无法区分「空数组」与「键不存在」**，两者都把可空集合属性留成 `null`；而模型侧（`ConfigShared.cs:108-115` 语义注释 + `GetRunDays`）的语义是 `!= null` 即权威、`null` 才回退旧格式串 `DiscardRunDate`。
  → 页面写 `"RunDays": []`（语义 = **全部取消运行日 = 功能不运行**）→ binder 给 `null` → 回退旧串 `"1,2,3,4,5,6,7"` → **丢弃功能反而被打开**（危险方向反了）。

**改法**

1. **唯一配置入口**：worker 构造函数注入 `IOptionsMonitor<AptioConfig>` / `IOptionsMonitor<FeatureConfig>`（`DisposeSampleWorker.cs:21-22`、`:73-74`），`RefreshConfig()` 改为读 `CurrentValue`（`:100`）；`using System.Text.Json` 与 `File.ReadAllText` 已从该文件移除。
2. **统一绑定实现**：`BindAptioSection`（`Program.cs:208`）成为 `Aptio` 段的**唯一**绑定实现，`BindAptioConfig`（`:197`）与 `ConfigureAptioOptions`（`:252`，DI 注册）**共用**它 → 消除「第二个口径」。
3. **空数组 = 显式清空/禁用**：`NormalizeEmptyArrays`（`Program.cs:230`）—— 键在配置里存在但绑定结果为 `null` → 还原为**空集合**（显式清空）。覆盖 `RunDays` / `TimeRanges` / `AllowedSrmErrorCodes` 三个键。
4. **失败保留旧值**：任一段绑定/刷新异常 → 保留上一次可用配置（**绝不静默回退出厂默认**），两段独立容错。

**语义不变**：`[Restart(InnerRestart)]` 的 Ip / Port / Database 语义**未改动**（本项只统一**配置来源**，不改变重启级别判定）。

**闸门**：`code/tests/UniFlow.Tests/Common/Models/AptioConfigEmptyArrayTests.cs`、`code/tests/UniFlow.Tests/DisposeSample/Workers/DisposeSampleWorkerConfigTests.cs` —— ① 空数组 = 显式禁用、键不存在才回退旧串；② `IOptionsMonitor` 与 `BindAptioConfig` 绑定口径一致；③ worker 源码**不再出现** `File.ReadAllText`（源码文本断言）；④ DI 确实注册了 monitor 依赖；⑤ monitor 变更被下轮循环拾取。

---

## 五、M-05：SQL 名称校验收紧为白名单（允许一个 schema 前缀）

**位置**：`code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs:674`

**原理**

- 旧 `SanitizeSqlName` 用正则**剔除**非法字符且**允许反引号**（`[^a-zA-Z0-9_`]`）→ 反引号可被拼进 SQL；
- 且「剔除」语义会把 `view over` 静默改成 `viewover`（**查到另一个对象**，排查不到）。

**改法**

| 项 | 1.0.6.6 | 1.0.6.7 |
|---|---|---|
| 规则 | 剔除非法字符（允许反引号） | **白名单整名匹配**：`^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$`（`:685`） |
| 允许 | `view_x` | `view_x`、**`flexlab.view_x`**（可选**一个** schema 前缀，复核项 D4） |
| 拒绝 | —— | 反引号、分号、空格、连字符、多级 schema（`a.b.c`）、非 ASCII |
| 不匹配时 | 静默改写 | **返回 `null` → 拒绝**；空名仍回退默认视图 `view_overtimestoragesample`（`:681`） |

**连库前拒绝**：`GetDisposeSamplesAsync` 在 **`NewConnection()` 之前**做校验（`:163`），非法名 → `LogError` + 抛 `ArgumentException` → **不连库、不发任何 SQL**。

**闸门**：`code/tests/UniFlow.Tests/DisposeSample/Services/DisposeDatabaseServiceSqlNameTests.cs` —— 合法名 / schema 前缀名接受；反引号、分号、空格、连字符、`a.b.c` 拒绝；空名回退默认视图；并保留两条「旧行为对照」用例证明旧实现确实会让反引号通过、旧严格规则确实会误拒 schema 前缀。

---

## 六、M-06：`SrmStatusDecoder.Decode` 返回值改为 `isReady`

**位置**：`code/src/UniFlow/Common/Services/SrmStatusDecoder.cs:62`

**原理**：旧实现命中**第一个** SRM 节点即无条件 `return true` —— `isReady` 参与了计算却不参与返回值；调用方只看返回值就会把「未就绪」误判成「已就绪」。（生产主路径走 `IsNodeReady`，按节点号取，本方法仅诊断/测试使用，故**无生产影响**。）

**改法**：`return true` → `return isReady`；报文里没有可解析的 SRM 节点 → `false`；节点存在但 Mode/Status/Error 不满足 → `false`。

**闸门**：`code/tests/UniFlow.Tests/Common/Services/SrmStatusDecoderTests.cs` 扩写 —— 多节点 STATUS 解析语义、首个节点 OFF / 次节点 ON、`isReady` 与返回值一致性。

---

## 七、M-08：删除回滚告警节流（Warning 10 分钟一条 / Error 每 10 次一条）

**位置**：`code/src/UniFlow/DMSAutoOrder/Services/SampleCleanupService.cs:281`（`LogRollbackFailure`）

**原理**

- 删除回滚（`affected = 0`）时**保留计时**并在**每一轮**重试 → 不节流就是每轮一条 Warning（DmsCleanup worker 循环 `DMS:LoopIntervalSeconds`，默认 **60s/轮**）→ **持续失败刷屏**；
- 复核项 D1：中间版本用 `escalated = fails >= 10` 且 `if (!throttled || escalated)` → **第 10 轮起每轮**一条 Error（实测 14 轮 → 5 条 Error），告警疲劳。

**改法**

| 项 | 1.0.6.7 |
|---|---|
| 节流间隔 | 同一 `sid` 的回滚 Warning **10 分钟一条**（`RollbackWarnInterval`，`:34`） |
| 首次失败 | **一定可见**（不进节流窗口判定） |
| 升级 Error | 累计失败**每满 10 次只记一条**（第 10、20、30… 次各一条）—— 判定为 `fails % RollbackErrorEscalationCount == 0`（`:288-289`，阈值 `RollbackErrorEscalationCount = 10`，`:40`），**不是**每轮一条 |
| 成功清零 | 删除成功 → 清掉该 sid 的节流时间与失败计数（下次再失败时「首次」重新可见） |
| 节流粒度 | 按 `sid`，互不影响 |

**行为不变**：**0 行时不发 Aptio 报文**、**保留计时继续重试**的安全语义完全保留（未被节流改写）。

**闸门**：`code/tests/UniFlow.Tests/DMSAutoOrder/Services/SampleCleanupServiceThrottleTests.cs` —— 窗口内只记一条 Warning；首次失败必可见；窗口过期后重新告警；累计失败升级 Error；**升级 Error 只记一条、不是每轮**；每 N 次重复一次；成功重置计数；节流按 sid 隔离；节流**不削弱**「不发 Aptio」的安全行为。

---

## 八、M-07：前端 XSS 红闸门（新增 6 条 Playwright 用例）

**位置**：`run-test/playwright/ui.spec.js:487`（`UI-9 M-07` describe 块）

**原理**：产品代码的 `escapeHtml`（`wwwroot/app.js:558`）已完整转义 `& < > " '` 五个字符，但 1.0.6.6 的 XSS 用例**只覆盖标签注入**（文本节点上下文）—— 属性注入上下文（`value="…"`）与健康/错误消息**没有独立闸门**。复核项 D2 实测：去掉引号转义后旧 4 条用例**仍然全绿**。

**新增 6 条用例**（`ui.spec.js:500` / `:546` / `:576` / `:631` / `:667` / `:761`）

| # | 行 | 覆盖点 |
|---|---|---|
| 1 | `:500` | dispose 队列**条码 / 位置 / 重排队次数 / 检查次数**的属性+引号注入按文本显示；DOM 无新增 `img`/`script`/`on*` 属性；**行内操作仍走事件委托**（无内联 `onclick`，按钮带 `data-act`，委托按 `data-i` 取回**原始**条码进 recheck 请求体） |
| 2 | `:546` | 健康卡片 `message` 含 `<img src=x onerror=…>` + 引号 → 按文本显示、无脚本执行 |
| 3 | `:576` | 错误列表 `module`/`level`/`timestamp`/`message` 含 HTML/引号 → 按文本显示，**不逃逸到属性**（`class` 只允许字母） |
| 4 | `:631` | 多事件类型标签注入（`onerror` / `onload` / `onmouseover`）被 `escapeHtml` 全部拦截 |
| 5 | `:667` | **配置编辑态 `value="…"` 属性上下文**：引号/标签注入不得逃逸（`input` 不得出现 `on*` 属性、原文不得被截断） |
| 6 | `:761` | `escapeHtml` **五字符完整转义**（`&`/`<`/`>`/`"`/`'`）→ 拼进 `value="…"` 与 `class="…"` 两种属性上下文后：属性值逐字还原、无额外属性、无 `img`/`svg`/`script`、无 `on*` 属性 |

**红闸门负向对照（本版实测）**

| 破坏 | 结果 |
|---|---|
| 从 `escapeHtml` 删掉 `"` 与 `'` 两条转义（`/tmp` 副本） | **2 条变红**：`ui.spec.js:667`（配置编辑态 `value="…"`）、`ui.spec.js:761`（五字符完整转义）→ `2 failed / 4 passed` |
| 把行内按钮改回内联 `onclick`（`/tmp` 副本） | **1 条变红**：`ui.spec.js:500`（行内仍走事件委托）→ `1 failed / 5 passed` |

> 说明：破坏只发生在 `/tmp` 冻结副本上，**仓库源码未被改动**。

---

## 九、测试与构建

| 项 | 1.0.6.6 | **1.0.6.7** | 命令 |
|---|---|---|---|
| 构建 | 0 Error / 14 Warning | **0 Error / 14 Warning** | `/usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release` |
| 单元 / 集成测试 | 380 / 380 | **446 / 446**（Failed 0 / Skipped 0） | `/usr/share/dotnet/dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` |
| Playwright（API + UI） | 49 / 49（API 32 + UI 17） | **55 / 55**（API 32 + UI 23） | `run-test/playwright/`（`npx playwright test`，对 1.0.6.7 冻结副本执行） |
| 前端语法 | OK | **OK** | `node --check code/src/UniFlow/wwwroot/app.js`（node v24.16.0） |
| 产物自检 | 通过 | **通过**（见 9.2） | `docs/Release-Process.md` 第 2 步 |

> 14 条 Warning 全部是**既有**告警，与本版改动无关：10 条 CS（`ConfigService.cs` 8 条 + `ImmuliteCleaner.cs` 2 条的 nullable/CS0414）+ NU1902（`MySqlConnector` 2.4.0）×2 + NU1903（`SQLitePCLRaw.lib.e_sqlite3` 2.1.11）×2。

### 9.1 测试规模变化（380 → 446，**+66**）

本版新增 **8 个**测试文件（6 个测试类 + 2 个辅助类）、扩写 **7 个**既有测试文件，覆盖 8 个维护项的**正向**与**负向**语义。下表「现有用例数」为 `dotnet test --list-tests` 的实测聚合（446 条全量）：

| 维护项 | 文件 | 文件状态 | 现有用例数 | 覆盖点 |
|---|---|---|---|---|
| M-01 | `DMSAutoOrder/Services/DmsDatabaseServiceDeadCodeTests.cs` | **新增** | 2 | 方法不再暴露 / 全仓 0 调用方 |
| M-02 | `Deploy/InstallServiceScriptPathTests.cs` | 扩写 | 11（3 → 11，新增 8） | install 分支不得以「exe 是否存在」为 publish 前置条件 / 默认走 publish / `-SkipPublish` 声明与版本警告 / 跳过时无 exe 必须拒绝 / publish 后校验 exe 存在 / 检查 `$LASTEXITCODE` 并在失败时中止 / publish 失败**到不了** `New-Service`/`Start-Service` / publish 到**暂存目录**而非安装目录 / 安装的确实是本次 publish 的产物 |
| M-03 | `Common/Services/UniFlowFileLoggerConcurrencyTests.cs` | **新增** | 4 | 并发不丢行 / 不同模块不串文件 / 同模块并发不丢行 / 错误日志分流 / 锁按文件非全局 |
| M-04 | `Common/Models/AptioConfigEmptyArrayTests.cs` | **新增** | 5 | 空数组 = 显式禁用（`RunDays` / `TimeRanges` / `AllowedSrmErrorCodes`）/ 键不存在才回退旧串 / monitor 与 `BindAptioConfig` 口径一致 |
| M-04 | `DisposeSample/Workers/DisposeSampleWorkerConfigTests.cs` | **新增** | 5 | worker 用 monitor 值而非直读文件 / monitor 变更被下轮拾取 / 派生值随之更新 / 源码不再 `File.ReadAllText` / DI 注册 monitor 依赖 |
| M-05 | `DisposeSample/Services/DisposeDatabaseServiceSqlNameTests.cs` | **新增** | 28 | 合法名 / schema 前缀名 / 反引号·分号·空格·连字符·多点·非 ASCII 拒绝 / 空名回退默认视图 / 两条旧行为对照 |
| M-06 | `Common/Services/SrmStatusDecoderTests.cs` | 扩写 | 41 | 多节点 STATUS / 首节点未就绪 / 返回值与 `isReady` 一致 |
| M-08 | `DMSAutoOrder/Services/SampleCleanupServiceThrottleTests.cs` | **新增** | 9 | 节流窗口内只一条 Warning / 首次必可见 / 窗口过期重告警 / 升级 Error / **升级 Error 只一条（非每轮）** / 每 N 次重复 / 成功清零 / 按 sid 隔离 / **不削弱「不发 Aptio」安全行为** |
| M-07 | `run-test/playwright/ui.spec.js` | 扩写 | Playwright **49 → 55（+6）** | 见第八节 |

> 另有 6 个既有 `DisposeSampleWorker*` 测试文件因构造函数新增 `IOptionsMonitor` 依赖、`Decode` 语义变化而**适配**（`DisposeSampleWorkerTests` 31 / `Requeue` 27 / `Resend` 10 / `StatusUpdateFailure` 6 / `UnknownNode` 3）。

> **测试环境说明**：Playwright 使用**冻结副本** `dotnet publish` + 测试配置（`127.0.0.1:5199`、`AdminPassword=Advia1`、Features 全 false），**不连真实 MySQL / Aptio**。
> **未做的环境级 E2E**：真 MySQL / 真 Aptio / Windows Service / Docker —— 见第十一节「现场验收清单」。

### 9.2 部署包产物与校验值

| 产物 | 大小 | MD5 | SHA-256 |
|---|---|---|---|
| `release/UniFlow-1.0.6.7-linux-x64.tar.gz` | 46,306,621 B | `8c60ceef716e7702fb26ef1eea9adf42` | `22e392b21af7587d54a98ce0c58453c9cccde41d0f8203ef39e8e1f612822750` |
| `release/UniFlow-1.0.6.7-win-x64.zip` | 47,135,232 B | `570458a38cdbda6c4c7b3049e0a8543b` | `545355f0c120cd41f9dfa7f21dc41374bb305eacc4d4e7ccfa6dd45d22d027ce` |

**包自检（`docs/Release-Process.md` 第 2 步）**

| 检查项 | 结果 |
|---|---|
| 两包各 3 份 `.md`（README / INSTALL / CONFIG-REFERENCE） | ✅ Linux：`uniflow/README.md`、`uniflow/INSTALL.md`、`uniflow/CONFIG-REFERENCE.md`；Windows 同 |
| `INSTALL.md` 两平台逐字节一致 | ✅ `cmp` → `INSTALL_OK` |
| `CONFIG-REFERENCE.md` 两平台逐字节一致 | ✅ `cmp` → `CONFREF_OK` |
| 主程序在包内 | ✅ Linux `uniflow/UniFlow`（ELF 64-bit LSB pie executable, x86-64）；Windows `uniflow/UniFlow.exe`（PE32+ console, x86-64） |
| `appsettings.json` 在包内 | ✅ 且与仓库同名文件 md5 一致（`6be582a5ea8bb7fc384276f683a27344`） |
| `wwwroot/` 在包内 | ✅ `app.js`/`index.html`/`style.css`（含 `.br`/`.gz`）；`app.js` 与仓库同名文件 md5 一致（`a84f212529e47295ad18f49e66aceada`） |
| README 平台专属（内容不同，符合预期） | ✅ Linux 2,118 B / Windows 1,731 B |

**版本冒烟（沙箱，仅 Linux 包）**

- 解包 → 以 `BindIp=127.0.0.1` / `Port=5197` 启动 → `GET /api/version` 返回 **`{"version":"1.0.6.7","informational":"1.0.6.7+6012902402cc25561778a05887d6a352a1a90fb3"}`** ✅
- `GET /api/health` → `healthy`（无 MySQL / Aptio，Features 全 false，模块均 stopped，符合预期）
- 无鉴权访问破坏性端点 → **401**（`PUT /api/config`、`PUT /api/config/batch`、`GET /api/config/download`、`DELETE /api/errors`、`POST /api/restart` 全部 401）✅
- `GET /api/auth/challenge` + `SHA-256(Advia1+nonce)` → **200**；**同一 nonce 复用 → 401**（一次性生效）✅
- Windows 包：扫描 `UniFlow.exe` 内嵌版本资源 → 含 `1.0.6.7` 与 `1.0.6.7+6012902…` ✅（**未在 Windows 上实机运行**）

> 冒烟环境已清理，**未对仓库与交付包做任何额外改动**。

---

## 十、升级注意（**升级前必读**）

### 10.1 `CommandName` 必须匹配新规则（**升级前逐现场核对**）

M-05 把视图/存储过程名校验收紧为 `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$`。升级前请核对**每个现场**的 `Aptio:DisposeSample:CommandName`：

- ✅ 合法：`view_overtimestoragesample`、`flexlab.view_x`
- ❌ 会被拒绝：含反引号、分号、空格、连字符、多级前缀（`a.b.c`）、非 ASCII

> 若现场配的是带 schema 前缀的名字 —— **本版已放行一个 schema 前缀**，不会像最初实现那样整个拒掉；但**多点**的名字仍会被拒绝。**一旦不合法，丢弃功能会在每轮抛 `ArgumentException`（被记为 "Worker error"）→ 丢弃链路停摆**（复核项 D4）。

### 10.2 空数组现在是「显式清空 / 禁用」

`RunDays: []` / `TimeRanges: []` / `AllowedSrmErrorCodes: []` 现在表示**显式清空/禁用**（不再是「不限制」）。

- **现场若用空数组表达「不限制 / 不清理」** → 请**改为不写该键**（删掉该键），系统会回退旧格式串（`DiscardRunDate` / `DiscardTimeRange` / `AllowSrmErrorCode`）；
- 现场若本意就是「全部取消运行日 / 不下发」→ **保持空数组**即可。
- ⚠️ 这正是 **1.0.6.6 及更早版本的危险根因**：模型语义（`ConfigShared.cs:108` 注释）本来就是「`!= null` 即权威、空数组 = 显式清空」，但 `ConfigurationBinder` 无法区分「空数组」与「键不存在」（都给 `null`）→ 空数组被回退成 `DiscardRunDate = "1,2,3,4,5,6,7"` → **「全部取消运行日」反而变成「全周运行」**。本版由 `NormalizeEmptyArrays` 补上这个缺口。

### 10.3 配置热更新统一走配置监视器

- worker 的配置读取已统一到 `IOptionsMonitor` → 保存 `appsettings.json` 后**下轮循环即生效**（Hot 字段）；
- 需要 `[Restart(InnerRestart)]` 的项（Ip / Port / Database）**语义不变**，仍按**页面提示**做内层重启；
- 页面显示的值、DI 绑定的值、worker 生效的值现在**同源**（1.0.6.6 的「配置双源」已消除）。

### 10.4 Windows 安装脚本默认会重新 publish

- `install-service.ps1` **默认始终 publish** → Windows 现场升级**耗时略增**（需要 .NET 10 SDK + 源码目录）；
- 只想重装已有二进制可用 `-SkipPublish`（会打印版本警告；**没有 exe 时直接拒绝**）；
- **升级后请核对打印的 `Installed binary version`**，确认装上去的是新版本。

### 10.5 其它

- **无配置结构变化**（JSON 键名、层级、默认值全部不变）；
- 1.0.6.6 的全部安全加固（管理面鉴权、配置打码、上传升级鉴权等）**继续有效**，本版未削弱；
- 日志**轮转 / 保留期 / Flush 策略未变**，仅去掉跨模块串行化；
- 本版**未改任何业务判定逻辑**（丢弃状态机、窗口 10、24 次上限、5 分钟重排队、DMS 事务、PitStop 定点删除等全部原样）。

---

## 十一、已知问题 / 延后项（**不阻塞本版**）

| 项 | 状态 | 说明 |
|---|---|---|
| **M-09** `send` 已投递但 DB 写失败时重复下发 | 延后 | 命令已写入 socket，但 DB `send=1` 失败 → 保持 Ready，下一轮可能重发同一条 COMMENT。协议幂等（TRASH）通常无害；极端 DB 故障时重复指令/日志。**本版未改**。 |
| **M-10** 依赖漏洞升级 | 延后 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（NU1903，high）、`MySqlConnector` 2.4.0（NU1902，moderate）。当前使用面为本地 admin SQLite / 内网 MySQL，无直接可利用入口；升级需单独维护窗口 + 全量回归，**不与业务改动混提**。**本版未改**。 |
| **D5** M-05 拒绝路径的日志风暴 | 接受 | 非法名在 worker 循环内每 3s 抛一次 → Error 日志每 3s 一条（无节流）。仅在**配置写错**时发生；按 10.1 核对配置即可避免。 |
| **D6** M-03 每行 `Flush` 未改 | 接受 | 本次只去掉**跨模块**串行化；单模块高日志量仍是每行一次落盘。 |
| **D7** M-03 `Dispose()` 竞态 / 状态字典无上限 | 接受 | `Dispose()` 遍历时若有新模块 `GetOrAdd`，该 writer 不会被关；模块名有界，实际风险低。 |
| **D8** M-08 计数/时间字典只增不减 | 接受 | `_rollbackWarnAtUtc` / `_rollbackFailCount` 只在删除成功时清理；样本被外部删除时长期缓慢增长。 |
| **D10** `install-service.ps1 --contentRoot $exePath`（指向文件） | 接受 | **既有**问题，非本轮引入；Windows 侧是否真能启动**未验证**（本机无 Windows）。 |
| **D11** M-04 `Ip/Port/SrmNodeIds` 的「热 / 重启」口径 | 接受 | 标注为 `InnerRestart`，但 worker 与 DB 服务都走 monitor → 会热生效；socket 工厂仍冻结 → 页面提示与实际生效范围**仍不完全一致**（**非本轮回归**，本版只统一配置来源）。 |
| **E-01 ~ E-07** 环境 / 现场验证项 | 待现场执行 | MyISAM/InnoDB 确认、真 MySQL（flexlab/DMSCN）验收、真 Aptio 2055 验收、Windows 服务 / 单实例、Docker 容器、现场浏览器 UI、HTTP/HTTPS 与鉴权重放 —— 逐项内容见 `docs/Maintenance-Order-2026-11-07.md` 第二节。 |
| HTTP 重放（nonce/hash 60s 窗口） | 接受 | 沿用 1.0.6.6 结论：明文 HTTP 下仍有重放窗口，局域网可信可接受；需更强防护请上 TLS。 |

### 现场验收清单（本版**未做**真实环境 E2E，必须现场确认）

1. **真 MySQL（flexlab / DMSCN）**：`sam_dispose_status` 的 `ALTER TABLE` 迁移（账号 DDL 权限）、`IF()` / `TIMESTAMPDIFF` / `LIMIT @p` / `information_schema` 兼容性、DMS 多表删除事务回滚（InnoDB / MyISAM 差异）、PitStop 定点删除、`SrmExport` 时间窗；
2. **真 Aptio 2055**：无 ACK 语义、慢 STATUS（3.5s）、半开连接、断链恢复、5 分钟重排队、窗口 10、24 次失败上限、`AllowStandbyDispose`（默认 false，STB/Pink 现场测试后再决定是否长期开启；GTO 不允许）；
3. **Windows**：服务安装/升级路径（**M-02 修完后必须复验**）、`Global\UniFlow` 单实例在服务账号权限不足时回退 `Local`、双开（服务 + 控制台）不会双写/双发；**本版安装脚本只做静态审阅，无 Windows 实机验证**；
4. **Docker**：`docker compose up` 使用 `appsettings.docker.json` / `Aptio__Database__*` / `Features__Aptio__*`；确认连到正确 MySQL、功能开关正确、卷 / 日志路径正确；
5. **现场浏览器 UI**：保存一次只弹一次密码、批量保存一次请求、下载完整配置需鉴权、测试连接只测已保存配置、上传升级 / 重启 / 删除的 401/403/取消文案；
6. **配置核对（最高优先级）**：逐现场核对 `Aptio:DisposeSample:CommandName`（10.1）与空数组键（10.2）—— **这两项错了会直接影响丢弃链路**。

### 升级步骤建议

1. 备份现场 `appsettings.json`（尤其是数据库口令与管理密码）；
2. 逐现场核对 `Aptio:DisposeSample:CommandName` 与三个集合键（`RunDays` / `TimeRanges` / `AllowedSrmErrorCodes`）是否为空数组；
3. 停服务 → 覆盖程序文件 → **保留现场 `appsettings.json`**（安装包内的配置是模板，不要覆盖现场配置）；
4. Windows 侧用 `install-service.ps1`（默认会重新 publish，耗时略增）；核对打印的 `Installed binary version`；
5. 启动后查看日志，确认 `sam_dispose_status` 加列成功（无 `Ensure sam_dispose_status.resend_count column failed` 告警）；
6. 用丢弃队列页做一次小批量验证，确认冰箱侧收到 COMMENT 的节奏符合窗口 10 的预期；
7. 确认 `/api/dispose/pending` 未返回 503，且日志中**没有** `Worker error`（若有，多半是 10.1 的命令名被拒）。

---

## 附：改动文件索引（发布相关）

| 文件 | 说明 |
|---|---|
| `code/src/UniFlow/UniFlow.csproj:5-7` | `Version` / `AssemblyVersion` / `FileVersion` → `1.0.6.7` |
| `deploy/build.sh:7` | `VERSION` → `1.0.6.7` |
| `deploy/package.sh:6` | 用法注释示例 → `1.0.6.7` |
| `docs/Release-Process.md:39,69,87` | 可复制即用的示例版本号 → `1.0.6.7` |
| `docs/CHANGELOG-1.0.6.7.md` | 本文件 |
| `docs/Maintenance-Order-2026-11-07.md` | 维护单（本版实现依据；**由主会话维护，本版未改**） |
| `code/src/UniFlow/DMSAutoOrder/Services/DmsDatabaseService.cs:102` | M-01 死代码删除 + 留注释 |
| `deploy/windows/install-service.ps1` | M-02 / D3 默认始终 publish、`-SkipPublish`、`$LASTEXITCODE` 闸门、暂存目录 |
| `code/src/UniFlow/Common/Services/Logging/UniFlowFileLogger.cs` | M-03 按文件独立锁 |
| `code/src/UniFlow/DisposeSample/Workers/DisposeSampleWorker.cs` | M-04 改走 `IOptionsMonitor` |
| `code/src/UniFlow/Program.cs` | M-04 `BindAptioSection` 统一绑定 + `NormalizeEmptyArrays` |
| `code/src/UniFlow/DisposeSample/Services/DisposeDatabaseService.cs` | M-05 `SanitizeSqlName` 白名单 + 连库前拒绝 |
| `code/src/UniFlow/Common/Services/SrmStatusDecoder.cs` | M-06 `return isReady` |
| `code/src/UniFlow/DMSAutoOrder/Services/SampleCleanupService.cs` | M-08 回滚告警节流 |
| `run-test/playwright/ui.spec.js` | M-07 新增 6 条 XSS 红闸门用例 |
| `code/tests/UniFlow.Tests/**`（新增 8 个文件 + 扩写 7 个文件） | M-01 ~ M-08 的单测闸门 |
| `docs_20261103/Review-2026-11-07-M01-M08.md` | L3 独立复核报告（复核项 D1 ~ D11 的来源） |
