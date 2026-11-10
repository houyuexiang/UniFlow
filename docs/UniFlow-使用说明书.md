# UniFlow 使用说明书

- **文档版本**：1.0（2026-11-09）
- **适用软件**：UniFlow **1.0.7.x**（含 `DisposeByRack` 按架排序、`EnableFastFill` 快速填窗）
- **适用对象**：现场实施、运维、实验室管理员
- **文档定位**：一册通。逐字段权威清单见 `CONFIG-REFERENCE.md`，接口细节见 `Interface-Manual.md`，部署细节见 `Deployment-Guide.md`。

> 本文不替代厂商手册。Aptio/FlexLab 的协议语义以 Siemens 手册
> `DCAU-H00.816.12.01.02.pdf` 为准；UniFlow 只负责按该口径收发命令和核对数据库。

---

## 0. 五分钟快速开始

1. **解压安装包**（Linux `.tar.gz` / Windows `.zip`）。
2. **改 `appsettings.json`**：数据库、Aptio IP/端口、`SrmNodeIds`、`WebAdmin:AdminPassword`、`WebAdmin:BindIp`。
3. **安装服务**：
   - Linux：`sudo ./install.sh`（或 `sudo ./install.sh --enable` 开机自启）
   - Windows：管理员 PowerShell 执行 `.\install.ps1`（或 `.\install-enable.ps1`）
   - Docker：`docker compose -f deploy/docker-compose.yml up -d`
4. **打开管理页**：`http://<服务器IP>:5100`，用 `WebAdmin:AdminPassword` 登录（默认 `Advia1`，**首次部署必须改**）。
5. **先小批量验证**：只开 `Features:Aptio:DisposeSample`，用 1~2 个样本走一轮，确认日志出现 `confirmed disposed` 后再放开。

---

## 1. 系统概述

UniFlow 是一个基于 **.NET 10** 的实验室自动化流程协调服务，统一替代原来的多个 Delphi 小工具。它以 Windows Service、Linux systemd 或 Docker 方式运行，自带 Web 管理界面。

### 1.1 功能模块

| 模块 | 作用 | 主要配置节 |
|---|---|---|
| **DisposeSample** | 样本丢弃状态机；SRM 超期样本自动丢弃 | `Aptio:DisposeSample` |
| **SrmExport** | 每日导出已丢弃样本清单（TXT） | `Aptio:SrmExport` |
| **WorkListCleaner** | Immulite 工作列表清理（Access 数据库，仅 Windows） | `WorkListCleaners` |
| **DMSAutoOrder** | DMS 自动工单：PitStop 监控、状态修正、样本自动删除 | `DMS` |
| **WebAdmin** | Web 管理界面、健康检查、错误/日志、配置、升级/重启 | `WebAdmin` |
| **Common** | Aptio Socket、ASTM、SRM 状态解析、工作时间判断、统一日志 | — |

### 1.2 数据源

| 数据 | 用途 | 默认 |
|---|---|---|
| Aptio/FlexLab MySQL | 样本位置、丢弃候选视图、丢弃确认 | `Aptio:Database`（库名通常 `flexlab`） |
| DMS MySQL | DMS 自动工单、PitStop、样本清理 | `DMS:DbHost` 等（库名通常 `DMSCN`） |
| 管理 SQLite | Web 错误日志、健康历史 | 安装目录 `uniflow_admin.db` |
| 文件日志 | 分模块日志 + 错误汇总 | 安装目录 `logs/` |

### 1.3 运行形态

```
UniFlow 进程
  ├─ WebAdmin（Kestrel，默认 5100）
  ├─ 内层业务宿主（可单独重启）
  │    ├─ DisposeSampleWorker
  │    ├─ SrmExportWorker
  │    ├─ WorkListCleaner（可选）
  │    └─ DMSAutoOrder（可选）
  └─ 公共 AptioSocketClient（2055）
```

> “重启业务任务”只重建内层宿主，Web 页面不断；“重启服务进程”会退出进程，由 systemd / Windows Service Recovery / Docker 自动拉起。

---

## 2. 安装与升级

### 2.1 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10/Server 2012+ x64；Linux x64（glibc 2.17+）；Docker 可选 |
| 运行时 | **发布包已自包含 .NET 10，无需预装运行时**；源码构建才需要 .NET 10 SDK |
| 数据库 | MySQL 5.7+ / 8.0（Aptio FlexLab、DMS） |
| 网络 | 可访问 Aptio 服务器 TCP 2055、MySQL 3306 |
| 资源 | 建议 2 GB 内存以上；日志盘按 30 天保留预留 |

### 2.2 安装包内容

**Linux** `UniFlow-<版本>-linux-x64.tar.gz`：

```
uniflow/
├── UniFlow              # 单文件可执行程序（自包含）
├── appsettings.json
├── wwwroot/             # Web 管理界面
├── install.sh           # systemd 安装脚本
├── INSTALL.md
├── 使用说明书.md         # 本说明书
├── README.md
└── CONFIG-REFERENCE.md
```

**Windows** `UniFlow-<版本>-win-x64.zip`：

