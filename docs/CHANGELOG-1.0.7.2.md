# UniFlow v1.0.7.2 改动记录

- 日期：2026-11-10
- 版本：1.0.7.1 → **1.0.7.2**
- 部署包：`UniFlow-1.0.7.2-linux-x64.tar.gz` / `UniFlow-1.0.7.2-win-x64.zip`
- 概述：**管理验证 5 分钟记忆（服务端短令牌）**。本版给 Web 管理界面的**破坏性动作**加了一条「验证记忆」：管理密码**验证成功一次**后，服务端发一个**短令牌**，默认 **5 分钟内**后续的破坏性动作**不再重复弹密码**；令牌**只存在服务端内存 + 浏览器内存**，页面刷新 / 改管理密码 / 服务重启后立即失效。

> 本版**新增 1 个配置字段** `WebAdmin:AuthMemoryMinutes`（**热生效**），**默认 `5`** → **升级即生效**。
> 但它**只改「是否重复弹密码」**，**不改任何业务判定**（保存/重启/升级/丢弃队列的逻辑一律原样）—— 见第一节与第六节。
> 实现依据为工作区未提交的「管理验证记忆」改动；独立复核记录见 `docs_20261103/REVIEW_20261110_admin-auth-memory_L3.md`（结论 PASS，无 P0/P1；P2×1、P3×3，均已在本版发布前处理或登记）。
> ⚠️ **本版尚未在真实客户浏览器 / HTTPS 反代环境 / Windows Service / Docker 容器上做过端到端验收** —— 第七节「已知问题 / 延后项」与其中的「现场验收清单」必须逐项确认后再投产。

---

## 一、需求与行为：管理验证 5 分钟记忆（服务端短令牌）

### 1.1 为什么改

1.0.7.1 的口径是「**每次破坏性动作都弹一次管理密码**」：一次性 nonce（60 秒、一次一用）+ `SHA-256(密码 + nonce)`，**每个动作各弹一次**（一次「保存全部」只算一次）。现场做一轮配置调整 / 一次升级往往要连着点好几个破坏性按钮，于是**同一个密码要在同一个页面上连输多次** —— 体验差，且**在共享值班终端上反复输入长口令**也不利于口令卫生。

本版引入「**验证记忆**」：一次密码验证 → 服务端签发**短令牌** → 令牌有效期内（默认 5 分钟）**不再弹密码**。

### 1.2 行为口径

| 事项 | 口径 |
|---|---|
| 令牌形态 | **32 字节随机 → 64 位十六进制小写**（`X-Admin-Token` 响应头下发） |
| 令牌存在哪 | **只存服务端内存 + 浏览器内存**（前端模块级变量）；**不落 localStorage / sessionStorage** |
| 有效期 | **固定 `AuthMemoryMinutes` 分钟**（默认 5），**从密码验证成功起算**；**不滑动续期**（用一次不重置计时） |
| 何时失效 | **页面刷新**（浏览器内存丢）/ **新标签页** / **改管理密码** / **服务进程重启** / 令牌自然过期 |
| 服务端存什么 | 只存令牌本身 + 过期时刻 + **管理密码的 SHA-256 指纹**（`PasswordFingerprint`）—— **不存明文密码** |
| 令牌能否重复用 | **可以**（不像 nonce 那样一次性）；nonce 仍是**一次性**（成败都消费） |
| 关闭方式 | `WebAdmin:AuthMemoryMinutes = 0` → 恢复「每个动作都弹一次密码」（**热生效**） |

### 1.3 与既有鉴权的关系（**不削弱**）

- 旧的 nonce/hash 流程**原样保留**：nonce **60 秒有效、一次性**、哈希**常量时间**比较、密码与哈希**不写日志**；
- 令牌只是**在 nonce/hash 之上**加的一条「免重复输入」的快捷通道，**不是**替代品 —— 令牌失效后**自动回落到 nonce/hash**；
- 未配置管理密码（`WebAdmin:AdminPassword` 为空）→ 仍然**一律 403**；
- 令牌无效 / 过期 / 未知 → **401**（`{"success":false,"error":"Admin authentication failed"}`）。

---

