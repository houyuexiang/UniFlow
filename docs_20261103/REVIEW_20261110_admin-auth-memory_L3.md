# L3 独立复核报告：管理验证 5 分钟记忆（v1.0.7.1 工作区未提交改动）

- 复核人：reviewer（L3 critic，独立会话）
- 日期：2026-11-10
- 被测物：仓库 `/root/opencode_project/UniFlow`，HEAD `3240cf1` + 工作区 14 个未提交文件（管理验证记忆功能）
- 结论：**PASS / 可合并**（无 P0/P1；P2×1、P3×3，均不阻塞）

---

## 1) 结论表

| # | 被复核断言 | 我的命令 | 原始输出片段 | 执行位置 |
|---|---|---|---|---|
| 1 | 构建 0 error | `dotnet build code/src/UniFlow/UniFlow.csproj -c Release` | `4 Warning(s) 0 Error(s)`（NU1902/NU1903 为既有包漏洞告警，与本改动无关） | 本机 |
| 2 | 无 MySQL 519+4skip | `dotnet test code/tests/UniFlow.Tests/UniFlow.Tests.csproj -c Release` | `Passed! - Failed: 0, Passed: 519, Skipped: 4, Total: 523` | 本机 |
| 3 | AdminAuth 子集 73/73 | `dotnet test --filter "FullyQualifiedName~AdminAuth"`（/tmp 副本） | `Passed! - Failed: 0, Passed: 73, Skipped: 0, Total: 73` | /tmp/opencode/review_neg |
| 4 | `node --check app.js` | `node --check code/src/UniFlow/wwwroot/app.js` | `node check OK` | 本机 |
| 5 | Playwright 58/58（含 UI-10 ×2） | `UNIFLOW_APP_DIR=/tmp/uniflow-e2e/app-review npx playwright test`（冻结副本=当前工作区 publish） | `58 passed (43.4s)`，含 `UI-10 … 第二次保存不再弹` ✓、`UI-10 … 不落 localStorage/sessionStorage` ✓ | /tmp/uniflow-e2e/pw-review |
| 6 | 服务端端到端：nonce/hash→签发→仅 token 复用 | python urllib 对冻结副本实测 | `status 200 … token:1e01a7c4… expiresIn:300` → 仅带 `X-Admin-Token` 第二次 batch `second status 200` | 本机→127.0.0.1:5199（app-review） |
| 7 | 浏览器端到端（真实 Chromium fetch/XHR） | Playwright 探针 + 原始 TCP 代理抓字节 | 原始响应含 `X-Admin-Token: beaeb93d…` + `Expires-In: 300`；fetch `hdrs['x-admin-token']='present'`、XHR `token:09863e7c… expiresIn:300` | /tmp/uniflow-e2e/pw-review |
| 8 | 仓库零写 | `git status --porcelain`（开始时/结束时各一次） | 均为同样 14 个 `M` 文件，无新增、无未跟踪文件、未 commit | 本机 |

注：第 5–7 项过程中我曾误用默认目录 `/tmp/uniflow-e2e/app`（11-07 的 v1.0.6.0 旧发布产物，`grep -c X-Admin-Token app.js = 0`），导致 UI-10/UI-4 误红；改指 `app-review` 后 58/58 绿。**3 个误红全部归因于我方环境错误，非产品缺陷**（详见 §4）。

## 2) 负向证据（/tmp 副本变异，仓库零写）

| 变异 | 手段 | 失败输出 |
|---|---|---|
| ① 去掉过滤器令牌校验分支（AdminAuthFilter ①段） | /tmp 副本删除 `TryValidateToken` 分支 | `Failed! Failed: 2` — `AdminToken_Only_Request_Succeeds_Without_NonceHash`、`AdminToken_Only_Request_Works_For_Destructive_Endpoints` 均 FAIL |
| ② 去掉密码指纹比较（TryValidateToken） | /tmp 副本删除指纹 if 块 | `Failed! Failed: 2` — `AdminToken_Invalidated_After_AdminPasswordChanged`、`TryValidateToken_AfterAdminPasswordChanged_Fails_AndTokenRemoved` FAIL |
| ③ 去掉 `memoryMinutes<=0` 分支 | /tmp 副本删除该 return | `Failed! Failed: 3` — `TryValidateAndIssueToken_MemoryZero_NoToken`、`TryValidateAndIssueToken_Clamps_Minutes_To_0_60`、API `AuthMemoryMinutes_Zero_NoToken_Header_And_TokenOnly401` FAIL |
| 恢复 | 从仓库原文件 cp 回副本 | 73/73 重新全绿 |

三个闸门都能"红"，且红的正是对应用例 → 闸门有效。

## 3) 逐项复核结果

1. **重跑** — PASS：见结论表 1–5。总用例数 523 与自报一致；有 MySQL 的 4 个用例本机无法跑（无 MySQL），但总数 523=519+4 与"有 MySQL 523"口径吻合。
2. **令牌生命周期** — PASS：
   - `TryValidateAndIssueToken` 先走一次性 nonce/hash（AdminAuthService.cs:119-121，`TryValidate` 原子 `TryRemove`，成败都消费）；
   - `memoryMinutes<=0` → `(true,null,0)` 不发令牌（:129-131，测试+API 测试双向覆盖）；
   - TTL 固定、不滑动：签发时定 `ExpiresAt`，验证只读不改（:186-189）；
   - 过期/未知/空 token → false（:163-190，测试 `TryValidateToken_With_Empty_Or_Unknown_Token_Fails`）；
   - 指纹不匹配 → 移除+false（:180-185，测试覆盖"原密码也救不活"）；
   - 可重复使用、nonce 仍一次性（测试 `TryValidateToken_Is_Reusable_But_Nonce_Stays_OneShot`）；
   - 上限/清理：`ConcurrentDictionary`+`TryRemove`，4096 上限+惰性清理（测试 `TokenStore_Is_Bounded` 5000 次压入 ≤4096）。