```
uniflow/
├── UniFlow.exe
├── appsettings.json
├── wwwroot/
├── install.ps1          # 安装为服务（手动启动）
├── install-enable.ps1   # 安装并开机自启
├── uninstall.ps1
├── INSTALL.md
├── 使用说明书.md         # 本说明书
├── README.md
└── CONFIG-REFERENCE.md
```

### 2.3 Linux 安装

```bash
tar -xzf UniFlow-1.0.7.2-linux-x64.tar.gz
cd uniflow

# 编辑配置（数据库、Aptio、管理密码）
vim appsettings.json

# 安装为 systemd 服务
sudo ./install.sh            # 安装并启动，不开机自启
sudo ./install.sh --enable   # 同时启用开机自启

# 验证
systemctl status uniflow
curl http://localhost:5100/api/health
```

日常管理：

```bash
systemctl start|stop|restart uniflow
journalctl -fu uniflow
```

### 2.4 Windows 安装

```powershell
# 1. 解压到 C:\UniFlow
# 2. 编辑 C:\UniFlow\appsettings.json
# 3. 管理员 PowerShell
cd C:\UniFlow
.\install.ps1          # 安装为 Windows 服务（LocalSystem，手动启动）
.\install-enable.ps1   # 安装并设为开机自启
```

验证：

```powershell
Get-Service UniFlow
# 浏览器：http://<服务器IP>:5100/api/health
```

**从源码构建机安装**（不是现场包安装）：

```powershell
# 默认总是重新 dotnet publish（避免把旧二进制装成服务）
.\deploy\windows\install-service.ps1

# 只重装已有二进制（会打印版本警告；没有 exe 时拒绝）
.\deploy\windows\install-service.ps1 -SkipPublish

# 其它动作
.\deploy\windows\install-service.ps1 -Action status
.\deploy\windows\install-service.ps1 -Action uninstall
```

> 升级前先备份 `release\publish\UniFlow\appsettings.json`；脚本会用本次 publish 的目录整体替换，配置会被覆盖，安装后恢复并重启服务。

### 2.5 Docker

```bash
docker compose -f deploy/docker-compose.yml up -d
docker compose logs -f
```

环境变量用 `__` 分隔，**没有 `UniFlow` 前缀**：

```text
Aptio__Database__Host / Port / User / Password / Database
Aptio__Ip / Aptio__Port
Features__Aptio__DisposeSample / SrmExport / Delivery / DeliveryFile / Priority / TestNameDispose
WebAdmin__BindIp / WebAdmin__Port
```

> 旧写法 `UniFlow__Database__*` / `UniFlow__Aptio__*` 是死变量，不要使用。

### 2.6 首次配置清单

| 配置 | 说明 |
|---|---|
| `WebAdmin:AdminPassword` | 默认 `Advia1`；**页面不能改**，只能后台改 `appsettings.json`；Hot 生效 |
| `WebAdmin:AuthMemoryMinutes` | 默认 `5`（管理验证记忆，分钟）：一次密码后 5 分钟内不再重复弹密码；`0` = 每次都弹；Hot 生效（见 §4.1） |
| `WebAdmin:BindIp` | 现场常用 `0.0.0.0`（局域网访问）；默认模板为 `localhost` 时只能本机访问 |
| `WebAdmin:Port` | 默认 `5100` |
| `Aptio:Ip` / `Aptio:Port` | Aptio 服务器地址 / 2055 |
| `Aptio:SrmNodeIds` | **必须包含数据里实际出现的节点号**（例如 `["13"]`；有 `&2` 存储时还要含 `01`） |
| `Aptio:Database` | FlexLab MySQL（Host/Port/User/Password/Database） |
| `Features:Aptio:DisposeSample` | 是否启用样本丢弃 |
| `Aptio:DisposeSample:CommandType/CommandName` | 0=视图；1=存储过程 |
| `DMS:*` | 如启用 DMS 自动工单再填 |

> `SrmNodeIds` 配错是最常见的“样本不丢”原因：`SkipOnUnknownNode=true` 时，节点不在清单里的样本会被标记 `send=2` 跳过。

### 2.7 升级与回滚

**推荐：Web 管理页在线升级**

1. 备份 `appsettings.json`；
2. 管理页上传升级包（需要管理密码）；
3. `apply` 应用（备份当前二进制+wwwroot 到 `backup/<时间戳>/`，`appsettings.json` 不会被覆盖）；
4. `restart/process` 重启进程；
5. 验证 `/api/version` 与 `/api/health`。

**手工升级**：停服务 → 备份配置 → 替换程序文件 → 恢复 `appsettings.json` → 启动 → 验证。

**回滚**：保留上一版安装包和配置；停服务 → 恢复上一版文件/配置 → 启动。在线升级的旧文件在 `backup/<时间戳>/`。

### 2.8 卸载

- Linux：`systemctl stop uniflow && systemctl disable uniflow`，删除 unit 与安装目录；
- Windows：`.\uninstall.ps1`（或 `sc.exe delete UniFlow`）；
- Docker：`docker compose down`（数据卷按需保留/删除）。