## 二、配置：`WebAdmin:AuthMemoryMinutes`

| 事项 | 口径 | 位置 |
|---|---|---|
| 键名 | `WebAdmin:AuthMemoryMinutes` | `code/src/UniFlow/Common/Models/ConfigShared.cs:223` |
| 类型 | `int` | 同上 |
| 默认值 | **`5`** | `ConfigShared.cs:223`、`ConfigService.cs:369` |
| 取值范围 | **0 ~ 60**（超出按 `Math.Clamp` 截断；**负数 → 0**） | `ConfigService.cs:372`、`:390`、`:392` |
| `0` 的含义 | **关闭验证记忆** → 恢复「每个动作都弹一次密码」 | `AdminAuthService.cs:130` |
| 缺失 / 非法值 | **回落默认 5**（读不到不抛异常，配置损坏不应把管理界面打成 500） | `ConfigService.cs:387`、`:393`、`:395-399` |
| 是否热生效 | **是**（`[Restart(RestartLevel.Hot)]`，每次动作现读配置） | `ConfigShared.cs:223`、`ConfigService.cs:382-399` |
| 仓库默认 `appsettings.json` | **已写入 `"AuthMemoryMinutes": 5`** | `code/src/UniFlow/appsettings.json:140` |
| `DefaultConfig` | **已加 `AuthMemoryMinutes: 5`** | `code/src/UniFlow/WebAdmin/Services/DefaultConfig.cs:36` |
| 页面「功能配置」表单 | **已加** `WebAdmin:AuthMemoryMinutes`（number，min 0 / max 60） | `code/src/UniFlow/wwwroot/app.js:830` |
| API 说明页 EXAMPLES | **已加** `"WebAdmin:AuthMemoryMinutes": "5"` | `code/src/UniFlow/wwwroot/app.js:702` |
| 文档 | `docs/CONFIG-REFERENCE.md:415`（字段表）+ `:436`（「管理验证记忆」小节）；`docs/Configuration-Manual.md:343`、`:348` | — |

> 现场 `appsettings.json` **不需要改** —— 缺失时按默认 5 生效；**要恢复 1.0.7.1 的「每次都弹密码」行为**才需要显式写 `0`。

---

## 三、后端

### 3.1 `AdminAuthService`（`code/src/UniFlow/WebAdmin/Services/AdminAuthService.cs`）

| 新增 / 改动 | 位置 | 说明 |
|---|---|---|
| `AuthTokenResult` 记录 | `:11` | `(bool Ok, string? Token, int ExpiresInSeconds)` |
| `MaxMemoryMinutes = 60` | `:42` | 上限常量 |
| `DefaultMemoryMinutes = 5` | `:45` | 兜底常量 |
| `MaxTokens = 4096` | `:51` | 内存中令牌数量上限（防御无限刷令牌撑爆内存） |
| `_tokens` 并发字典 | `:56` | `ConcurrentDictionary<string, TokenEntry>` —— **仅存内存**，进程重启即全部失效 |
| `TokenEntry` 记录 | `:60` | `(DateTimeOffset ExpiresAt, string PasswordFingerprint)` —— **不含明文密码** |
| `ActiveTokenCount` | `:71` | 当前有效令牌数（测试/观测用） |
| **`TryValidateAndIssueToken`** | `:123-147` | 校验 nonce/hash → 按 `memoryMinutes` 签发令牌；`minutes <= 0` → `(true, null, 0)` **不发令牌**；令牌 = `Convert.ToHexString(RandomNumberGenerator.GetBytes(32))`（`:143`）；满 4096 时挤掉**最早过期**的一条（`:134-139`） |
| **`TryValidateToken`** | `:160-190` | 空/未知 → false；过期 → 移除并 false（`:169-173`）；**指纹与当前管理密码不一致（密码已改）→ 移除并 false**（`:176-180`）；有效 → true 并返回剩余秒数 |
| **`PasswordFingerprint`** | `:193-194` | `SHA-256(管理密码)` 的十六进制小写 —— **只存指纹，不存明文** |
| `CleanupExpired` | `:201-210` | 惰性清理过期 nonce 与令牌（发挑战/发令牌时顺带做，**不起定时器线程**） |

