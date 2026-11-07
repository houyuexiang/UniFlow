# UniFlow 维护单（1.0.6.7 目标）

- **创建日期**：2026-11-07
- **基线**：1.0.6.6（已构建/打包，未提交、未打 tag）
- **来源**：`docs/Fix-Report-2026-11-07.md` §7/§8（全项目审查与多轮复核的延后项）
- **原则**：本维护单**不修改 1.0.6.6 的发布产物**；代码项在 1.0.6.6 提交/tag 后另起分支或工作树实施，目标版本 **1.0.6.7**；环境项由现场/运维执行。
- **不阻塞 1.0.6.6 上线**：本单所有项均为 P2/P3 或环境验证，不影响 1.0.6.6 的正常运行。

---

## 一、代码维护项（m3 负责，目标 1.0.6.7）

### M-01 `GetUnknownPatientOrdersAsync` SQL 运算符优先级（P2，死代码）
- **位置**：`DMSAutoOrder/Services/DmsDatabaseService.cs:63-73`
- **原理**：`WHERE (A) AND (B) OR (C)` 无括号，实际为 `(A AND B) OR C`；`codoid NOT IN (...)` 单独成为 OR 分支，会把所有无订单的 `reqtube` 纳入。
- **影响面**：当前**无调用方**（旧 DMS 自动下单遗留），生产无影响；未来启用会结果集错误。
- **决定**：删除死方法，或显式加括号并补测试；优先删除，减少误用。
- **验收**：全仓 0 调用方；若保留，SQL 语义测试覆盖 `A AND (B OR C)`。

### M-02 `deploy/windows/install-service.ps1` 已有 exe 时跳过 publish（P3）
- **位置**：`deploy/windows/install-service.ps1:37` 附近
- **原理**：`install` 分支若目标 exe 已存在则跳过 `dotnet publish`，可能把旧二进制装成服务。
- **影响面**：Windows 现场升级时“升级后还是旧程序”。
- **决定**：修。改为始终 publish，或增加显式 `-SkipPublish` 开关并在跳过时打印警告/版本对比。
- **验收**：脚本在 exe 已存在时仍 publish（或明确拒绝并提示）；Windows 现场双路径验证。

### M-03 `UniFlowFileLogger` 全局锁 + 每行 Flush（P3，性能）
- **位置**：`Common/Services/Logging/UniFlowFileLogger.cs:145-164`
- **原理**：所有模块日志共享一把 `_lock`，每行 `Flush()`；高日志量时串行化、落盘放大。
- **影响面**：仅性能/延迟，不影响正确性；DisposeSample debug 或错误风暴时可能拖慢 worker。
- **决定**：评估后改为按模块/文件独立锁 + 批量 flush（或 `AutoFlush=false` + 定时 flush）；加基准/回归。
- **验收**：高并发日志基准不劣化；日志不丢、轮转/保留不变。

### M-04 配置双源（worker 直接读 JSON + IOptions）（P3，设计）
- **位置**：`DisposeSampleWorker.RefreshConfig`（每 3s `File.ReadAllText`）与 DI `IOptionsMonitor`
- **原理**：两套配置源并存；`[Restart(InnerRestart)]` 的 Ip/Port/Database 被 worker 热读但 socket/DB 工厂冻结，语义不一致。
- **影响面**：页面显示新值但部分组件要内层重启才生效；不造成数据错误，但易误判。
- **决定**：统一配置入口（全部走 `IOptionsMonitor` + `[Restart]` 语义），worker 不再直接读文件；或明确文档化“哪些热、哪些要重启”。
- **验收**：保存后各组件生效级别与页面提示一致；无重复读盘。

### M-05 `SanitizeSqlName` 允许反引号（P3，低风险）
- **位置**：`DisposeSample/Services/DisposeDatabaseService.cs:650-656`
- **原理**：正则允许 `` ` ``，`CommandName` 来自本地配置，理论上可构造反引号注入。
- **影响面**：配置写操作已鉴权；仅管理员可改，风险低。
- **决定**：收紧为 `[A-Za-z0-9_]`，或改为白名单校验 + 反引号包裹；补测试。
- **验收**：含反引号/分号/空格的名称被拒绝；正常视图名可用。

### M-06 `SrmStatusDecoder.Decode` 首个节点无条件 `return true`（P3，潜在）
- **位置**：`Common/Services/SrmStatusDecoder.cs:45-59`
- **原理**：只处理第一个 SRM 节点，`isReady` 未参与返回；生产主路径用 `IsNodeReady`，此方法仅诊断/测试。
- **影响面**：无生产影响；未来误用会误判。
- **决定**：修正为 `return isReady` 或删除未用方法；补测试。
- **验收**：多节点 STATUS 解析语义正确；全仓无未用告警。

### M-07 XSS 引号转义/健康消息的独立红闸门（P3，测试）
- **位置**：`run-test/playwright/ui.spec.js`、`wwwroot/app.js`
- **原理**：代码已转义 `& < > " '`，但现有 XSS mock 只覆盖标签注入；属性注入（`value="..."`）与健康消息未独立测试。
- **影响面**：代码已修，仅测试缺口；未来回归可能漏。
- **决定**：加 1 条 Playwright mock（健康消息/错误消息含引号/HTML）与 1 条属性注入用例；不改变产品行为。
- **验收**：去掉引号转义时用例变红；正常消息显示不破版。