---

## 3. 配置说明

### 3.1 顶层结构

```
Logging/            日志级别、文件日志
Features/           功能开关（Aptio / Immulite / Dms）
Aptio/              基础设施 + 核心业务（Database / DisposeSample / SrmExport / Delivery / …）
WorkListCleaners/   工作列表清理器数组（Immulite）
WebAdmin/           Web 管理控制台
DMS/                DMS 基础设施与自动工单
```

旧结构 `AptioAutoProcess/`、平铺根 `Database/`、`Dispose/`、`Export/` 会在启动时一次性迁移到当前结构；新部署不要再用旧键。

### 3.2 关键配置

#### 功能开关

| 路径 | 默认 | 说明 |
|---|---|---|
| `Features:Aptio:DisposeSample` | `true` | 样本丢弃 |
| `Features:Aptio:SrmExport` | `true` | SRM 导出 |
| `Features:Aptio:Delivery` | `false` | 按测试名递送 |
| `Features:Aptio:DeliveryFile` | `false` | 清单文件递送 |
| `Features:Aptio:Priority` | `false` | 优先级标记 |
| `Features:Aptio:TestNameDispose` | `false` | 按测试名丢弃 |
| `Features:Dms:PitStopMonitor` | `false` | PitStop 监控 |
| `Features:Dms:StatusCorrection` | `false` | 状态修正 |
| `Features:Dms:SampleCleanup` | `false` | 样本自动删除 |
| `Features:Dms:EmptyResultCleanup` | `false` | 空结果清理 |

#### Aptio 连接

| 路径 | 默认 | 说明 |
|---|---|---|
| `Aptio:Ip` | `127.0.0.1` | Aptio 服务器 |
| `Aptio:Port` | `2055` | Aptio Socket 端口 |
| `Aptio:SrmNodeIds` | `["09","16"]` | SRM 节点号清单；**必须含现场实际节点** |
| `Aptio:Database:Host/Port/User/Password/Database` | — | FlexLab MySQL |

#### 样本丢弃（DisposeSample）

| 路径 | 默认 | 说明 |
|---|---|---|
| `CommandType` | `0` | 0=视图；1=存储过程 |
| `CommandName` | `view_overtimestoragesample` | 视图/存储过程名 |
| `RunDays` | `[1..7]` | 运行日；空数组=显式禁用 |
| `TimeRanges` | 两段示例 | `Start/End/Threshold`；`Threshold` 是 SRM 样本数阈值 |
| `LoopIntervalSeconds` | `3` | 状态机轮询间隔 |
| `MaxWaitDiscardCount` | `2` | 默认单条模式的最大确认轮数 |
| `MaxOnetimeSelectDiscardCount` | `10` | 单次选取上限，**同时是发送窗口** |
| `DisposeByRack` | `true` | **按架排序**（1.0.7.1 起）；`false` 回退原顺序 |
| `EnableFastFill` | `false` | 快速填窗；关闭时与 1.0.6.7 行为一致 |
| `FastFillIntervalMs` | `1000` | 快速填窗每条 COMMENT 间隔，0~5000 |
| `FastFillConfirmIntervalSeconds` | `5` | 批量确认间隔，1~60 |
| `AllowedSrmErrorCodes` | `["0000","0F0A","0E59","AAAA"]` | 允许的 SRM 错误码 |
| `SkipOnUnknownNode` | `true` | 未知节点是否跳过（`send=2`） |
| `AllowStandbyDispose` | `false` | 测试开关：STB/Pink 是否允许丢弃；GTO 始终不允许 |
| `ResendIntervalMinutes` | `5` | 重排队检查间隔；0=关闭 |
| `MaxResendCount` | `24` | 单条最多重发次数；用尽置 `send=3` |
| `MaxPendingRecords` | `0` | 待处理队列硬上限；0=不限 |
| `PendingWarnThreshold` | `1000` | 待处理队列告警阈值 |

#### WebAdmin

| 路径 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 启用管理页 |
| `BindIp` | `0.0.0.0`（模板） | 绑定地址；默认代码值是 `localhost`，以实际 `appsettings.json` 为准 |
| `Port` | `5100` | 管理页端口 |
| `AdminPassword` | `Advia1` | **只能后台改**；置空则破坏性端点全部 403 |
| `ErrorRetentionDays` | `30` | 错误记录保留天数 |
| `HealthCheckIntervalSeconds` | `60` | 健康检查间隔 |

#### DMS（可选）

| 路径 | 说明 |
|---|---|
| `DMS:DbHost/Port/User/Password/DbName` | DMS MySQL |
| `DMS:PitStop:TimeoutMinutes` | PitStop 超时清理 |
| `DMS:StatusCorrection:AutoModifyTestStatus` | `1` 不修改 / `2` 归零重发 / `3` 置 F |
| `DMS:SampleCleanup:TestTriggerSampleDeletion` | 测试触发删除规则（`TestName:TimeoutMinutes;...`） |
| `DMS:SampleCleanup:SendCancelMessageToAptio` | 删除后是否向 Aptio 发取消 |