- **令牌固定 5 分钟、不滑动续期**：`ExpiresAt` 在**签发时一次算定**，验证时只读不改（`:186-189`）；
- **密码变更自动失效**：服务端比对指纹（`:176-180`），改密码后旧令牌**立即不可用**；
- **进程重启即失效**：令牌只在进程内存（`:56`）。

### 3.2 `AdminAuthFilter`（`code/src/UniFlow/WebAdmin/Services/AdminAuthFilter.cs`）

| 环节 | 位置 | 行为 |
|---|---|---|
| 读取令牌 | `:43` | `request.Headers["X-Admin-Token"]` |
| ① 令牌优先 | `:47-49` | 令牌**有效** → **直接放行**（不再重复校验密码） |
| ② 无 nonce/hash | `:51-57` | 既无有效令牌、又没带 nonce/hash → **401** |
| ② nonce/hash 回落 | `:58-59` | 令牌**无效/过期**但**带了 nonce/hash** → **继续走原 nonce 校验流程**（不是直接拒绝 —— 否则旧令牌会挡住「用新密码重新鉴权」） |
| 403 优先 | `:37-40` | 管理密码未配置 → **403**，且**先于**令牌判定 |
| 下发响应头 | `:65-70` | nonce/hash 校验通过 → 响应头 **`X-Admin-Token`** + **`X-Admin-Token-Expires-In`**（秒）；**必须在 `next()` 之前写**（响应一旦开始输出就不能再改头） |

### 3.3 覆盖范围（**所有**挂 `AdminAuthFilter` 的端点，10 个）

| 端点 | 过滤器挂载位置 |
|---|---|
| `POST /api/restart`（重启业务 worker） | `code/src/UniFlow/WebAdmin/ApiEndpoints.cs:75` |
| `POST /api/restart/process`（进程重启） | `ApiEndpoints.cs:90` |
| `POST /api/dispose/pending/reset`（队列**重排队**） | `ApiEndpoints.cs:224` |
| `POST /api/dispose/pending/delete`（队列**删除**） | `ApiEndpoints.cs:251` |
| `DELETE /api/errors`（**清错误**） | `ApiEndpoints.cs:305` |
| `GET /api/config/download`（**下载配置原文**） | `ApiEndpoints.cs:337` |
| `PUT /api/config`（**保存**单个配置项） | `ApiEndpoints.cs:381` |
| `PUT /api/config/batch`（**批量保存**） | `ApiEndpoints.cs:448` |
| `POST /api/update/upload`（**上传**升级包） | `ApiEndpoints.cs:679` |
| `POST /api/update/apply`（**应用升级**） | `ApiEndpoints.cs:749` |

> 挂载清单来源：`grep -n "AddEndpointFilter<AdminAuthFilter>()" code/src/UniFlow/WebAdmin/ApiEndpoints.cs` → `:75 :90 :224 :251 :305 :337 :381 :448 :679 :749`（`:53` 是注释行）。

---

## 四、前端（`code/src/UniFlow/wwwroot/app.js`）