3. **过滤器优先级** — PASS（AdminAuthFilter.cs:37-72）：403 在 token 判定之前（:37-40）；有效 token 直接放行（:48-49）；无效 token+有效 nonce/hash 继续走 nonce 流（:53-59，注释口径与实现一致）；两者皆无 → 401（:53-56）；响应头在 `next` 之前写（:65-70），业务体不受影响（API 测试 /config/download 200 且带 body）。
4. **前端** — PASS：令牌仅模块级 `let _adminToken`（app.js:34-35），无 localStorage/sessionStorage 写入（UI-10 红闸门用例验证）；`adminAuth` 有令牌不弹（:126-129）；401 清令牌回落 nonce/hash（:175-180）；网络错误直接 return 不清令牌（:172-173）；upload+apply 复用（uploadUpdateWithProgress 取响应头 :1560-1563，成功后 `rememberAdminToken` :1624，apply 走 authFetch）；下载原文 `forcePrompt:true`（:1410）且全程不采纳下载响应令牌（:1412-1447 无 rememberAdminToken 调用）；刷新页面即失（无持久化）；时钟偏差双向自愈（本地提前过期→重新弹；本地滞后→服务端 401→清令牌重弹）。
5. **安全边界** — PASS：服务端只存 SHA-256 指纹（`PasswordFingerprint`，测试断言 =SHA-256(密码+空串)）；bearer/重放窗口/HTTP 明文风险在 CONFIG-REFERENCE.md「管理验证记忆」小节 + Configuration-Manual.md 均写明（"重放窗口更长…建议上 HTTPS"）；`AuthMemoryMinutes=0` 后旧令牌在自身有效期内仍可用**已写进文档**（"已签发的旧令牌最长在其自身有效期内（≤5 分钟）仍可用"）；改密码/重启/刷新失效有测试；grep 全部 C# 日志调用，无 token/nonce/hash 入日志。
6. **配置/UI 一致** — PASS：`AuthMemoryMinutes` 默认 5、clamp 0~60、缺失/非法→5（ConfigService.cs:368-399，字符串/数字双容错）；`[Restart(Hot)]`（ConfigShared.cs:221）；appsettings.json / DefaultConfig.cs / FORM_FIELDS / EXAMPLES 四处均为 5；`0` 时表单（min:0）与后端行为一致（API 测试）。
7. **回归** — PASS：dotnet 519+4skip 全绿；Playwright 58/58（UI-4 快速填窗、鉴权、批量保存、下载、XSS M-07 等全部通过）；队列动作用例通过。
8. **负向 3 条** — PASS：见 §2。

## 4) 偏差/新问题清单

- **P2（登记，不阻塞）**：`authFetch` 401 重输时，若用户输入空串且 `pwHolder.pw` 原本存在，则沿用旧（可能就是错的）密码重试（app.js:181-183 `pwHolder.pw = again === '' ? pwHolder.pw : again`）。反例：密码输错→401→重输时按空串确认→用同一个错密码再打一次必然 401，白耗 1 次尝试并再弹一次。修法：空输入时视为取消或强制重弹。不修的后果：仅多一轮无意义弹窗，行为有界（≤3 次），无安全影响。
- **P3**：令牌上限挤旧用 `OrderBy(ExpiresAt)` 快照，满载时每次签发 O(n log n)（n≤4096，实际频率极低）；并发挤旧时可能多挤 1 条，安全无虞。可用 `ToggleAll`/最小堆优化，非必需。
- **P3**：`expiresIn` 用 `Math.Ceiling`，浏览器按 `Date.now()+exp` 本地判断，可能比服务端毫秒级提前过期 → 一次额外 401 后自愈。可忽略。
- **P3（口径观察，已部分写文档）**：下载原文时服务端仍会签发令牌（客户端刻意不采纳），该令牌将在服务端内存中闲置至过期；前端 `forcePrompt` 只约束本页 UX，服务端 `/api/config/download` 仍接受有效 token——文档已把下载列入"同时接受两种口径"端点清单，口径自洽；若要更严可在 filter 对 download 端点跳过签发。
- **复核过程环境事故（非产品缺陷，登记备查）**：`/tmp/uniflow-e2e/app` 为 11-07 的 v1.0.6.0 旧发布产物且是 Playwright 默认目录；未显式指定 `UNIFLOW_APP_DIR` 时会测到旧代码（UI-10/UI-4 误红）。建议：主会话归档时把该目录改名或写 README 强提醒。

## 5) 放行建议

**放行**。核心闸门（token-only 放行、指纹失效、0=关闭、nonce 一次性、上限、不落盘）均有可红测试且经负向变异验证；浏览器与服务端端到端均实测通过；文档口径完整（含 0 值旧令牌、重放窗口、HTTPS 建议）。

## 6) 未验证项

| 项 | 原因 | 谁能补 |
|---|---|---|
| 有 MySQL 的 4 个用例（523 全绿） | 本机无 MySQL | 主会话（其环境有 MySQL，自报 523/523） |
| upload→apply→进程重启全链路 Playwright | README 已知限制（进程重启无法在共享环境安全执行）；upload+apply 的令牌复用已由代码审读+authFetch 单测口径覆盖 | 有独立测试机时可补 |
| `MaxTokens` 满载并发挤旧的压测 | 现有测试为单线程有界性验证；并发安全由 ConcurrentDictionary+TryRemove 保证（代码审读） | 如需可加多线程压测用例 |