### 3.3 修改方式与生效级别

| 方式 | 说明 |
|---|---|
| 管理页「功能配置」 | 修改多个字段后点「保存全部」：**一次密码、一次批量请求**；任一字段非法则全部不写 |
| 直接改 `appsettings.json` | 保存后按字段的生效级别生效；不确定时点一次「重启业务任务」 |
| 配置下载 | 需要管理密码；页面显示密码为 `***`，下载的是完整原文（按设计） |
| 测试数据库连接 | 免鉴权，但**只测试已保存配置**；改了表单要先保存再测 |

| 生效级别 | 含义 |
|---|---|
| **Hot** | 保存后下一轮循环即生效，无需重启 |
| **InnerRestart** | 需要「重启业务任务」重建内层宿主 |
| **ProcessRestart** | 需要「重启服务进程」（WebAdmin:BindIp/Port 等） |

---

## 4. Web 管理界面

### 4.1 访问与鉴权

- 地址：`http://<服务器IP>:5100`
- 破坏性动作（保存配置、下载配置、重启、升级、清错误、删队列）需要管理密码：**有有效「验证记忆」短令牌时不弹密码**，否则每次弹一次；
- 鉴权方式：一次性 nonce（60 秒、一次一用）+ `SHA-256(密码 + nonce)`；**不建长会话**，只在默认 5 分钟内用一次**短令牌**（见下方「管理验证记忆」）；
- 连续做多个动作 = 令牌有效期间**不再弹密码**；令牌失效（过期 / 刷新页面 / 改密码 / 重启服务）后，每个动作各弹一次；一次「保存全部」只算一次；
- 401 = 密码错误/验证已过期；403 = 后台未配置 `AdminPassword`；
- 401 后会弹一次「重新输入」：**留空或取消都视为中止本次动作**（不会拿上一次的密码再试一次），页面提示「已取消」；
- 管理密码只能改后台 `appsettings.json` 的 `WebAdmin:AdminPassword`，页面不显示、不可改。

**管理验证记忆（默认开启，5 分钟）**

- 密码验证成功后，服务端发一个**短令牌**（响应头 `X-Admin-Token`），**5 分钟内**的后续破坏性动作**不再弹密码**（页面自动带上该令牌）；
- 令牌**只存浏览器内存**（不落 localStorage/sessionStorage）→ **刷新页面即失效**，刷新后的第一个动作会重新弹密码；
- 服务端令牌只存内存、**不保存明文密码**（只存管理密码的 SHA-256 指纹）→ **改管理密码**或**重启服务**后旧令牌立即失效（页面会重新弹密码）；
- 时长**固定 5 分钟、不滑动续期**（不是「每次操作续 5 分钟」）；
- 想恢复「每个动作都弹一次密码」：把后台 `appsettings.json` 的 `WebAdmin:AuthMemoryMinutes` 设为 `0`（热生效，无需重启）；
- 令牌是 **bearer 凭证**（拿到就能用）：HTTP 明文下抓包者可在其有效期内重放 → 建议用 HTTPS 反代后再开放管理界面；
- 「下载配置原文」返回**全部明文密码**，始终重新输入密码（不借用令牌）。

### 4.2 仪表盘

- 每个模块一张健康卡片：`healthy` / `degraded` / `stopped`；
- 显示最近错误摘要、待处理队列、版本、重启状态；
- 顶部有「待重启」横幅时，按提示点「重启业务任务」或「重启服务进程」。

常见 `degraded` 原因：

| 文案 | 含义 | 处理 |
|---|---|---|
| `Aptio not connected` | Socket 未连上/半开 | 查 IP/端口/防火墙；看 Aptio 是否在线 |
| `Database unavailable` | MySQL 连不上 | 查连接串、账号、网络 |
| `Dispose status update failed` | DB 写入失败 | 查 DB 锁/权限；记录会重试 |
| `Aptio slow link` | STATUS 超过探测阈值 | 观察是否网络抖动；单飞会避免重复拆链 |

### 4.3 功能配置

- 按模块分组显示字段；密码字段显示 `已设置（留空保持不变）`；
- 改完点「保存全部」：一次密码、一次 `PUT /api/config/batch`；
- 每个字段旁标注生效级别（Hot / 重启业务任务 / 重启进程）；
- 「测试连接」只测**已保存**配置，不会测未保存的表单值。

### 4.4 丢弃队列

- 显示 `sam_dispose_status` 记录：条码、位置、架、`send`、`disposed`、`checkcount`、`resend_count`、时间；
- 状态含义：

| `send` | 含义 |
|---|---|
| `0` | 待发送 |
| `1` | 已投递，等待 `t_location = &3-{node}-%` 确认 |
| `2` | 未知节点跳过（sentinel） |
| `3` | 重发次数用尽/失败，等待人工处置 |

- 批量动作：重新检查、重置（回 `send=0`）、删除；每次动作弹一次密码；
- 未勾选任何记录时只提示，不发请求。

### 4.5 错误日志 / 日志文件