| 事项 | 口径 | 位置 |
|---|---|---|
| 令牌只存内存 | 模块级 `let _adminToken` / `_adminTokenExpiresAt`；**不写 localStorage / sessionStorage** | `:31-32` |
| 取用令牌 | 过期即清并返回 `null` | `:98-102` |
| 丢弃令牌 | 令牌失效/过期/改密码后清空 | `:105-109` |
| 采纳令牌 | 从响应头 `X-Admin-Token` / `X-Admin-Token-Expires-In` 取值存内存 | `:113-120` |
| **有效令牌不弹密码** | `adminAuth()`：有有效令牌 → 直接返回 `{ token }`，**不取 nonce、不算 hash** | `:126-135` |
| 组装鉴权头 | 有令牌 → 只带 `X-Admin-Token`；否则取一次性新 nonce 现算 `X-Admin-Hash` | `:141-154` |
| **401 清令牌并回落 nonce/hash** | 401 → 清令牌 → 弹「重新输入」→ 换新 nonce 重试（**最多 3 次尝试**） | `:174-190` |
| **401 重输留空 = 取消** | 留空或取消 → **视为中止本次动作**（不再沿用上一次的密码白耗一次尝试） | `:178-184` |
| 网络错误保留令牌 | `fetch` 抛异常 → 直接返回，**不清令牌**（服务端可能只是暂时不可达） | `:171-172` |
| 成功后采纳令牌 | 任何非 401 响应都采纳服务端下发的令牌（含业务层 400 → 密码没错就不该再弹一次） | `:191-193` |
| **多请求动作复用**（上传 + 应用升级） | 上传走 XHR，从响应头取令牌（`:1567`）→ 后续 apply（`:1631`）**复用同一令牌**，不再弹密码 | `:1553-1640` |
| **下载配置原文始终重输密码** | `adminAuth('下载配置原文', { forcePrompt: true })` → **强制弹密码，不借用令牌** | `:1414` |
| **下载不采纳下载响应的令牌** | 下载函数（`downloadConfig()`，`:1407-1453`）内**没有** `rememberAdminToken` 调用 → 不因一次下载延长信任窗口 | `:1407-1453` |
| **页面刷新重新弹** | 令牌只在内存 → 刷新后第一个破坏性动作重新弹密码 | `:31-32`（无任何持久化写入） |

> API 说明页（`code/src/UniFlow/wwwroot/index.html:149`）已同步说明：鉴权成功会下发 `X-Admin-Token`，其后的破坏性动作只带该令牌即可。

---

## 五、测试

### 5.1 构建

```
$ export HOME=/root; /usr/share/dotnet/dotnet build code/src/UniFlow/UniFlow.csproj -c Release
    14 Warning(s)
    0 Error(s)
```

14 条 warning **全部为历史存量**（`WebAdmin/Services/ConfigService.cs`、`WorkListCleaner/Services/ImmuliteCleaner.cs`、NU1902 / NU1903 依赖告警）。

### 5.2 单元 / 集成测试

| 环境 | 命令 | 结果 |
|---|---|---|
| **有 MySQL** | `UNIFLOW_TEST_MYSQL_HOST=127.0.0.1 UNIFLOW_TEST_MYSQL_DB=uniflow_loc_analysis MYSQL_PWD=… dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 523, Skipped: 0, Total: 523, Duration: 51 s` |
| **无 MySQL** | 同上但**不设** MySQL 环境变量 | `Passed! - Failed: 0, Passed: 519, Skipped: 4, Total: 523, Duration: 52 s` |

- 无 MySQL 时跳过的是 **4 条** MySQL 集成用例（`DisposeDatabaseServiceRackTests` 下 4 条 `OnRealMySql`），**不是**失败；
- 本版新增/相关的鉴权用例集中在 `code/tests/UniFlow.Tests/WebAdmin/Services/AdminAuthServiceTests.cs`（26 个 `[Fact]`/`[Theory]`）与 `code/tests/UniFlow.Tests/WebAdmin/Api/AdminAuthApiTests.cs`（32 个 `[Fact]`/`[Theory]`），覆盖：令牌签发 / 令牌-only 放行 / 无效令牌 401 / `AuthMemoryMinutes=0` 不发令牌 / 改密码后令牌失效 / 令牌存储有界 / 令牌只存浏览器内存等。

### 5.3 前端与端到端

| 项 | 命令 | 结果 |
|---|---|---|
| JS 语法 | `node --check code/src/UniFlow/wwwroot/app.js` | 无输出，`rc=0`（node v24.16.0） |
| Playwright（**冻结副本**） | `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-current npx playwright test` | **59 passed**（44.8 s） |

> 说明：`run-test/playwright/playwright.config.js` 的**默认** `APP_DIR=/tmp/uniflow-e2e/app` 是 **11-07 的旧产物**（`grep -c X-Admin-Token app.js = 0`），用它跑出来的结果**不覆盖本版改动**。本次**显式指定** `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-current` —— 该副本是**按 1.0.7.2 工作区重新 publish** 的产物（`wwwroot/app.js` 与仓库源文件 **md5 一致**：`c97581c546c1176ada02cc34a148d401`）。
> 其中 UI-10 组 3 条（`:917` 第一次保存弹 1 次 / 5 分钟内第二次不弹 / 刷新后重新弹、`:971` 不落 localStorage/sessionStorage、`:1011` 401 重输留空 = 取消）全部通过。