### M-08 `SampleCleanupService` 回滚重复 Warning 节流（P3）
- **位置**：`DMSAutoOrder/Services/SampleCleanupService.cs:220-228`
- **原理**：删除回滚返回 0 时保留计时，每轮重试并打 Warning；持续失败会刷日志。
- **影响面**：日志量，不影响数据。
- **决定**：按 sid 10 分钟节流；或累计失败次数后升级为 Error 一次。
- **验收**：持续失败不刷屏；首次/关键失败仍可见。

### M-09 `send` 已投递但 DB 写失败时重复下发（P2/P3，设计）
- **位置**：`DisposeSampleWorker.HandleReadyAsync` + `DisposeDatabaseService.UpdateStatusAsync`
- **原理**：命令已写入 socket，但 DB `send=1` 失败 → 保持 Ready，下一轮可能重发同一条 COMMENT。
- **影响面**：协议幂等（TRASH），通常无害；极端 DB 故障时重复指令/日志。
- **决定**：评估加“内存已投递标记”（同一 barcode 在进程内不重发）或 DB 写重试 3 次；不改变正常路径。
- **验收**：DB 失败用例不重复发送；DB 恢复后状态正确。

### M-10 依赖漏洞升级（P3，安全/维护窗口）
- **位置**：`UniFlow.csproj` 传递依赖 `SQLitePCLRaw.lib.e_sqlite3 2.1.11`（NU1903 高危）、`MySqlConnector 2.4.0`（NU1902 中危）
- **原理**：依赖版本存在已知漏洞；当前使用面为本地 admin SQLite / 内网 MySQL，无直接可利用入口。
- **影响面**：安全审计/供应链要求；不阻塞功能。
- **决定**：在维护窗口升级到修复版本，跑全量 380 单测 + Playwright 49 + 真 MySQL 回归；不要与业务改动混提。
- **验收**：`dotnet list package --vulnerable` 无高危/中危；测试全绿。

---

## 二、环境/现场验证项（运维/现场负责，不写代码）

### E-01 MyISAM/InnoDB 引擎确认（P2 条件项）
- **命令**：
  ```sql
  SELECT TABLE_NAME, ENGINE FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='DMSCN' AND ENGINE<>'InnoDB';
  ```
- **影响**：MyISAM 表不参与 DMS 事务回滚，半删除仍可能。
- **决定**：若存在 MyISAM，改 InnoDB 或接受限制并记录；上线前完成。

### E-02 真 MySQL（flexlab/DMSCN）验收
- `sam_dispose_status.resend_count` 自动加列（旧表 ALTER）权限/耗时；
- `IF()`/`TIMESTAMPDIFF`/`LIMIT @p`/`information_schema` 方言；
- DMS 多表删除事务回滚（InnoDB）；
- PitStop 只删超时 id；
- `SrmExport` 时间窗。

### E-03 真 Aptio 2055 验收
- 无 ACK（写成功=已投递）；
- 慢 STATUS（3.5s）、半开连接、断链恢复；
- 5 分钟重排队、窗口 10、24 次失败；
- `AllowStandbyDispose` 默认 false；现场测试 STB/Pink 后再决定是否长期开启；GTO 不允许。

### E-04 Windows 服务/单实例
- `Global\UniFlow` 在服务账号权限不足时回退 `Local`；双开验证（服务 + 控制台）不会双写/双发；
- `install-service.ps1` 升级路径（M-02 修完后复验）。

### E-05 Docker 容器
- `docker compose up` 使用新 `appsettings.docker.json` / `Aptio__Database__*` / `Features__Aptio__*`；
- 确认连到正确 MySQL、功能开关正确、卷/日志路径正确。

### E-06 浏览器/现场 UI
- 保存一次只弹一次密码；批量保存一次请求；
- 下载完整配置需鉴权；测试连接只测已保存配置；
- 上传升级/重启/删除的 401/403/取消文案。

### E-07 HTTP/HTTPS 与鉴权重放
- 当前 HTTP 下 nonce/hash 60s 重放窗口；局域网可信可接受；
- 若现场网络不可信，升级 HTTPS（反向代理/证书）并复测。

---

## 三、执行顺序与交付

1. **1.0.6.6 已提交**：commit `6012902`（未打 tag；发布包已生成）；
2. 代码维护项 **M-01~M-08 已完成**并进入 1.0.6.7；**M-09 延后**、**M-10 单独维护窗口**；
3. 环境项（E-01~E-07）在 1.0.6.7 测试包到现场后执行；
4. 1.0.6.7 已完成：全量 **446** 单测、Playwright **55**、构建 0 Error；真环境验收（E-01~E-07）仍需现场执行。