- 错误页：按天/模块/级别查询，可清空（需要密码）；
- 日志页：列出 `logs/` 下的文件，可查看尾部、下载、打包（按天/模块）；
- 日志默认保留 30 天，按 10 MB 轮转。

### 4.6 API 说明页

管理页「API 说明」列出全部端点、方法、示例。常用：

| 方法 | 端点 | 说明 |
|---|---|---|
| GET | `/api/health` | 健康状态 |
| GET | `/api/version` | 版本 |
| GET | `/api/auth/challenge` | 取一次性 nonce（免鉴权） |
| GET | `/api/config` | 读配置（密码打码） |
| GET | `/api/config?path=Aptio:Port` | 读单个配置 |
| PUT | `/api/config/batch` | 批量保存（需鉴权） |
| GET | `/api/config/download` | 下载完整配置原文（需鉴权） |
| POST | `/api/config/test-db?target=aptio` | 测试已保存配置（免鉴权） |
| GET | `/api/dispose/pending` | 丢弃队列 |
| POST | `/api/dispose/pending/recheck` | 重新检查 |
| POST | `/api/dispose/pending/reset` | 重置 |
| POST | `/api/dispose/pending/delete` | 删除 |
| GET | `/api/errors` | 错误记录 |
| DELETE | `/api/errors` | 清空错误（需鉴权） |
| GET | `/api/logs/list` / `/api/logs/view` | 日志列表/查看 |
| POST | `/api/update/upload` / `/api/update/apply` | 上传/应用升级包（需鉴权） |
| POST | `/api/restart` | 重启业务任务（需鉴权） |
| POST | `/api/restart/process` | 重启服务进程（需鉴权） |

鉴权头：`X-Admin-Nonce` + `X-Admin-Hash`。

---

## 5. 样本丢弃流程（重点）

### 5.1 业务目标

当 SRM 内样本数超过当前时间段的 `Threshold` 时，把**超期存储**的样本丢弃到 IOM 废料口，直到数量回落到阈值以下。

**确认口径**：

- Aptio Host LIS 的 `COMMENT S002` / `ORDER` **没有 ACK**；写成功 = 已投递；
- 是否真的丢弃，只看数据库：`t_sample.t_location` 变为 `&3-{节点}-%` 即视为已丢弃；
- 另有每 60 秒一次的补查（`send=1 AND disposed=0`），命中即置 `disposed=1`。

### 5.2 候选来源：视图还是存储过程

| `CommandType` | `CommandName` | 代码行为 | `DisposeByRack` |
|---|---|---|---|
| **0（默认）** | 视图名 | `SELECT * FROM 视图 LIMIT N` | **生效** |
| **1** | 存储过程名 | `CALL 存储过程()`（**不传参数**） | 不生效，由过程内部排序 |

视图必须返回：

| 列（别名） | 必须 | 用途 |
|---|---|---|
| `barcode` | ✅ | 条码；底层 `sample_id AS barcode` |
| `location` | ✅ | 位置；底层 `t_location AS location` |
| `update_time` | ✅（按架排序） | 按架 SQL 用 `MIN(update_time)`；没有则自动回退原查询 |
| `patient` | 推荐 | 写入队列/显示 |
| `stype` | 推荐 | 底层 `s_type AS stype` |
| `Res1` / `Res2` | 推荐 | 底层 `res_1 AS Res1`、`res_2 AS Res2`（Dapper 默认不认下划线） |

> “超期”条件由视图/存储过程定义，UniFlow 不校验；现场用 `SHOW CREATE VIEW` 核对。

### 5.3 状态机（默认单条模式）

```
None ──样本数>阈值──▶ Ready ──STATUS OK──▶ 发 COMMENT ──send=1──▶ Ack
  ▲                                                              │
  │                                                              ├─ 查到 &3 → disposed=1 → Dispose
  │                                                              └─ 未查到 → checkcount+1（最多 MaxWaitDiscardCount）
  └──────────────────────── STATUS-REQUEST ────────────────────────┘
```

- 每轮从 `send=0` 取 `ORDER BY ID` 的第一条；
- 投递成功 → `send=1`；
- 未知节点 + `SkipOnUnknownNode=true` → `send=2` 跳过，不再阻塞队首；
- 历史清理：`send>=1` 且 `send_time` 超过 10 天的记录自动删除。

### 5.4 发送窗口

`active = send=0（待发） + send=1 且 disposed=0（已发未确认）`

- `active >= MaxOnetimeSelectDiscardCount` → **不再插入新记录**；
- `inFlight = send=1 且 disposed=0`；`inFlight >= 窗口` → **不发任何 COMMENT（含 STATUS）**；
- 有 `send=0` 时先发（不受窗口插入限制）；
- 窗口满只等待，不丢记录。

### 5.5 5 分钟重排队与 24 次失败