### 5.4 版本冒烟（**跑的是安装包里的二进制**）

```
$ tar -xzf release/UniFlow-1.0.7.2-linux-x64.tar.gz -C smoke && cd smoke/uniflow && ./UniFlow &
$ curl -s http://127.0.0.1:5100/api/version
{"version":"1.0.7.2","informational":"1.0.7.2+3240cf195f28ed89b10785cb328dce920d59dcba"}
```

- `version` = **`1.0.7.2`** ✔；`informational` 带 HEAD 提交号 `3240cf1`（本版代码**尚未提交**，属预期）。

### 5.5 打包产物的鉴权冒烟（**跑的是安装包里的二进制**）

| 步骤 | 结果 |
|---|---|
| `GET /api/auth/challenge` → 带 `X-Admin-Nonce` + `X-Admin-Hash` 调 `PUT /api/config/batch` | 响应头 `X-Admin-Token: 0b4e7ee9…`（64 位 hex）+ `X-Admin-Token-Expires-In: 300`（= 5 分钟）✔ |
| 只带 `X-Admin-Token` 再调同一端点 | **鉴权通过**（返回业务层 400 `Missing required field: changes`，**不是 401**）✔ |
| 带一个无效 `X-Admin-Token` 且**不带** nonce/hash | **401** `Admin authentication failed` ✔ |

---

## 六、升级注意

### 6.1 默认行为变化（升级即生效）

- `WebAdmin:AuthMemoryMinutes` **默认 `5`** → **升级后破坏性操作的弹密码频率显著降低**（5 分钟内连续操作**不再弹**）；
- 需要恢复 1.0.7.1 行为 → 改 `appsettings.json` 的 `WebAdmin:AuthMemoryMinutes` 为 **`0`**（**热生效，无需重启**），或页面「功能配置」把「管理验证记忆(分钟)」设为 `0`；
- **业务逻辑零改动**：保存 / 批量保存 / 重启 / 进程重启 / 上传 / 应用升级 / 清错误 / 下载原文 / 队列重排队与删除的**判定逻辑一律原样**；1.0.7.0 的快速填窗、1.0.7.1 的按架排序、丢弃窗口 / 重试 / 重排队逻辑**全部原样保留**，本版未削弱。

### 6.2 安全边界（**必读**）

| 事项 | 口径 |
|---|---|
| **令牌是 bearer 凭证** | 拿到就能用。管理界面走 **HTTP 明文** → 抓包者可在**剩余有效期（最长 5 分钟）**内**重放**该令牌。**建议在管理界面前加 HTTPS 反代** |
| **关掉不等于立刻失效** | `AuthMemoryMinutes` 从 5 改成 0 后，**已签发的旧令牌最长仍可用到它自身的到期时刻（≤ 5 分钟）**（服务端令牌不会因为改配置而主动清空） |
| **时钟偏差可自愈** | 浏览器与服务端时钟有偏差时，前端可能**提前**判定令牌过期 → 触发 **1 次 401** 后**自动重新弹密码**并重新鉴权（**自愈**，不影响正确性） |
| **刷新 / 新标签页会重新弹密码** | 令牌只在内存 → **刷新页面或开新标签页**都会重新弹一次密码（这是**刻意**的安全取舍，不是缺陷） |
| **未配置管理密码仍 403** | `WebAdmin:AdminPassword` 为空 → 所有破坏性端点仍返回 **403**（与本版无关，行为不变） |
| **无效 / 过期令牌 401** | 且**带 nonce/hash** 时会**继续走原鉴权流程**（可用新密码当场恢复，不必刷新页面） |
| **下载配置原文始终重输密码** | 该端点返回**全部明文密码**，属敏感度最高的动作 → **始终重新输入密码**，且**不采纳下载响应里的令牌**（不因一次下载延长信任窗口） |

> **敏感现场建议**：`AuthMemoryMinutes` 设 `0`（每次都弹）或 `1~2` 分钟。

### 6.3 其它