**验收闸门**：任何代码维护项必须附 `file:line` + 测试/负向证据；环境项必须附命令 + 原始输出 + `主机:路径`；不得把“未验证”写成 PASS。

---

## 四、维护单状态

> 更新：2026-11-07，1.0.6.7 发布工程完成（未提交/未 tag）。

| 项 | 状态 | 负责人 | 目标 |
|---|---|---|---|
| M-01 删除 `GetUnknownPatientOrdersAsync` | ✅ 完成（死方法已删，0 调用方，测试锁定） | m3 | 1.0.6.7 |
| M-02 Windows 安装脚本始终 publish | ✅ 完成（`-SkipPublish` 逃生口 + 失败退出） | m3 | 1.0.6.7 |
| M-03 日志按文件独立锁 | ✅ 完成（并发测试） | m3 | 1.0.6.7 |
| M-04 配置单源（IOptionsMonitor + 空数组语义） | ✅ 完成（不再读文件；空数组=显式禁用） | m3 | 1.0.6.7 |
| M-05 `SanitizeSqlName` 收紧 | ✅ 完成（允许一个 `schema.` 前缀） | m3 | 1.0.6.7 |
| M-06 `SrmStatusDecoder.Decode` 返回 `isReady` | ✅ 完成 | m3 | 1.0.6.7 |
| M-07 XSS 属性上下文红闸门 | ✅ 完成（Playwright 55/55，6 条新增） | macflash | 1.0.6.7 |
| M-08 回滚告警节流 | ✅ 完成（10 分钟/sid；Error 每 10 次一条） | m3 | 1.0.6.7 |
| M-09 `send` DB 失败重复下发 | ⏸ **延后**（协议幂等 + Error 告警可接受） | — | 后续 |
| M-10 依赖漏洞升级 | ⏸ **延后**（需维护窗口 + 真 MySQL 回归） | m3 + 现场 | 后续 |
| E-01~E-07 环境项 | 待现场执行 | 运维/现场 | 1.0.6.6/1.0.6.7 上线前 |

**1.0.6.7 验证**：构建 0 Error；单测/集成 **446/446**（1.0.6.6 为 380）；Playwright **55/55**（1.0.6.6 为 49）；独立复核 PASS（`docs_20261103/Review-2026-11-07-M01-M08.md` + 收尾 spot-check）。发布说明见 `docs/CHANGELOG-1.0.6.7.md`。

---

## 五、1.0.7.0 新增需求：样本丢弃“快速填满窗口”

| 项 | 状态 | 负责人 | 目标 |
|---|---|---|---|
| **F-01 快速填窗**（`EnableFastFill`，默认关闭） | ✅ 完成 | m3 | 1.0.7.0 |
| **F-02 批量确认**（`FastFillConfirmIntervalSeconds`，默认 5 秒） | ✅ 完成 | m3 | 1.0.7.0 |
| **F-03 重排队死锁修复**（窗口满仍允许重排队发 1 条 STATUS） | ✅ 完成（**默认现场也生效**，需现场验证） | m3 | 1.0.7.0 |
| **F-04 页面/文档/Playwright** | ✅ 完成（3 个配置字段 + 1 条 UI 用例） | m3/macflash | 1.0.7.0 |

**关键决策**：
- 快速填窗**默认关闭**；开启后一个 tick 内连续发 COMMENT 到窗口满（`MaxOnetimeSelectDiscardCount`，默认 10），每条间隔 `FastFillIntervalMs`（默认 1000ms，硬钳 0~5000ms）。
- 确认改批量：每 `FastFillConfirmIntervalSeconds`（默认 5，硬钳 1~60）用一条 SQL 确认所有在途记录（`send=1 & disposed=0`），命中 `&3-{node}-%` 即 `disposed=1`。
- 失败/节点未就绪/空条码/skip 失败 → 立即停止本轮；skip 成功继续，单轮上限 200 条。
- `MaxOnetimeSelectDiscardCount<=0` → 回退单条路径（逐条确认仍生效）。
- 重排队死锁修复**不受开关控制**：窗口满且有到期候选时仍发 1 条 STATUS 并重排队；无候选不发 STATUS。现场需验证慢冰箱/节点未就绪场景的重复 COMMENT。

**1.0.7.0 验证**：构建 0 Error；单测/集成 **473/473**（1.0.6.7 为 446，+27）；Playwright **56/56**（1.0.6.7 为 55，+1）；独立复核 PASS-with-findings，5 个 P2 已收口 4 个（P2-5 为默认行为变化，写入发布说明由现场验证）。发布说明见 `docs/CHANGELOG-1.0.7.0.md`；复核报告见 `docs_20261103/Review-2026-11-07-fastfill.md`。

> 本维护单不修改 1.0.6.6/1.0.6.7 产物；1.0.6.6/1.0.6.7 的 release note 已把本单列为“已知问题/延后项”，不阻塞发布。