- 每 `ResendIntervalMinutes`（默认 5 分钟）检查一次；
- 候选：`send=1 & disposed=0 & send_time` 超过 5 分钟 & `resend_count < MaxResendCount`；
- 只有节点 `ON` + 状态 `G/Y` + 错误码允许 → 放回 `send=0`（**不直接发丢弃命令**）；
- 真正重发成功时 `resend_count + 1`，`send_time` 更新；
- 24 次后 `send=3`（失败，页面人工处置），腾出窗口；
- **死锁修复**：窗口满时仍允许本轮重排队（有到期候选才发 1 条 STATUS），把记录放回 `send=0` 释放窗口；无候选不发 STATUS。

### 5.6 快速填窗（可选，默认关闭）

`EnableFastFill=true` 时：

- 一个 tick 内连续发 COMMENT，直到 `inFlight` 到窗口（默认 10）；
- 每条间隔 `FastFillIntervalMs`（默认 1000 ms，硬钳 0~5000）；
- 不再逐条进 Ack；确认改为批量：每 `FastFillConfirmIntervalSeconds`（默认 5，硬钳 1~60）一条 SQL 确认所有在途记录；
- 投递失败 / DB 写失败 / 节点未就绪 / 空条码 / skip 失败 → 立即停止本轮；
- 未知节点 skip 成功继续下一条，单轮最多 200 条；
- `MaxOnetimeSelectDiscardCount<=0` 时**不走**快速填窗，回退单条模式；
- 默认关闭；建议先用 1000 ms 间隔观察日志。

### 5.7 按架排序（`DisposeByRack`，默认开启）

**目的**：让窗口内的 10 条尽量来自同一个架，减少 SRM 搬架次数。

**架键**：

| `t_location` 形态 | 架键 | 示例 |
|---|---|---|
| `&1-{节点}-{架号}-{层}-{槽}-{位}` | `&1-{节点}-{架号}` | `&1-13-000078-05-14-01` → `&1-13-000078` |
| `&2-{节点}-000000-00-{槽}-{位}` | `&2-{节点}-{槽}` | `&2-01-000000-00-18-01` → `&2-01-18` |
| `&3-...`（已丢弃哨兵） | — | **排除** |
| `&0`/NULL/短格式/其它 | `~other` | **保留、排最后**，不静默丢弃 |

**排序**：

1. `~other` 组最后；
2. 架内合格样本数降序；
3. 架的最早 `update_time` 升序；
4. 同架内 `location` 升序（位置 01→48）。

**效果**：窗口 10 条基本来自同一架；一个满架 48 条分 5 批，但始终是同一架。若现场确认 SRM 会把架留在丢弃位，可再把窗口调到 48（一次丢完整架）。

**回退**：`DisposeByRack=false` 恢复原顺序；按架 SQL 失败（视图缺 `update_time`、MySQL 版本不支持等）自动回退原查询，10 分钟一条 Warning，丢弃不停摆。

**性能**：按架 SQL 会扫视图两次；14k 行实测约 0.2~0.3 秒（原约 0.05 秒），对 3 秒循环可接受。视图特别大时建议 `EXPLAIN ANALYZE`。

### 5.8 典型时间线

以满架 48 条、窗口 10、快速填窗关闭为例：

```
第 1 轮：插入 10 条 → 逐条发 COMMENT（每条约 3 秒）→ 确认
第 2~5 轮：同一架继续 10 条/轮 → 架清空
若某条未确认：5 分钟后重排队（send=0）→ 再发
24 次仍失败：send=3（失败，人工处置）
```

### 5.9 关键日志

| 日志 | 含义 |
|---|---|
| `Inserted dispose record: ... Rack=...` | 插入候选，显示架键 |
| `State → Ready/Ack/Dispose` | 默认状态机流转 |
| `Fast-fill sent N record(s), inFlight=X/10` | 快速填窗发送 |
| `Fast-fill confirm: N ... confirmed` | 批量确认命中 |
| `Dispose window is full: ...` | 窗口满 |
| `Requeued N record(s) for resend` | 5 分钟重排队 |
| `Dispose failed after 24 resend(s) ... send=3` | 重发用尽 |
| `{Barcode} confirmed disposed` | 已确认丢弃 |
| `rack-aware query failed, fallback ...` | 按架 SQL 回退 |

### 5.10 现场验证清单

1. `Aptio:SrmNodeIds` 包含数据里的节点（如 `13`；有 `&2` 时含 `01`）；
2. `CommandType=0` 时视图有 `barcode`、`location`、`update_time`；
3. `t_location` 符合 `&1-{节点}-{架号}-...` / `&2-...`；
4. 先开 1~2 个样本，观察 `COMMENT S002`、`&3-` 确认、`disposed=1`；
5. 再开窗口/快速填窗/按架排序；
6. 观察 `Fast-fill`、`Requeued`、`window is full` 日志；
7. DMS 事务回滚只在 InnoDB 生效；MyISAM 表需现场确认（`SHOW TABLE STATUS`）。

---

## 6. 其它模块

### 6.1 SrmExport

- 每天 `Aptio:SrmExport:ExportTime`（默认 08:30）导出前一日丢弃记录；
- 查询 `t_location LIKE '&3-{node}-%'`，输出 TXT 到 `OutputPath`（相对安装目录或绝对路径）；
- 文件保留 `LogRetentionDays` 天；
- 时间窗逻辑：到点后当天一次；DB 失败不标记当天，可补；重启可补。