- 配置键名 / 层级 / 其余默认值**全部不变**；仓库默认 `appsettings.json` **已含**该键（值 `5`）→ 现场配置文件**不写也能生效**；
- 令牌**不落盘**：服务端只在进程内存、浏览器只在页面内存 —— **不写 localStorage / sessionStorage**，浏览器关页即丢；
- 令牌与密码**都不写日志**（沿用既有口径）。

---

## 七、已知问题 / 延后项（**不阻塞本版**）

| 项 | 状态 | 说明 |
|---|---|---|
| **真实环境 E2E 未做** | ⚠️ **必须现场验证** | **未在真实客户浏览器 / HTTPS 反代环境 / 真实 Aptio 2055 / 真实 SRM / 真实冰箱 / 真实客户 MySQL / Windows Service / Docker 容器上验证过本版任何行为**。「5 分钟内不弹密码」「刷新后重新弹」「改密码后失效」「HTTPS 下令牌不被明文嗅探」等**均需现场确认**。本版的自动化验证**全部在 Linux 本机 + HTTP 明文 + 默认配置**下完成 |
| **M-09** `send` 已投递但 DB 写失败时重复下发 | 延后 | 命令已写入 socket，但 DB `send=1` 失败 → 保持 Ready，下一轮可能重发同一条 `COMMENT`。协议幂等（TRASH）通常无害；极端 DB 故障时重复指令 / 日志。**本版未改**。 |
| **M-10** 依赖漏洞升级 | 延后 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（NU1903，high）、`MySqlConnector` 2.4.0（NU1902，moderate）。当前使用面为本地 admin SQLite / 内网 MySQL，无直接可利用入口；升级需单独维护窗口 + 全量回归，**不与业务改动混提**。**本版未改**。 |
| **MyISAM / InnoDB 未区分验证** | ⚠️ 需现场确认 | 本机测试库 `uniflow_loc_analysis` 的 `t_sample` 是 **MyISAM**（无 MVCC、表级锁）；InnoDB 单语句内为一致性快照，风险更低但**未实测**。与本版功能无直接关系（令牌走内存，不碰数据库），但**沿用上一版的登记**。 |
| **按架 SQL 扫视图两次的性能需现场观察** | 需现场采集 | 1.0.7.1 引入的按架排序：按架 SQL **把视图扫了两遍**（外层出样本列 + 内层出 `update_time` 聚合）。本机实测 0.21~0.22 s（11844 行视图），3 s 循环可接受。**百万行级现场请 `EXPLAIN ANALYZE`**；必要时 `DisposeByRack=false` 回退。**本版未改**。 |
| **令牌上限挤旧用 `OrderBy` 快照** | P3 观察项 | 内存令牌满 4096 时，签发路径用 `OrderBy(ExpiresAt)` 排序挤掉最早过期的一条 → 满载时每次签发 **O(n log n)**（n ≤ 4096，实际签发频率极低）；并发挤旧时可能多挤 1 条，**安全无虞**。可用最小堆优化，**非必需，本版未做** |
| **`expiresIn` 用 `Math.Ceiling`** | P3 观察项 | 浏览器按 `Date.now() + exp` 本地判断到期，可能比服务端**提前**几毫秒过期 → 多一次 401 后**自愈**（重新弹密码）。**可忽略** |
| **下载原文时服务端仍会签发令牌** | P3 观察项 | 客户端**刻意不采纳**下载响应里的令牌，但服务端**已把它签发并留在内存**直到过期（闲置）。`forcePrompt` 只约束本页 UX，服务端 `/api/config/download` 仍接受有效令牌 —— 与「同时接受两种口径」的文档口径自洽。若要更严，可在过滤器对 download 端点跳过签发，**本版未做** |
| **HTTP 明文下的既有风险** | 接受（既有） | 管理口令的 `X-Admin-Hash` nonce **60 秒有效、一次性** —— nonce 只能防「同一次动作重复提交」，**不能防「抓到包后在 60 秒窗口内立刻重放」**（MITM）。**本版未改**，现场应限制管理端口的网络可达范围 |

### 现场验收清单（本版**未做**真实环境 E2E，必须现场确认）