### 6.2 WorkListCleaner（Immulite）

- 仅 Windows（Access/OleDb）；
- 检测超时未测样本加 `ERR` 前缀、不可用结果加 `NA` 前缀；
- 可通过 ASTM 向 Centralink 推送标志；
- 配置在 `WorkListCleaners` 数组，支持多实例。

### 6.3 DMSAutoOrder

| 服务 | 作用 |
|---|---|
| PitStopMonitor | 扫描 `pitstop%`，超时只删除对应 `idpitstop`（不再清整表） |
| StatusCorrection | 清理 E/R/P；按模式 1/2/3 修正 flgtohost/结果 |
| SampleCleanup | 测试触发删除；多表删除用事务；回滚不发 Aptio 报文、保留计时重试 |

> DMS 多表删除事务只在 **InnoDB** 生效；MyISAM 表不会回滚，现场需确认引擎。

### 6.4 Delivery / Priority / TestNameDispose

- Delivery：`COMMENT S002^{barcode}\DELIVER^S`
- Priority：`COMMENT S010^{barcode}\S^S`（STAT）
- TestNameDispose：`COMMENT S002^{barcode}\TRASH^S`
- 由 `AptioBatchScannerWorker` 按测试名触发，独立于 DisposeSample 状态机。

---

## 7. 日志、健康与故障排查

### 7.1 日志

```
logs/
├── DisposeSampleWorker_2026-11-09.log
├── SrmExportWorker_2026-11-09.log
├── DmsAutoOrderWorker_2026-11-09.log
├── ...（按模块分文件）
└── UniFlow_Error_2026-11-09.log   # 所有 ERROR 及以上
```

- 默认 10 MB 轮转、保留 30 天；
- 页面「日志文件」可查看/下载/打包；
- 敏感信息（DB 密码）不会写入日志。

### 7.2 常见问题

| 现象 | 可能原因 | 处理 |
|---|---|---|
| 页面 401 | 密码错/挑战过期/验证记忆已过期 | 重新输入；确认 `AdminPassword`；页面刷新后需重新输入（令牌只在内存） |
| 页面 403 | 后台 `AdminPassword` 为空 | 在 `appsettings.json` 设置密码 |
| `Aptio not connected` | IP/端口/防火墙/Aptio 未开 | 查 2055、网络、Aptio 状态 |
| `Database unavailable` | MySQL 连不上/账号错 | 查连接串、账号、网络 |
| 样本不丢 | `SrmNodeIds` 不含节点 | 加节点号；`SkipOnUnknownNode` 行为 |
| 样本不丢 | 视图没有候选 | `SHOW CREATE VIEW`；手动 `SELECT * FROM view LIMIT 10` |
| 样本不丢 | 节点非 ON/G/Y 或错误码不允许 | 看 STATUS 日志；必要时调 `AllowedSrmErrorCodes` |
| 一直重复 COMMENT | 5 分钟重排队；`resend_count` 增加 | 正常重试；24 次后 `send=3` |
| 窗口满不动 | 10 条未确认 | 看 `disposed`；5 分钟重排队；检查 `&3` |
| 保存配置后不生效 | 字段是 InnerRestart/ProcessRestart | 按提示重启业务任务/进程 |
| 升级后还是旧程序 | 用了旧安装脚本/未 publish | 用 `install-service.ps1` 默认 publish；不要 `-SkipPublish` |
| DMS 删除半截 | MyISAM 不支持事务 | 改 InnoDB 或接受限制 |
| 配置保存提示“部分未保存” | 旧版本；现为一次批量 | 升级到 1.0.6.6+ |

### 7.3 诊断命令

```bash
# 版本/健康
curl http://127.0.0.1:5100/api/version
curl http://127.0.0.1:5100/api/health

# 服务
systemctl status uniflow
journalctl -fu uniflow
# Windows
Get-Service UniFlow

# 数据库
mysql -h<host> -u<user> -p <db>
SHOW CREATE VIEW view_overtimestoragesample;
SELECT barcode,location,send,disposed,resend_count FROM sam_dispose_status ORDER BY ID DESC LIMIT 20;
SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE '&1-13-%';
SELECT sample_id,t_location FROM t_sample WHERE t_location LIKE '&3-13-%' LIMIT 10;
```

---

## 8. 安全

- 默认假设**局域网可信**；`WebAdmin:BindIp=0.0.0.0` 时同网段可访问；
- **首次部署必须改** `WebAdmin:AdminPassword`（默认 `Advia1`）；
- 破坏性动作每次密码；nonce 60 秒一次性；HTTP 明文下仍有 60 秒重放窗口；
- 「管理验证记忆」开启后（默认 5 分钟）改用短令牌 `X-Admin-Token`：bearer 凭证、不落盘、刷新/改密码/重启即失效，但 **HTTP 明文下重放窗口变成最长 5 分钟** → 建议上 HTTPS，或把 `WebAdmin:AuthMemoryMinutes` 设为 `0`；
- 网络不可信时：用反向代理加 HTTPS + IP 白名单，或只绑内网；
- 升级包目前**没有签名校验**，只应使用可信来源的包；
- 单实例保护：`Global\UniFlow`（Windows 权限不足回退 `Local`）；
- 建议：限制 5100/2055/3306 来源 IP；定期备份配置和数据库；日志目录权限最小化。

---

## 9. 备份与恢复

| 对象 | 建议 |
|---|---|
| `appsettings.json` | 每次升级前备份；含 DB 密码/管理密码，注意权限 |
| `logs/` | 按需归档；默认保留 30 天 |
| `uniflow_admin.db` | 管理库（错误/健康历史）；可随安装目录备份 |
| Aptio MySQL | 由 DBA 定期备份（`flexlab`） |
| DMS MySQL | 由 DBA 定期备份（`DMSCN`） |

恢复：停服务 → 恢复配置/数据库 → 启动 → 验证 `/api/health` 与丢弃队列。版本回滚见 §2.7。

---

## 10. 附录

### 10.1 数据库表（丢弃相关）

`sam_dispose_status`：

| 列 | 说明 |
|---|---|
| `ID` | 自增主键 |
| `barcode` | 样本条码（NOT NULL） |
| `patient` / `stype` | 病人/样本类型 |
| `location` | 位置 |
| `update_time` | 视图给出的更新时间 |
| `res_1` / `res_2` | 旧程序保留字段 |
| `rack` | 架键（`&1-{节点}-{架号}` / `&2-{节点}-{槽}`） |
| `send` | 0 待发 / 1 已投递 / 2 跳过 / 3 失败 |
| `send_time` | 最近发送时间 |
| `disposed` | 1=已确认丢弃 |
| `disposed_time` | 确认时间 |
| `checkcount` | 默认单条模式的确认轮数 |
| `check_time` | 最近确认时间 |
| `resend_count` | 真正重发次数 |

`t_sample` 关键列：`sample_id`、`t_location`、`t_status`、`update_time`、`res_1`、`res_2`。

`t_location` 形态：

```
&1-{节点}-{架号}-{层}-{槽}-{位}     在用存储
&2-{节点}-000000-00-{槽}-{位}       另一类存储
&3-{节点}-000000-00-00-00           已丢弃/归档哨兵
&0-... / NULL                        特殊/未定位
```

### 10.2 文档索引

| 文档 | 内容 |
|---|---|
| `README.md` | 项目概览、快速开始 |
| `docs/Configuration-Manual.md` | 配置手册（按节） |
| `docs/CONFIG-REFERENCE.md` | 逐字段权威参考 |
| `docs/Deployment-Guide.md` | 部署配置手册 |
| `docs/Interface-Manual.md` | Aptio/ASTM 接口 |
| `docs/Functional-Design.md` | 功能设计 |
| `docs/DATABASE-STRUCTURE.md` | 数据库结构 |
| `docs/dispose-state-machine.html` | 丢弃状态机图 |
| `docs/CHANGELOG-1.0.7.1.md` | 按架排序 + 使用说明书版本说明 |
| `docs/CHANGELOG-1.0.7.0.md` | 快速填窗版本说明 |
| `docs/Maintenance-Order-2026-11-07.md` | 维护单/已知问题 |
| `docs/Release-Process.md` | 发布流程 |

### 10.3 术语

| 术语 | 含义 |
|---|---|
| **SRM** | 样本冷藏/存储模块 |
| **IOM** | 废料输出模块 |
| **LIS** | 实验室信息系统 |
| **DMS** | 数据管理系统（DMSCN 库） |
| **PitStop** | DMS 卡死任务表 |
| **窗口** | `MaxOnetimeSelectDiscardCount`，未丢弃总数上限 |
| **inFlight** | `send=1 & disposed=0` 在途记录 |
| **send=0/1/2/3** | 待发/已投递/跳过/失败 |
| **`&3-{node}-%`** | 已丢弃样本的位置 |
| **rack** | 架键：`&1-{节点}-{架号}` 或 `&2-{节点}-{槽}` |
| **STB / GTO** | SRM 模式：Stand-by / Global Transport Off |
| **G/Y/P** | SRM 状态：Green / Yellow / Pink |

### 10.4 版本摘要

| 版本 | 主要内容 |
|---|---|
| 1.0.6.6 | 管理鉴权、丢弃窗口/重试、Socket 队列、DMS/PitStop 修复 |
| 1.0.6.7 | 维护版：死代码、配置单源、日志锁、安装脚本、XSS 闸门 |
| 1.0.7.0 | 快速填窗（默认关）+ 批量确认 + 重排队死锁修复 |
| 1.0.7.1 | 按架排序（默认开）+ `~other` 兜底 + 回退测试 |
| 1.0.7.2 | 管理验证 5 分钟记忆（服务端短令牌） |

---

> 本说明书随代码维护；如与 `CONFIG-REFERENCE.md` 冲突，以配置参考和实际代码为准。