1. **默认 5 分钟**：点一次「保存全部」（弹 1 次密码）→ 5 分钟内再点别的破坏性动作 → **不再弹密码**、动作成功；
2. **刷新失效**：刷新页面 → 下次破坏性动作**重新弹密码**；
3. **过期回落**：把 `AuthMemoryMinutes` 改成 `1`，1 分钟后再操作 → **重新弹密码**；令牌过期后动作**不失败**（应回落 nonce/hash）；
4. **改密码失效**：改后台 `WebAdmin:AdminPassword` → 旧令牌**立即不可用**（下次动作重新弹密码，且旧令牌被服务端拒绝 401）；
5. **重启失效**：重启服务 → 旧令牌不可用；
6. **`= 0` 关闭**：设 `WebAdmin:AuthMemoryMinutes=0`（热生效）→ 每个动作**都弹密码**；并注意**已签发的旧令牌最长还能用到 ≤5 分钟**；
7. **下载原文**：点「下载配置原文」→ **必定弹密码**（哪怕 5 分钟内刚验过其它动作）；
8. **HTTPS**：在 HTTPS 反代后重复第 1 条，并确认抓包**看不到明文令牌**；
9. **队列动作用**：重排队 / 删除在令牌有效期内**不再弹密码**且动作生效；
10. **未配置管理密码**：把 `AdminPassword` 置空 → 破坏性动作**返回 403**，页面提示「未配置管理密码」。

### 升级步骤建议

1. 备份现场 `appsettings.json`；
2. 停服务 → 覆盖程序文件 → **保留现场 `appsettings.json`**（安装包内的配置是模板，不要覆盖现场配置）；
3. 启动后查看日志，确认无 `Worker error`；
4. **按现场口径决定是否显式写 `WebAdmin:AuthMemoryMinutes`**（不写 = 默认 5 分钟记忆；想保持旧行为 = 写 `0`）；
5. 先跑一遍上面的现场验收清单第 1~7 条，再投产。

---

## 附一：产物校验（本机实测）

| 产物 | 大小（字节） | md5 | sha256 |
|---|---|---|---|
| `release/UniFlow-1.0.7.2-linux-x64.tar.gz` | 46,340,872 | `d26aae73918975cd8a0a888e90665cf3` | `1b6077bb17a4235546f0876bcc173db6f22bcc801dccb8884c37f485ad05587c` |
| `release/UniFlow-1.0.7.2-win-x64.zip` | 47,169,040 | `67873870e24eb973c8a60c1b175529e0` | `885fbbb58c1e18c27368d925fcdaaddd7690244389b6741374834e8990a265de` |

包内自检（全部通过）：

| 检查项 | 结果 |
|---|---|
| Linux 包 `.md` 清单 | `README.md` / `CONFIG-REFERENCE.md` / `INSTALL.md` / `使用说明书.md` → **4 份** ✔ |
| Windows 包 `.md` 清单 | 同上 → **4 份** ✔ |
| `cmp` INSTALL.md（两平台） | `INSTALL_OK` ✔ |
| `cmp` CONFIG-REFERENCE.md（两平台） | `CONFREF_OK` ✔ |
| `cmp` 使用说明书.md（两平台） | `MANUAL_OK` ✔ |
| 使用说明书 == 源文件 `docs/UniFlow-使用说明书.md` | `MANUAL_SRC_OK` ✔ |
| Linux 包内 `UniFlow` / `appsettings.json` / `wwwroot/`（10 项）/ `使用说明书.md` | 全部存在 ✔ |
| Windows 包内 `UniFlow.exe` / `appsettings.json` / `wwwroot/`（10 项）/ `使用说明书.md` | 全部存在 ✔ |
| 旧包未被改动（1.0.6.5 ~ 1.0.7.1 共 10 个，md5 前后一致） | `OLD_PACKAGES_UNCHANGED` ✔ |
| 包内说明书 §2.3 解压示例包名 | `UniFlow-1.0.7.2-linux-x64.tar.gz`（与本版包名一致）✔ |
| 包内说明书 md5 == 源文件 md5 == 两平台互相一致 | `5fbb7e324d85a2fa5bb449cb342721d4`（三份同值）✔ |

## 附二：改动文件索引（发布相关）

| 文件 | 位置 | 改动 |
|---|---|---|
| `code/src/UniFlow/UniFlow.csproj` | `:5-7` | `Version` / `AssemblyVersion` / `FileVersion` → `1.0.7.2` |
| `deploy/build.sh` | `:7` | `VERSION` → `1.0.7.2` |
| `deploy/package.sh` | `:6` | 用法注释示例 → `1.0.7.2` |
| `deploy/INSTALL.md` | `:1`、`:9`、`:22`、`:48` | 标题 + 两个平台的包名 + Linux 解压示例 → `1.0.7.2`（第六节「版本记录」历史叙述**保留**） |
| `docs/Release-Process.md` | `:39`、`:69`、`:88` | 可复制即用的示例版本号 → `1.0.7.2`（历史叙述未改） |
| `docs/CHANGELOG-1.0.7.2.md` | 本文件 | 本版改动记录 |
| `docs/UniFlow-使用说明书.md` | `:759`（§10.4） | 版本摘要**加一行** `1.0.7.2 | 管理验证 5 分钟记忆（服务端短令牌）` |
| `docs/UniFlow-使用说明书.md` | `:334-336`（§4.1） | 修正 3 条**与新默认行为矛盾**的旧表述（原文「每次弹一次管理密码」「没有会话 token」「每个动作各弹一次」→ 改为与 5 分钟记忆一致的口径） |
| `docs/UniFlow-使用说明书.md` | `:114`（§2.3） | Linux 解压示例包名 `UniFlow-1.0.7.1-linux-x64.tar.gz` → `UniFlow-1.0.7.2-linux-x64.tar.gz`（避免用户照着解压错包；**已随本版重新打包**） |
| `code/src/UniFlow/WebAdmin/Services/AdminAuthService.cs` | 工作区已有（本版未改） | 令牌签发 / 校验 / 指纹 / 清理 |
| `code/src/UniFlow/WebAdmin/Services/AdminAuthFilter.cs` | 工作区已有（本版未改） | 令牌优先放行 + 响应头下发 |
| `code/src/UniFlow/WebAdmin/Services/ConfigService.cs` | 工作区已有（本版未改） | `ReadAuthMemoryMinutes()` |
| `code/src/UniFlow/Common/Models/ConfigShared.cs` | 工作区已有（本版未改） | `AuthMemoryMinutes`（Hot） |
| `code/src/UniFlow/appsettings.json` | 工作区已有（本版未改） | `"AuthMemoryMinutes": 5` |
| `code/src/UniFlow/WebAdmin/Services/DefaultConfig.cs` | 工作区已有（本版未改） | `AuthMemoryMinutes: 5` |
| `code/src/UniFlow/wwwroot/app.js` | 工作区已有（本版未改） | 令牌内存态 / 免弹密码 / 401 回落 / 下载强制重输 |
| `code/src/UniFlow/wwwroot/index.html` | 工作区已有（本版未改） | API 说明页补充令牌口径 |
| `code/tests/UniFlow.Tests/WebAdmin/Services/AdminAuthServiceTests.cs` | 工作区已有（本版未改） | 令牌生命周期单测 |
| `code/tests/UniFlow.Tests/WebAdmin/Api/AdminAuthApiTests.cs` | 工作区已有（本版未改） | 令牌 API 级测试 |
| `docs/CONFIG-REFERENCE.md` / `docs/Configuration-Manual.md` | 工作区已有（本版未改） | 配置字段与「管理验证记忆」小节 |
| `docs_20261103/REVIEW_20261110_admin-auth-memory_L3.md` | — | L3 独立复核报告（PASS；**未改**） |

> 本版**未改动**任何业务判定逻辑（保存 / 重启 / 升级 / 丢弃队列 / 按架排序等）、**未改动** `archives/**`、旧包
> `release/UniFlow-1.0.6.*` / `1.0.7.0-*` / `1.0.7.1-*`、`docs/Maintenance-Order-*`、`docs/Fix-Report-*`、
> `docs/dispose-state-machine.*`；**未 commit / 未 tag / 未 push**。
