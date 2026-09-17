# UniFlow ↔ FlexLab(Dream) 报文接口文档（APIO 协议）

> **来源**：Inpeco FlexLab 软件架构手册 `DCAU-H00.816.12.01.02`（FL-SA.62，790 页）。
> 主要章节：**第 9 章「Messages from Host LIS or GUI to Dream」**（GUI/LIS→Dream，及 Dream 发往自身），以及 **第 8 章**（Dream→GUI/LIS 的通知类消息）。
> 说明：本文件由手册 `pdftotext -layout` 文本（`/tmp/dcau.txt`）核对整理而成，所有行号指该文本文件行号，便于回查原文。字段名、取值保留英文原样。
> 角色说明：**Dream** 指 FlexLab 后台自动化引擎（DREAM Software）；**GUI** 指操作界面；**Host LIS** 指宿主 LIS / 中间件。**UniFlow** 在本协议中扮演 Host LIS（DMS）角色，通过 TCP/IP 向 Dream 发送报文。
> 关联文档：`DATABASE-STRUCTURE.md`（手册 3.3–3.5：t_sample / tube_log / aliquot_request / automation_status / errorhandling 表结构、DMS 数据库直连模式、Counter 文件）。

---

## 1. 发送方 / 接收方矩阵

依据手册第 9 章开头（`/tmp/dcau.txt` 26632–26639 行）与第 8 章开头（22196–22200 行）：

- 「All messages can be sent by GUI, **except the CLOCK message that can be sent by Dream only**」（26634）
- 「The **COMMENT, ORDER and ERROR** messages can be sent by Host LIS」（26635）
- 「The **COMMENT, ORDER, ERROR, CLOCK and METHOD-CHANGED** messages can be sent by Dream」(26636，Dream 发往自身)
- 「Before being sent to Host LIS, **COMMENT and ORDER** messages are translated into ASTM format」(26637)
- 「All messages can be sent to GUI, **except INIT and SHUTDOWN, which can be sent to Host LIS only**」（22196）
- 「**COMMENT, ORDER, INIT and SHUTDOWN** messages can be sent to Host LIS」（22197）

| 报文 | 发送方 | 接收方 | 用途 |
|------|--------|--------|------|
| **ORDER** | GUI、Host LIS（LIS） | Dream | 样本测试请求 / 取消 / 追加（Add-on） |
| **COMMENT**（S002/S008/S010…、I001/I003…） | GUI、Host LIS（LIS） | Dream | 样本属性变更、状态/清单/面板等请求 |
| **ERROR** | GUI、Host LIS（LIS）、Dream | Dream | 错误通知 |
| **CLOCK** | **仅 Dream**（发往自身） | Dream | 触发 5 分钟 / 每日定时活动 |
| **METHOD-CHANGED** | Dream（发往自身记录） | Dream | 记录 Analyzer 方法变更 |
| **SCHEDULE-TESTS** | Dream（发往自身记录） | Dream | 记录调度到 Analyzer 的测试 |
| **BRIDGE** | Dream | 另一自动化系统（跨 OUM/VTX） | 跨自动化系统载物台/管信息交换 |
| **INIT** | Dream | Host LIS（LIS driver） | Dream 要求 Host LIS driver 初始化与 Host LIS 的通信（**GUI 不使用**） |
| **SHUTDOWN** | Dream | Host LIS（LIS driver） | Dream 要求 Host LIS driver 关闭通信（**GUI 不使用**） |
| **STATUS-REQUEST** | GUI | Dream | 请求 STATUS 通知 |
| **STATUS**（Notification） | Dream | GUI / DMS IUI | 自动化系统状态回复（STATUS-REQUEST 的应答） |

> 注：UniFlow 作为 Host LIS 主要使用 **ORDER**（含取消）、**COMMENT**、**STATUS-REQUEST**（探测连接）。Dream 无显式 ACK，见 §6 附录。

---

## 2. ORDER 报文（样本请求）

手册 9.2（`/tmp/dcau.txt` 26683–26759 行）。

**帧格式**（26693–26697）：

```
ORDER <Sample ID>|<Patient-ID>|<Sample-Type>|<Sample-Priority>|<Sample-Dilution>|<Sample-Collection>|<Patient-Birth-Date>|<Patient-Gender>|<Patient-Race>|<Attending-Physician>|<Admission/Discharge-Date>|<Patient Location>|<Action-Code>|<Sample-Source>|<Ordering-Physician>|<Patient-Comment>|<Order-Comment>|<Test-Request-1>|…|<Test-Request-n>
```

字段以 `|`（ASTM 主分隔符）连接。**索引按 0 起算**，`Test-Request-i` 从索引 17 起依次排列。

### 2.1 字段表

| 索引 | 字段名 | 类型 | 说明 | 取值/示例（手册原文） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID | string | 样本 ID（26702） | — |
| 1 | Patient-ID | `PA PID^LAB PID^Last Name^First Name^Middle Initial` | 子字段以 `^` 分隔（26703） | — |
| 2 | Sample-Type | 枚举/字符串 | 样本类型（26704） | `S,C,P,U,W,A,O`（或任意字符串） |
| 3 | Sample-Priority | 枚举 | 样本优先级（26705） | `S`(STAT) / `A`(ASAP) / `R`(Routine) |
| 4 | Sample-Dilution | 小数 | 稀释倍数（26706） | decimal number |
| 5 | Sample-Collection | 时间戳 | 采集时间（26707） | `YYYYMMDDHHMMSS` |
| 6 | Patient-Birth-Date | 日期 | 出生日期（26708） | `YYYYMMDD` |
| 7 | Patient-Gender | 枚举 | 性别（26709） | `F`(Female) / `M`(Male) / `U`(Unknown) |
| 8 | Patient-Race | string | 种族（26710） | — |
| 9 | Attending-Physician | `Code^Last Name^First Name^Middle Initial^Suffix` | 主治医生，子字段以 `^` 分隔（26711） | — |
| 10 | Admission/Discharge-Date | `YYYYMMDD^YYYYMMDD` | 入院^出院，子字段以 `^` 分隔（26712） | — |
| 11 | Patient Location | `code^text` | 患者位置，子字段以 `^` 分隔（26713） | — |
| 12 | **Action-Code** | 枚举 | 操作码（26714） | `N`(New) / `A`(Add-on) / `C`(Cancel) |
| 13 | Sample-Source | `<Centrifugation Required>^<Lab Tube Type>^<Automated QC>` | 见 §2.3（26715–26722，Nov 2 起可用） | — |
| 14 | Ordering-Physician | `Code^Last Name^First Name^Middle Initial^Suffix` | 开单医生，子字段以 `^` 分隔（26740） | — |
| 15 | Patient-Comment | — | 未使用（26741） | not used |
| 16 | Order-Comment | — | 未使用（26742） | not used |
| 17 | **Test-Request-1** | `tci^tni^tti` | **独立的顶层 `\|` 字段**（第 1 个测试）；`^` 仅用于字段内 `tci^tni^tti`，见 §2.2（26696–26697、26743） | — |
| 18… | Test-Request-2 … n | `tci^tni^tti` | **每个测试各占一个独立的顶层 `\|` 字段**，依次占索引 18、19…；最多 80 个测试（26696–26697、26750–26751） | — |

### 2.2 Test-Request-i 结构 `tci^tni^tti`（26743–26747）

子字段以 `^` 分隔：

| 子字段 | 含义 |
|--------|------|
| `tci` | ASTM Test Code（Host LIS 用）或 Test Code（GUI 用） |
| `tni` | ASTM Test Name（**被忽略 / ignored**） |
| `tti` | Instrument Target：指定 Instrument ID，表示在该指定仪器上运行该测试 |

**多测试字段规则（重要，26696–26697、26743，实测确认）**：

- 帧格式串写的是 `…|<Order-Comment>|<Test-Request-1>|…|<Test-Request-n>`（26696–26697）——Test-Request 依次从索引 17 开始。
- **多个测试名用 `^` 连接放进 Test-Request 字段**（实测确认，与 Dream 端行为一致）：`…|C|…|<tci1>^<tci2>^…^<tcin>`，即索引 17=`<tci1>^<tci2>^…`。`^` 同时承载字段内子分量（tci/tni/tti）与多测试的分隔作用，实际设备按 `^` 切分逐个识别。
- 用户实测报文佐证：`ORDER test3001|*****************^^^||R||20260915212516||*||^^|||C|U^^||||BNP`（单测试直接写测试码；多测试如 `BNP^RMSMP` 用 `^` 连接）。

- 若 ASTM Test Code 无法解析，则未知 ASTM Test Code 会加下划线前缀 `_` 存入样本记录并标记为 "Unknown Test"（26748–26749）。
- 每个样本在数据库中最多 80 个测试；超过仅保存前 80 个，超限时按 CANCELLED/SORTED/ALIQUEOTED/DELIVERED/SAMPLED → COMPLETE 的顺序删除已终态测试（26750–26759）。

### 2.3 Sample-Source（索引 13，26715–26722）

子字段以 `^` 分隔：`<Centrifugation Required>^<Lab Tube Type>^<Automated QC>`

| 子字段 | 取值 |
|--------|------|
| Centrifugation Required | `U`：LIS 要求离心（即使已装在离心输入模块/IOM Lane）；空=不要求。**前提**：LIS.ini 中 `LIS Allow Requesting Centrifugation` 需为 Enabled（26717–26720） |
| Lab Tube Type | Lab Tube Type Code（2 位数字）（26721） |
| Automated QC | `Q`：该管为自动 QC 管；空=否（26722） |

### 2.4 Action-Code 语义（新增/追加/取消）

手册未逐条给出 N/A 的完整字段差异，仅定义取值（26714）与功能需求（26687–26691）。结合功能需求条款：

| Action-Code | 含义 | 手册依据 |
|-------------|------|----------|
| `N` | New — 新建样本订单 | 26714；FXSMS-822（26685–26686，存为 "To Do"） |
| `A` | Add-on — 追加测试，样本再次处理 | 26714；FXSMS-828（26690–26691） |
| `C` | Cancel — 取消尚未处理的测试，标记 "Cancelled" | 26714；FXSMS-773（26687–26689） |

> **取消（C）帧格式**：手册**未单独给出 Cancel 专用帧**——取消复用同一 ORDER 帧，仅 Action-Code=C（26714）；FXSMS-773（26687–26689）仅要求 Dream "接收 Sample Cancel Orders 并将尚未处理的被取消测试标记为 Cancelled"。

> **LIS 渠道与 GUI 渠道一致性（核实，26635–26640、2949–2953）**：
> - ORDER 消息（含 Cancel）是 **Host LIS 与 GUI 共用同一条消息**（26635–26640：COMMENT/ORDER/ERROR 均可由 Host LIS 发，All messages 可由 GUI 发），两渠道入站格式一致。
> - Tube Log 事件表（2949–2953）证实：`CANCEL (Sample Request with Action Code C sent by Host LIS or GUI)`——Cancel 即 ORDER 帧 + Action-Code=C，来源渠道不改变报文。
> - 唯一渠道差异在**出站方向**（26637–26638）：Dream 发回 Host LIS 时 COMMENT/ORDER 转 ASTM 格式；GUI 渠道不走 ASTM。
> - **注意**：2055 是 Dream（FlexLab Server）监听端口（5811，`Server Computer IP:Port` 默认 `127.1.1.0:2055`），GUI 与 Host LIS 均连此端口，身份不同、报文格式一致。

> **Action-Code 分层差异（核实）**：
> - **协议层**（第 9 章 ORDER 定义，26714）：Action-Code = `N (New), A (Add-on), C (Cancel)`——入站消息只有 N/A/C。
> - **数据库层**（Sample screen 字段清单，2803）：Action-Code = `N, A, C, K (Complete), B (Add-on which must not cause a rerun)`——含 K/B，为 t_sample.act_code 列的存储枚举。
> - 两处差异是**协议层 vs 数据库层**的不同抽象层定义，**不是** LIS/GUI 渠道差异。

> **Complete（K）消息的发送路径（核实，26779–26800）**：
> - **推荐/手册明确**：`COMMENT S002^<Sample-ID>\COMPLETE\<Instrument-Type>\<Test1;…;Testn>^S`（26779，注释明确 Host LIS or GUI 均可发）——不带参数=全部待处理测试完成；带 Instrument-Type=该类型测试完成；带 Test 列表=指定测试完成。
> - **不推荐**：ORDER 帧 Action-Code=K——数据库字段枚举（2803）有 K，但协议层 ORDER 定义（26714）未列，手册未确认 ORDER 帧带 K 的合法性。
> - Tube Log 事件表（2952）：`COMPLETE (Sample Complete Notification S002 sent by Host LIS or GUI)`——Complete 即 S002 消息，非 ORDER。
> - 若 UniFlow 需发 Complete，应使用 `COMMENT S002^{sid}\COMPLETE\...^S`，而非 ORDER K。

> **待核实**：手册未说明 Cancel 时 `Test-Request` 是否必须省略、或需列出被取消的具体测试名（省略 = 是否取消该样本全部测试？）。**若需列出具体测试，则每个被取消测试各占一个独立的顶层 `\|` 字段（索引 17、18…，见 §2.2 多测试规则），而非 `^` 拼进单字段**。UniFlow 现有两处取消实现见 §6.1。

### 2.5 非法字符约束（26699–26701）

- 任何字段均**不得**包含非法字符 `|`、`\`、`^`、`&`、`~`。
- `;` 和 空格(blank) 仅在 **使用 ASTM Code 作为 Test Code** 时被视为非法。

> ⚠️ **手册内部不一致（待核实）**：上文 26639 / 26700 称字段不得含 `^`，但 Patient-ID、Attending-Physician、Sample-Source 等字段定义（26703、26711、26715、26713、26712）本身用 `^` 分隔子字段。可理解为「`^` 仅用于字段内子分量，不作为顶层字段分隔符」，但手册表述存在冲突，需以实际固件行为为准。

---

## 3. COMMENT 报文（S002 系列等）

COMMENT 为通用前缀，后接消息 ID（Sxxx=样本、Ixxx=仪器/配置），子字段通常以 `\`（子分隔符）分隔（个别消息如 S018 用 `^` 分隔），结尾带类型标志（`^S` 样本、`^I` 仪器）。

### 3.1 COMMENT S002 — 样本属性变更（26761–26874）

**变体格式**：

```
COMMENT S002^<Sample-ID>\<Attribute>^S
COMMENT S002^<Sample-ID>\COMPLETE\<Instrument-Type>\<Test1;…;Testn>^S
COMMENT S002^<Sample-ID>\DELIVER\<IOM-Instance>\\\<Automation-ID>^S
```

由 Host LIS 或 GUI 发送，用于改变所选样本管的处理（26781）。

**COMMENT 通用帧结构**：`COMMENT <ID>^<F0>\…\<Fn>^<TypeFlag>`。消息 ID（`S002`）后紧跟 `^`，其后为以 `\` 分隔的字段序列，末尾 `^<TypeFlag>` 为类型标志（`S`=样本 / `I`=仪器）。索引 **0 起算**，按 `\` 字段顺序排列；`Type Flag` 为结尾标志，不参与索引。

**3.1.1 基础 / COMPLETE 变体字段表**（基础 26778；COMPLETE 26779）

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例 |
|-----|--------|------|------|-----------|
| 0 | Sample-ID（样本ID） | string | 目标样本管 ID（26798） | `S00123` |
| 1 | Attribute（处理动作） | 枚举 | 变更样本管处理；**仅 COMPLETE 时索引 2、3 存在**，其余取值（ERROR/KEEP/TRASH/RESET-TIME/S010）省略索引 2、3（26799–26855） | `COMPLETE`/`ERROR`/`KEEP`/`TRASH`/`RESET-TIME`/`S010` |
| 2 | Instrument-Type（仪器类型） | 枚举/字符串 | **仅 COMPLETE**；指定后仅完成该仪器类型的未完成测试；空=不限；Hotel 3 起，非 COMPLETE 时被忽略（26800–26809、26856–26858） | 仪器类型码 |
| 3 | Test1;…;Testn（测试列表） | string；`;` 分隔 | **仅 COMPLETE**；指定后仅完成这些测试；Hotel 3 起，非 COMPLETE 时被忽略（26800–26809、26856–26858） | `CBC;DIF` |
| — | Type Flag（尾标志） | 字面量 | 样本标志 | `S` |

> COMPLETE 变体中索引 2、3 **均可选**：两者皆空 → 完成全部未完成测试；仅指定 Instrument-Type → 仅完成该仪器类型；仅指定 Test 列表 → 仅完成指定测试（26800–26809）。

**3.1.2 DELIVER 变体字段表**（26780）

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例 |
|-----|--------|------|------|-----------|
| 0 | Sample-ID（样本ID） | string | 目标样本管 ID | `S00123` |
| 1 | Attribute（处理动作） | 枚举 | 固定 `DELIVER`（26810–26811） | `DELIVER` |
| 2 | IOM-Instance（IOM实例） | number（1–9） | 投递目标 IOM 实例；缺省=首个配置投递架的 IOM；Papa 1 起；R18.1 起可指定经 VTM 连接的另一 FlexLab 上的 IOM；最大值 9（26859–26864） | `1` |
| 3 | Automation-ID（自动化系统ID） | number（0–8） | 投递目标自动化系统；1–8=指定系统，0/空=当前系统；R19.1 起 / VTX 为 R24（26865–26874） | `0` |
| — | Type Flag（尾标志） | 字面量 | 样本标志 | `S` |

> ⚠️ **手册未明示**：DELIVER 格式串中 `\<IOM-Instance>` 与 `\<Automation-ID>` 之间写作 `\\`（双反斜杠，26780），其确切语义手册未说明（推测 IOM-Instance 为空时保留空槽位）。

**`<Attribute>` 取值**（26799–26855）：

| Attribute | 含义 |
|-----------|------|
| `COMPLETE` | 将尚未完成的测试标记为完成并按此处理该管。无 `<Instrument-Type>` 且无 `<Test1;…;Testn>` → 所有未完成测试均完成；指定 `<Instrument-Type>` → 仅该仪器类型的未完成测试完成；仅指定 `<Test1;…;Testn>` → 仅指定测试完成（26800–26809） |
| `DELIVER` | 送至 User Deliver Sorted Output Rack（若配置），否则 PO Rack（#SC001）（26810–26811） |
| `ERROR` | 送至 Automatic Deliver Sorted Output Rack（若配置），否则 PO Rack（#SC00C）（26812–26813） |
| `KEEP` | 在 Storage Module 保留至 disposal 超时，之后送至 Long Term Storage Sorted Output Rack（#SC00D）（26814–26816） |
| `TRASH` | 请求丢弃/取回：在 Storage 中立即丢弃；在 IOM 中取回至 track；其它模块/track 上则忽略（Hotel 3 起可用，India 1 起用于 Dispose 按钮）（26817–26823） |
| `RESET-TIME` | 重置管进入模块的计时为当前时间（PNP Analyzer 自 Papa 2；Centrifuge 与 Track OUM/VTX 自 R19.1）。用于 SC075/SC0A3/SC0A1 超期错误恢复（26824–26833） |
| `S010` | 给管标记 SC010 样本错误（Cap Type/存在性与测试订单不符）并送至 Sorted Output Rack / PO Rack（R20.1 起，仅由主 FlexLab 经 LIS3 通道发往次级 FlexLab）（26834–26855） |

**可选参数**（26856–26874）：
- `<Instrument-Type>`、`<Test1;…;Testn>`：仅对 `COMPLETE` 有效，其它 Attribute 时忽略（Hotel 3 起）。
- `<IOM-Instance>`：仅 `DELIVER` 可用（Papa 1 起），值 ≤ 9；R18.1 起可指定经 VTM 连接的其它 FlexLab 上的 IOM 实例。
- `<Automation-ID>`：仅 `DELIVER` 可用（R19.1 起 / VTX 为 R24）；1–8 表示指定自动化系统的 IOM 实例；0/空表示当前自动化系统。

### 3.2 其它常用 COMMENT 请求

| 报文 | 格式 | 用途 | 手册行号 |
|------|------|------|----------|
| S008 | `COMMENT S008^<Sample-ID>\<Duplicate-Tube-Number>^S` | 请求样本状态通知（S009）。`<Duplicate#>` 1–15，缺省=原管(0) | 26876–26907 |
| S010 | `COMMENT S010^<Sample-ID>\<New-Priority>^S` | 升级优先级；`<New-Priority>`=`A`(ASAP)/`S`(STAT)（R20 起） | 26909–26920 |
| S011 | `COMMENT S011^<Sample-ID>\<Parameter>^S` | 手动移出；`<Parameter>`=`DELETE` 时删除样本记录 | 26922–26927 |
| S012 | `COMMENT S012^<Sample-ID>^S` | 清除样本错误 | 26929–26932 |
| S016 | `COMMENT S016^<Sample-ID>\<Sample-Volume>^S` | 设置血清体积（μl）；空=重置（Hotel 3 起） | 27915–27940 |
| S018 | `COMMENT S018^<Primary-Sample-ID>^<Action-Code>^<Aliquot-SID-1>^<Label-Type-1>^<Microliters-1>^<Test-1>^<Dept-1>\...\<Aliquot-SID-n>^<Label-Type-n>^<Microliters-n>^<Test-n>^<Dept-n>^S` | DMS 分装请求（Mike 1 起）；Action-Code=`N`(新建)/`A`(追加分装管)/`C`(取消该主管全部请求，**C 时后续参数必须省略**)；扩展标签模板时每个分装管另加 DC/Worklist/Test-Name/Priority/Temp/SpecRecd/SpecNbr/AliqInd/Rack/SpecCond/PSite 字段（28075–28082） | 28047–28173 |
| I001 | `COMMENT I001^<Node-ID>\<Test-Code>\<Command>^I` | 启用/禁用测试或 Analyzer；Command=ENABLE/DISABLE | 26934–26941 |
| I003 | `COMMENT I003^<Node-ID>^I` | 请求测试试剂清单（I004）；Node-ID=`*` 表示全部 | 26943–26964 |
| I005 | `COMMENT I005^<Node-ID>^I` | 请求测试面板（I006） | 26966–26971 |
| I009 | `COMMENT I009^<Node-ID>^I` | 请求测试映射（I010） | 26973–26979 |
| I011 | `COMMENT I011^^I` | 请求测试信息（I012） | 26981–26986 |
| I013 | `COMMENT I013^^I` | 请求分选测试（I014） | 26988–26993 |
| I015 | `COMMENT I015^^I` | 请求分装测试（I016） | 26995–27017 |
| I026 | `COMMENT I026^<Comment-Fields>^I` | 变更 Analyzer Test Codes 表 | 27992–28008 |
| I027 | `COMMENT I027^^I` | 请求 Analyzer Test Codes（I028） | 28010–28015 |

---

## 4. 状态 / 时钟 / 调度 / 桥接 / 其它报文

### 4.1 STATUS-REQUEST（GUI→Dream，9.26，27203–27207）

```
STATUS-REQUEST <IOM-Instance>
```

GUI 请求 Dream 发送 STATUS 通知（27204–27207）。报文名与参数以**空格**分隔。

**字段位置表**（索引 0 起）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例 |
|-----|--------|------|------|-----------|
| 0 | IOM-Instance（IOM实例） | number | 用于在 STATUS 通知中获取该 IOM 的 lane 配置（27206–27207） | `1` |

- **UniFlow 实际使用**：`STATUS-REQUEST 1`（探测连接，见 §6.2）。

### 4.2 STATUS（Dream→GUI / DMS IUI，8.25，23490–23568）

STATUS-REQUEST 的应答。每个 Node 一组字段，多个 Node 之间以 `\` 连接（`\...\<Node-IDn>…`），**单个 Node 内字段以 `^` 分隔**：

```
STATUS <Node-ID1>^<Node-Type1>^<Node-Instance1>^<Node-Mode1>^<Error-Code1>^<Error-Type1>^<Run-Status1>^<Routine-Sample-Load-Status1>^<STAT-Sample-Load-Status1>^<Empty-Carrier-Load-Status1>^<Consumable-Count1>^<Consumable-Status1>^<Number-of-Carriers-in-Main-Queue1>^<Number-of-Carriers-in-Secondary-Queue1>^<Number-of-Carriers-inside-Module1>\...\<Node-IDn>^…
```

**单 Node 字段（本手册版本共 15 个，索引 0 起）**——单 Node 内字段以 `^` 分隔（格式串 23504）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Node-ID（节点ID） | string | Node ID（23535） | `1` |
| 1 | Node-Type（节点类型） | string | Node 类型（23536） | `IOM` |
| 2 | Node-Instance（节点实例号） | number | 该类型 Node 的实例号（23537） | `0` |
| 3 | Node-Mode（节点模式） | 枚举 | 见 §4.2.1（23538–23539） | `ON` |
| 4 | Error-Code（错误码） | string | 无错误为 `0000` 或空；按 Gate/Node/LongRunning 错误严重度择一（23540–23541） | `SC001` |
| 5 | Error-Type（错误类型） | 枚举 | 见 §4.2.2（23542） | `G` |
| 6 | Run-Status（运行状态） | number | 见 §4.2.3（23543–23545） | `1` |
| 7 | Routine-Sample-Load-Status（常规样本装载状态） | number | Routine 样本可用槽位数；或 TRU Node（Node 0）的 Overdue Carrier Queue（23546–23547） | `5` |
| 8 | STAT-Sample-Load-Status（STAT样本装载状态） | number | STAT 样本可用槽位数（23548） | `0` |
| 9 | Empty-Carrier-Load-Status（空载物台装载状态） | number | 空载物台可用槽位；Liaison/Jo Plus 为 Sampling Queue 载物台数（23549–23550） | `3` |
| 10 | Consumable-Count（消耗品计数） | number/enum | 见 §4.2.4，按 Node 类型不同（23551–23554） | `12` |
| 11 | Consumable-Status（消耗品状态） | string/enum | 见 §4.2.5，按 Node 类型不同（23555–23561） | `G` |
| 12 | Number-of-Carriers-in-Main-Queue（主track队列载物台数） | number | 主 track 队列（前节 divert gate 至本节点 divert gate）载物台总数；**R19 起新增**（23532、23562–23564） | `2` |
| 13 | Number-of-Carriers-in-Secondary-Queue（次级lane载物台数） | number | 本节点次级 lane 载物台总数；**R19 起新增**（23532、23565–23566） | `1` |
| 14 | Number-of-Carriers-inside-Module（模块内载物台数） | number | 仅 WBB 模块；模块内部载物台总数；**R19 起新增**（23532、23567–23568） | `0` |

**4.2.1 Node-Mode 取值**（23538–23539）

| 值 | 含义 |
|----|------|
| `ON` | On-line |
| `PA` | Off-line |
| `OFF` | Off-line with Flush |
| `GTO` | Going To Off-line |
| `STB` | Stand-by |
| `EXE` | Exercise |
| `FR` | Paused |
| `SH` | Shutdown |

**4.2.2 Error-Type 取值**（23542）

| 值 | 含义 |
|----|------|
| `G` | Green |
| `P` | Pink |
| `Y` | Yellow |
| `R` | Red（或 Red- / Red*） |
| `R+` | Red+ |

**4.2.3 Run-Status 取值**（23543–23545）

| 值 | 含义 |
|----|------|
| `0` | Unknown |
| `1` | On-line |
| `2` | Transitioning To Paused |
| `3` | Paused |
| `4` | Initializing |
| `5` | Stop |
| `6` | Exercise |
| `7` | Off-line |
| `9` | Shutdown |
| `10` | Transitioning To Shutdown |
| `13` | Transitioning To Off-line |
| `14` | Transitioning To Exercise |
| `15` | Stopping |

**4.2.4 Consumable-Count 语义**（按 Node 类型，23551–23554）

| Node 类型 | 含义 |
|-----------|------|
| Decapper / Sealer / Recapper / Desealer / Aliquoter / Centrifuge | 消耗品计数器（number） |
| Liaison Analyzer | Backlog Time（秒） |
| Vista / ExL | Backlog Time（分钟） |
| Sapphire / SMS（Juliet 2 起） | Open Mode 的 Rack 数 |
| TRU（Node 0） | Overall Status（**Node 0 无此计数字段，用 Overall Status 代替**） |

**4.2.5 Consumable-Status 语义**（按 Node 类型，23555–23561）

| Node 类型 | 含义 |
|-----------|------|
| Decapper / Sealer / Recapper / Desealer / Aliquoter | 消耗品状态 |
| Storage（Golf 3 起） | Waste Counter |
| Centrifuge（Hotel 2 起） | Spin Cycles |
| Liaison Analyzer | 校准状态：`G`(Green)/`Y`(Yellow)/`R`(Red) |
| TRU（Node 0） | Software Version |
| EXL | `S`（模块支持 STAT lane） |
| Track Bypass Module（Juliet 1 起） | `T`（模块为 Track T） |
| Sapphire / SMS（Juliet 2 起） | `On`/`Off`（Open Mode 开/关） |

> **⚠️ 待核实（实测与手册不一致）**：手册本版本定义每 Node **15** 个字段（其中 12–14 为 R19 新增，即 R19 前为 12 个）。但此前 UniFlow 实测收到的回复为 `STATUS 1 0 0 0 0 0 0 0 0`（**9** 个值，且以空格分隔）。二者字段数与分隔符均不一致，**很可能为不同软件版本的 STATUS 变体**，需按实际运行 Dream 的版本重新核实（本手册 DCAU-H00.816.12.01.02 未定义该 9 字段形式）。

### 4.3 CLOCK（仅 Dream 发往自身，9.1，26645–26662）

```
CLOCK <Time>    # <Time> = 5MIN 或 DAY
```

Dream 通知自身启动指定时间框架的定时活动（26646–26647）。报文名与参数以**空格**分隔；仅 Dream 可发送（26634）。

**字段位置表**（索引 0 起）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Time（时间框架） | 枚举 | 触发定时活动（26648） | `5MIN` / `DAY` |

- `CLOCK 5MIN`（26649–26654）：评估每 5 分钟活动——backlog 暂存样本复测、存 Counter 文件、必要时存 Memory Log 文件。
- `CLOCK DAY`（26655–26660）：每日活动——存消耗品计数（.ini）并清零、重置 time motor check（DIAGNOSTICS 111/112）、重置 OUM/VTX BACKUP 检查、删除前一日已发往接口的错误。
- 该消息记录进 Dream Log（26662）。

### 4.4 SCHEDULE-TESTS（Dream 发往自身记录，9.74，28017–28026）

```
<Node-ID> <Node-Type> SCHEDULE-TESTS <Sample ID>^<Test1> … <Testn>
```

载物台连同管被偏转到 Analyzer 时，Dream 记录（写入 DREAM Log）已调度的测试（Kilo 3 起，28019–28021）。

**字段位置表**（索引 0 起，按格式串 28018 顺序）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer 的 ID（数字）（28022） | `5` |
| 1 | Node-Type（Analyzer Type） | string | Analyzer 类型（28023） | `LXL` |
| — | `SCHEDULE-TESTS` | 字面量 | 报文关键字（28018） | — |
| 2 | Sample-ID（样本ID） | string | 涉及管的样本 ID（R18 起）（28024） | `S00123` |
| 3… | Test1…Testn（测试代码） | string；**空格分隔** | 调度到该 Analyzer 的测试代码（28025–28026） | `CBC DIF` |

> ⚠️ **手册不一致**：格式串（28018）在 `<Sample ID>` 与 `<Test1>` 间写 `^`，但字段描述（28026）称 `<Test1>…<Testn>` **以空格（blanks）分隔**。二者冲突，测试间到底用 `^` 还是空格**手册未明示**，需以实际固件为准。

### 4.5 METHOD-CHANGED（Dream 发往自身记录，9.69，27955–27962）

```
METHOD-CHANGED <Node-Type>^<New-Method>^<Reason>
```

Dream 记录某 Analyzer Type 的 Method 已变更（Juliet 3 起，27957–27958）。字段以 `^` 分隔。

**字段位置表**（索引 0 起）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Node-Type（Analyzer Type） | string | Analyzer 类型（27959） | `LXL` |
| 1 | New-Method（新方法） | number | 该 Analyzer Type 变更到的新 Method（数字）（27960） | `2` |
| 2 | Reason（原因） | 枚举字符串 | Method 变更原因（见手册 §2.16）（27961–27962） | `First Sample`/`GUI`/`STAT`/`Timeout`/`Critical Mass` |

### 4.6 BRIDGE（Dream↔另一自动化系统，8.90，25701–26026）

```
BRIDGE <source-automation-ID> <destination-automation-ID> <command> [<parameters>]
```

R19.1 起（VTX 为 R24，25774）。Dream 经 Track Overpass/Underpass 或 VTX 模块，跨自动化系统交换载物台 ID / 管 ID / 可用性与过载状态。基础帧各 token 以**空格**分隔（25775）。

**基础帧字段表**（索引 0 起，25775–25781）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | source-automation-ID（源系统ID） | number/string | 当前（发送方）自动化系统 ID，取发送方 Bridge-Config.ini 的 “This Automation ID”（25777–25778） | `1` |
| 1 | destination-automation-ID（目标系统ID） | number/string | 目标自动化系统 ID，取发送方 Bridge-Config.ini 的 “Bridge to Automation”（25780–25781） | `2` |
| 2 | command（子命令） | 枚举 | 11 个子命令之一（见 4.6.1–4.6.11） | `INIT`/…/`REMOVE-TUBE` |
| 3+ | parameters（参数） | 依子命令 | 各子命令参数；各子命令内索引从其参数起 **0 起算** | — |

> 各子命令参数分隔符：`INIT`/`SHUTDOWN`/`GTO`/`CANCEL-GTO`/`REMOVE-TUBE` 等用空格或 `|`；`CARRIER-ARRIVES`/`CARRIER-RECEIVED`/`UNEXPECTED-CARRIER-RECEIVED` 用 `|`；`AUTOMATION-INFO` 用 `\`（组间）与 `^`（组内）；`TEST-INFO` 仅用 `\`（测试之间，无 `^`）。

#### 4.6.1 INIT（25802）

`BRIDGE <src> <dst> INIT <IP>:<port> [client]` —— 系统启动时由源系统发送，用 `<IP>`/`<port>`/可选 `client`（均取 Bridge-Config.ini）初始化与目标系统的通信。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例 |
|--------|--------|------|------|-----------|
| 0 | IP:port（IP:端口） | string | 目标系统 IP:port（Bridge-Config.ini）（25802–25804） | `192.168.1.1:8000` |
| 1 | client（客户端标志，可选） | 字面量 | 可选 client 参数（25802） | `client` |

#### 4.6.2 SHUTDOWN（25807）

`BRIDGE <src> <dst> SHUTDOWN <IP>:<port> [client]` —— 系统关闭时由源系统发送，关闭与目标系统的通信。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例 |
|--------|--------|------|------|-----------|
| 0 | IP:port（IP:端口） | string | 目标系统 IP:port（25807） | `192.168.1.1:8000` |
| 1 | client（客户端标志，可选） | 字面量 | 可选 client 参数（25807） | `client` |

#### 4.6.3 CARRIER-ARRIVES（25811–25814，参数以 `|` 分隔，共 14 字段）

`BRIDGE <src> <dst> CARRIER-ARRIVES <Carrier-ID>\|<Sample-ID>\|<OUM/VTX-Instance>\|<Routing-Node-Type>\|<Tube-Type>\|<Tube-Spinning>\|<Tube-Cap>\|<Tube-Cap Type>\|<Tube-Category>\|<Tube-Weight>\|<Tube-Sealed>\|<Tube-Number-Of-Seals>\|<Tube-Error-Code>\|<Sample-Volume>`

源系统通知目标系统：载有样本管的载物台已被路由到连接两系统的 OUM/VTX 模块（25815–25818）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Carrier-ID（载物台ID） | string | 载物台 ID（25819） | — |
| 1 | Sample-ID（样本ID） | string | 样本管 ID（25820） | — |
| 2 | OUM/VTX-Instance（实例号） | number | 源系统路由载物台所经的 OUM/VTX 模块实例（25821–25822） | — |
| 3 | Routing-Node-Type（路由节点类型） | string | 到达最终目的地系统后样本应路由到的节点类型（IOM 排序/按错误排序、LXL、HVS 等）；`UNEXPECTED`=载物台被意外偏转到该模块（GATE-WARNING 8004）（25823–25827） | `IOM`/`HVS`/`UNEXPECTED` |
| 4 | Tube-Type（管类型） | number | 3..14、16..19（见 APPENDIX M）；视觉系统未识别但为默认管类型时尾随 `D`（25828–25830） | `3`/`3D` |
| 5 | Tube-Spinning（是否离心） | enum | 0=unspun、1=spun（25831） | `0`/`1` |
| 6 | Tube-Cap（管盖） | enum | 0=unknown、N=not capped、C=capped（25832） | `C` |
| 7 | Tube-Cap Type（管盖类型） | number | 0=unknown、1..999（25833） | `15` |
| 8 | Tube-Category（管类别） | enum | P=Primary Tube、S=Secondary Tube（25834） | `P` |
| 9 | Tube-Weight（管重量） | number | 毫克（mg）；无则空字符串（25835） | `12` |
| 10 | Tube-Sealed（是否封口） | enum | 0=not sealed、1=sealed（25836） | `0`/`1` |
| 11 | Tube-Number-Of-Seals（封口次数） | number | 0..99（25853） | `1` |
| 12 | Tube-Error-Code（管错误码） | string | 最新样本错误码，无则 `0000`（25854） | `0000` |
| 13 | Sample-Volume（样本体积） | number | 样本体积，if available（25855）⚠️ 单位见下 | — |

> ⚠️ **手册未明示 / 不一致**：
> - **25856 额外列出 `<Internal-Diameter>`**（Tube Internal Diameter，1/10 mm），但该字段**未出现在格式串（25811–25814）的 14 个 `|` 字段中**。经核对该字段确为真实字段（通知消息 L001 见 22223、S009 见 22604 自 R19.1 起均含 `<Internal-Diameter>`），故此处应为格式串遗漏；BRIDGE CARRIER-ARRIVES 实际是否发送该字段**待核实**（以实际固件/抓包为准）。
> - `<Sample-Volume>` 单位手册原文写作 “in **516microliters**”（25855），单位表述异常，疑为 OCR/笔误，确切单位**手册未明示**。
> - 若接收方 OUM/VTX 实例号与发送方不同，消息被忽略（25857–25859）；接收方若因不活动被自动停止，则收到本消息会自动启动（25860–25861）。

#### 4.6.4 CARRIER-RECEIVED（25863–25864，参数以 `|` 分隔）

`BRIDGE <src> <dst> CARRIER-RECEIVED <Carrier-ID>\|<Sample-ID>\|<OUM/VTX-Instance>` —— 源系统通知目标系统：已从连接两系统的 OUM/VTX 模块收到一个预期载物台（25865–25868）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Carrier-ID（载物台ID） | string | 载物台 ID（25869） | — |
| 1 | Sample-ID（样本ID） | string | 样本管 ID；空载物台为空字符串（25870） | — |
| 2 | OUM/VTX-Instance（实例号） | number | 源系统从该 OUM/VTX 模块收到载物台的实例（25871–25872） | — |

#### 4.6.5 UNEXPECTED-CARRIER-RECEIVED（25877–25878，参数以 `|` 分隔）

`BRIDGE <src> <dst> UNEXPECTED-CARRIER-RECEIVED <Carrier-ID>\|<Sample-ID>\|<OUM/VTX-Instance>\|<Position>` —— 源系统通知：在 track 上检测到一个未预期的载物台（无对应 CARRIER-ARRIVES）（25879–25883）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Carrier-ID（载物台ID） | string | 载物台 ID（25884） | — |
| 1 | Sample-ID（样本ID） | string | 样本管 ID；空载物台为空字符串（25885） | — |
| 2 | OUM/VTX-Instance（实例号） | number | 源系统从该 OUM/VTX 模块收到载物台的实例（25886–25887） | — |
| 3 | Position（位置/检测方式） | enum | `0`=载物台经连接另一系统的 OUM/VTX 的 `RETURNED` 事件检测到；`1`=经该 OUM/VTX 之后的模块 `PASSED` 事件检测到（25888–25890、25907–25908） | `0`/`1` |

#### 4.6.6 AUTOMATION-INFO（25910–25911，组间 `\`、组内 `^`）

`BRIDGE <src> <dst> AUTOMATION-INFO <Node-Type-1>^<Available-Slots-1>\…\<Node-Type-n>^<Available-Slots-n>` —— 源系统通知目标系统各 Node Type 可接收的满载物台数（考虑 Bridge-Config.ini 过载阈值及其它可达系统可用性）（25912–25916）。

| 组内索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Node-Type-i（节点类型） | string | 配置的 Analyzer 类型 / AQM / HVS / SRM（仅此几类）（25917） | `LXL`/`AQM`/`HVS`/`SRM` |
| 1 | Available-Slots-i（可用槽位数） | number | 源系统可从目标系统接收的、发往该 Node Type 的满载物台数（25918–25919） | `5` |

> 多个 `<Node-Type-i>^<Available-Slots-i>` 组以 `\` 连接；Available-Slots 为本地可用槽位与远端可用槽位之和（25920 起）。

#### 4.6.7 TEST-INFO（25944，`\` 分隔）

`BRIDGE <src> <dst> TEST-INFO <Test-1>\…\<Test-n>` —— 源系统通知目标系统其可运行的测试列表（含其它可达系统的测试），用于阻止目标系统发送无法运行的载物台（25960 起）。

| 组索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| i | Test-i（测试代码） | string | 一个测试代码，多个以 `\` 分隔（25971） | `CBC`/`DIF` |

#### 4.6.8 ERROR（25976，空格分隔）

`BRIDGE <src> <dst> ERROR <Error-Code>` —— 目标系统在检测到与源系统的通信错误（或先前通信错误已恢复）时生成。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Error-Code（错误码） | string | `SC09C`=通信错误；`0000`=先前通信错误已恢复（25978–25980） | `SC09C`/`0000` |

#### 4.6.9 GTO（25983–25984，参数以 `|` 分隔）

`BRIDGE <src> <dst> GTO <OUM/VTX-Instance>\|<Router-Node-Type>` —— 源系统通知：连接源系统的 OUM/VTX 模块被设为 Going To Off-line（25985–25987）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | OUM/VTX-Instance（实例号） | number | 发送 GTO 命令的 OUM/VTX 模块实例（25988–25989） | `1` |
| 1 | Router-Node-Type（路由节点类型） | string | 空=Track Overpass 模块（此时其前的分隔符可省略）；`VTX`=VTX 模块（25990–25991） | ``（空）/`VTX` |

#### 4.6.10 CANCEL-GTO（25993–25994，参数以 `|` 分隔）

`BRIDGE <src> <dst> CANCEL-GTO <OUM/VTX-Instance>\|<Router-Node-Type>` —— 源系统通知：取消 “Going To Off-line” 命令（25995–25996）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | OUM/VTX-Instance（实例号） | number | 发送 online/offline 命令的 OUM/VTX 模块实例（25997–25998） | `1` |
| 1 | Router-Node-Type（路由节点类型） | string | 空=Track Overpass 模块（此时其前的分隔符可省略）；`VTX`=VTX 模块（26015–26016） | ``（空）/`VTX` |

#### 4.6.11 REMOVE-TUBE（26017–26018，参数以 `|` 分隔）

`BRIDGE <src> <dst> REMOVE-TUBE <Sample-ID>\|<OUM/VTX-Instance>\|<Router-Node-Type>` —— 源系统通知：指定管已在源系统经 COMMENT S011 被手动取出（26019–26021）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|--------|--------|------|------|------------------------|
| 0 | Sample-ID（样本ID） | string | 被手动取出的样本管 ID（26022） | `S00123` |
| 1 | OUM/VTX-Instance（实例号） | number | 管被取出时所在的 OUM/VTX 模块实例（26023–26024） | `1` |
| 2 | Router-Node-Type（路由节点类型） | string | 空=Track Overpass 模块；`VTX`=VTX 模块（26025–26026） | ``（空）/`VTX` |

### 4.7 ERROR（GUI / Host LIS / Dream → Dream，9.22，27146–27153）

```
ERROR <Error-Code>
```

GUI / Host LIS / Dream 通知一个错误（27147–27148）。报文名与参数以**空格**分隔。

**字段位置表**（索引 0 起）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Error-Code（错误码） | string | 所通知的错误码（27149）；SC001 特例见下 | `SC001` |

- 特例：`SC001`（Sample Delivery Request）可带格式 `SC001/<IOM-instance>` 或 `SC001/<IOM-instance>/<Automation-ID>`（投递到当前/其它自动化系统的特定 IOM 实例，27150–27153）。

### 4.8 INIT（Dream → Host LIS driver，8.20，23268–23282）

```
INIT <Comm-Parameters>|<Sender-ID>
```

Dream 发送，要求 Host LIS driver 初始化与 Host LIS 的通信。**GUI 不使用**（23269–23282）。顶层字段以 `|` 分隔。

**字段位置表**（索引 0 起）：

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含手册行号） |
|-----|--------|------|------|------------------------|
| 0 | Comm-Parameters（通信参数） | string | 通信方式，两种形式之一（23270–23280） | `TCP-IP 192.168.100.12:8000` |
| 1 | Sender-ID（发送方ID） | string | 写入 ASTM Header “Sender ID” 字段的字符串（23281） | `DREAM` |

**4.8.1 Comm-Parameters 两种形式**（23270–23280）

| 形式 | 语法 | 参数说明 | 示例 |
|------|------|----------|------|
| 串口 | `Serial COMxx [Baud Rate] [Parity-Data/Stop-Bits]` | Baud 与 PDS 可选（23272）；`[Baud Rate]`=9600/19200/38400…（23273）；`[PDS]`=P D S：P(Parity)=N/O/E/M/S，D(Data)=8/7，S(Stop)=1/2（23274–23277） | `Serial COM11 9600 N81`（23278） |
| TCP-IP | `TCP-IP <IP-address>:<port>` | IP 地址与端口（23279） | `TCP-IP 192.168.100.12:8000`（23280） |

### 4.9 SHUTDOWN（Dream → Host LIS driver，8.21，23284–23288）

```
SHUTDOWN
```

Dream 发送，要求 Host LIS driver 关闭。**GUI 不使用**（操作结果见手册 12.2 章，23284–23288）。

**字段位置表**：本报文**无参数**，仅报文关键字 `SHUTDOWN`（23285）。

---

## 5. 帧约定（分隔符 / 头 / 编码 / 行尾）

### 5.1 分隔符层级

| 层级 | 分隔符 | 用途 | 出现报文 | 手册依据（行号） |
|------|--------|------|----------|----------------|
| 顶层字段 | `\|`（主分隔符，竖线字符） | ORDER 顶层字段；INIT 顶层字段；BRIDGE 的 CARRIER-ARRIVES / CARRIER-RECEIVED / UNEXPECTED-CARRIER-RECEIVED / GTO / CANCEL-GTO / REMOVE-TUBE 参数 | ORDER、INIT、BRIDGE | 26693、23269、25811、25863、25877、25983、25993、26017 |
| 子字段 | `\`（子分隔符） | COMMENT 消息 ID 后字段间；多 Node STATUS 之间；BRIDGE 的 AUTOMATION-INFO 组间、TEST-INFO 列表 | COMMENT、STATUS、BRIDGE | 26778、23504、25910、25944 |
| 子分量 | `^`（子分量分隔符） | ORDER 内 Patient-ID / Test-Request / Sample-Source 等子分量；COMMENT 头尾（消息 ID 后、Type Flag 前）；单 Node STATUS 字段间 | ORDER、COMMENT、STATUS | 26703、26743、26715、26778、23504 |
| 测试列表内 | `;` | S002 COMPLETE 的 `Test1;…;Testn`；Default Test Orders 等 | S002（COMPLETE） | 26779 |
| 报文名/参数 | 空格 | 报文关键字与参数（CLOCK、ERROR、STATUS-REQUEST、INIT 参数等）；SCHEDULE-TESTS 测试代码之间 | CLOCK、ERROR、STATUS-REQUEST、SCHEDULE-TESTS | 26646、27147、27204、28026 |

> ⚠️ 手册内部矛盾：第 9 章开头（26639）称「Message fields shall not contain any of the ASTM separator characters (`|`, `\`, `^`)」，但多数字段定义本身用 `^`（及 `\`、`|`）分隔子分量/子字段。可理解为「这些字符不作为顶层字段内容，仅作结构分隔」，但表述存在冲突，需以实际固件为准（另见 §2.5）。

### 5.2 ASTM 头 / STX

| 项 | 说明 | 手册依据（行号） |
|----|------|----------------|
| 消息头 | 头为 STX（`\02`，即 0x02） | 26640 |
| Flexlab36（R23.1 起） | 接受带头或不带头的消息；头在转发给 DREAM 前被剥离 | 26640–26641 |
| Flexlab-X | 仅接受带头消息；无头则拒绝并断开与 Client 的连接 | 26641–26643 |

### 5.3 编码

| 项 | 说明 | 手册依据（行号） |
|----|------|----------------|
| 字符集 | ASCII（英文）；手册仅明确要求 GUI 标签用英文 ASCII（1945），报文级 ASCII 系据此推定；UniFlow 传输层采用 `Encoding.ASCII` | 1945（仅标签约束） |
| 标签约束 | GUI 标签配置须仅用 English ASCII 字符 | 1945 |

### 5.4 行尾 / 消息终止

| 项 | 说明 | 手册依据（行号） |
|----|------|----------------|
| 行尾符 | 第 8/9 章**未明确**规定 CRLF/CR 行尾符 | （手册未明示） |
| 相关证据 1 | CONFIG-FILES 消息示例使用 `\r\n`（CRLF） | 24810–24811 |
| 相关证据 2 | Error Recovery 特殊字符定义中 `\n` → carriage return | 11403 |
| ASTM 惯例 | E1394 以 CR（`\r`, 0x0D）作为消息终止符 | （通用惯例，非本手册） |
| UniFlow 现行 | 采用 CRLF（`\r\n`）发送（见 §6） | — |

> **待核实**：本手册未给出统一行尾定义。UniFlow 现行实现用 CRLF 发送，需与 Dream 端确认其接受 CRLF 还是仅 CR。

---

## 6. 附录：UniFlow 当前实现对应的报文与字段位置

UniFlow 源码（C#）：`code/src/UniFlow/DisposeSample/Services/AptioCommandService.cs`

### 6.1 ORDER 取消（Cancel）

代码库中有**两处**生成取消 ORDER 帧的实现，字段布局一致（Action-Code@12、Test-Request 从索引 17 起），但**多测试拼法不同**：

#### 6.1.1 `AptioCommandService.SendCancelTestAsync`（单测试，`DisposeSample/Services/AptioCommandService.cs` 第 40–46 行，帧在第 42 行）

实际生成帧（**单个**测试）：

```
ORDER {barcode}||||||||||||C|||||{test}
```

按 `|` 拆分后各索引内容：

| 索引 | 值 | 对应手册字段 |
|-----|-----|-------------|
| 0 | `{barcode}` | Sample-ID |
| 1–11 | 空 | Patient-ID … Patient Location |
| **12** | `C` | **Action-Code = Cancel** |
| 13–16 | 空 | Sample-Source / Ordering-Physician / Patient-Comment / Order-Comment |
| **17** | `{test}` | **Test-Request-1** |

- 与手册 §2.1 字段顺序一致：**Action-Code 在索引 12、Test-Request-1 在索引 17**。
- 单测试场景下，索引 17 只放一个测试码，**符合 §2.2 多测试规则（选项 A 的 n=1 特例）**。
- 相关提交：`818f174`（修正 DMS cancel ORDER 帧字段偏移，Action-Code C@12、Test-Request@17，此前整体 +1 偏移）、`40fe2b0`（cancel ORDER 帧带测试名）。
- **待核实**：取消时 Test-Request（索引 17）应填具体测试名还是可省略，手册未明说（见 §2.4）。

#### 6.1.2 `SampleCleanupService.BuildCancelCommand`（多测试，`DMSAutoOrder/Services/SampleCleanupService.cs` 第 76–84 行）

`ProcessTriggeredDeletionAsync` 删除样本前用 `GetTestsBySidAsync`（`DmsDatabaseService.cs` 第 145–153 行，`SELECT DISTINCT codtest FROM reqtest WHERE codsid=…`）取出**该样本在 reqtest 中的全部测试名**，再拼取消帧。多测试时实际生成帧形如：

```
ORDER {sid}||||||||||||C|||||{test1}^{test2}^{test3}
```

即索引 17 = `string.Join("^", tests)`（第 82 行）。

- **✅ 与协议一致（实测确认）**：`BuildCancelCommand` 把多个测试名用 `^` 拼进**索引 17 单个字段**（`string.Join("^", tests)`），Dream 端按 `^` 切分逐个识别，多测试取消均生效。
- **作用**：样本含多个测试时（`GetTestsBySidAsync` 返回全部 DISTINCT 测试），一次取消报文即可覆盖该样本所有测试。样本仅 1 个测试时同样有效（无 `^`）。

### 6.2 STATUS-REQUEST 连接探测 — `SendStatusRequestAsync`（第 48–51 行）

```
STATUS-REQUEST 1
```

- 对应手册 9.26，`<IOM-Instance>=1`。
- 相关提交：`5f1eb1d`（用 STATUS-REQUEST 探测重连的 Aptio 链路，因协议无 ACK）、`d9a58d8` / `91998f9`（Aptio 协议为 fire-and-forget，仅在写失败时重试，不因无响应重发）。
- 回复为 §4.2 的 STATUS 通知（15 字段/Node）；实测收到 9 值形式，见 §4.2 待核实项。

### 6.3 其它 UniFlow 已实现帧（同文件）

| 方法（行号） | 实际帧 | 对应手册 |
|--------------|--------|----------|
| `SendDisposeAsync`（16–22） | `COMMENT S002^{barcode}\TRASH^S` | §3.1 S002 `TRASH` 属性 |
| `SendDeliverAsync`（24–30） | `COMMENT S002^{barcode}\DELIVER^S` | §3.1 S002 `DELIVER` 属性 |
| `SendStatPriorityAsync`（32–38） | `COMMENT S010^{barcode}^S` | §3.2 S010；⚠️ 代码省略了手册格式中的 `<New-Priority>` 子字段（手册要求 `\<New-Priority>`，`A`=ASAP/`S`=STAT） |

传输层（`code/src/UniFlow/Common/Services/AptioSocketClient.cs`）：`Encoding.ASCII`、`NewLine = "\r\n"`（CRLF），与 §5 一致。

---

## 7. 覆盖报文清单

**GUI→Dream / LIS→Dream**：ORDER、COMMENT（S002/S008/S010/S011/S012/S016/S018、I001/I003/I005/I009/I011/I013/I015/I026/I027 等）、ERROR、STATUS-REQUEST。
**Dream→GUI / DMS IUI**：STATUS（Notification，§4.2）。
**Dream→Host LIS（LIS driver / 解析方向，第 8 章）**：INIT（§4.8）、SHUTDOWN（§4.9）、以及 **COMMENT 通知类**——样本生命周期 L001/L002/L003/L004/L005/L006、S001/S003/S004/S005/S006/S007/S009/S013/S014/S015/S017/S021/S022/S024/S025/S026、仪器/配置 I002/I004/I006/I007/I008/I010/I012/I014/I016/I018/I020/I028、架事件 R001。**第 8 章 8.1–8.103 全部 103 小节完整清单见 §9**（含发往 GUI/IUI 的诊断/配置通知）。
**Dream→自身记录**：CLOCK、SCHEDULE-TESTS、METHOD-CHANGED。
**Dream↔跨自动化系统**：BRIDGE（§4.6）、VTM-CARRIER-ARRIVES（§9.2.22）、VTM-OVERLOAD（§9.2.23）。

## 8. 不确定 / 手册未明确点（汇总）

1. **ORDER Cancel 语义**：手册未给 Cancel 专用帧（复用 ORDER 帧，Action-Code=C），未说明取消时 `Test-Request` 是否必须省略 / 列出具体测试 / 省略是否等于取消全部（§2.4）。**多测试拼法已确认（实测）**：多个测试名用 `^` 连接放进索引 17 单字段（如 `BNP^RMSMP`），Dream 端按 `^` 切分逐个识别（§2.2、§6.1.2）。`SampleCleanupService.BuildCancelCommand` 当前实现即此格式，与实测一致。
2. **STATUS 字段数**：手册定义每 Node 15 字段（R19 前 12），实测 9 值形式不符，疑为旧版本变体（§4.2）。
3. **行尾符（CRLF vs CR）**：手册第 8/9 章未明确，仅提及 STX 头；UniFlow 现用 CRLF 待 Dream 端确认（§5）。
4. **`^` 非法字符约束的内部矛盾**：顶层字段不得含 `^`，但多个字段定义用 `^` 分子分量，需以实际固件为准（§2.5）。
5. **CLOCK / SCHEDULE-TESTS / METHOD-CHANGED / BRIDGE** 均为 Dream 侧（发往自身或跨系统）消息，UniFlow（作为 Host LIS）通常不直接产生，本文件按手册如实记录。
6. **测试结果（RESULT / ASTM R 帧）无 Dream 侧帧**：手册第 8 章**未定义**任何由 Dream 生成的“RESULT / R”结果帧。原文 1175 行明确「The Analyzer sends the test results to DMS through the **LIS protocol**」——**带数值的结果由 Analyzer（经 Interface Module）直接经 LIS 协议上报 DMS，不经过 Dream**。Dream 侧只发“测试/样本状态”通知（S024 测试状态、S026 已完成测试列表、S009/S025 样本状态）。因此 UniFlow 若需解析**结果数值**，应监听 Analyzer→DMS 的 ASTM R 帧（本手册未给出其字段定义，见 §9.0）。
7. **COMMENT 各系列 ID 与首字段分隔符不一致**：S 系列 / I 系列为 `COMMENT <ID>^<F0>\…`（ID 后接 `^`）；**L 系列 / R 系列为 `COMMENT <ID> <F0>\…`（ID 后接空格）**（L001=22222、R001=25323 vs S001=22289、I002=22772）。解析器须同时兼容两种形态，勿臆断统一为 `^`（详见 §9.0）。
8. **ORDER 无显式回执 / ACK**：手册未定义 Dream 收到 ORDER 后向 Host LIS 回发的确认帧。§9.1 中 8.59 `ACK`/`NAK` 仅针对 **GUI 侧命令**（ADVANCED-CONFIG-CHANGE、CLEAR-TUBES、COMMENT I021–I024 等，24875–24881），**与 Host LIS 的 ORDER 无关**。Dream 无 ORDER ACK（与 §1 注、§6.2 STATUS-REQUEST 探测法一致）。
9. **大量可选字段随 LIS.ini 配置 / 软件版本变化**：L001/S001/S003/S005/S009 等消息中的 `<Duplicate-Tube-Number>`、`<Input-Lane>`、`<Carrier-ID>`、`<Internal-Diameter>`、`<Node-ID>`、`<Shaking/Router-Timestamp>`、`<Number-of-Sealings>` 等字段**均带发送条件**（仅当对应 LIS.ini 开关 Enable 且软件版本达标才发送），且部分版本出现 `\\`（双反斜杠，空槽位保留）或省略尾字段。**解析时不能假定字段数固定**，应按“存在性 + 配置”动态解析（各字段条件见 §9.2 各行号）。
10. **L004 的 Tube-Type/Cap-Type/Spinning 不发送**：8.73（25286）明确 L004 中 `<Tube-Type>`、`<Cap-Type>`、`<Spinning>` **不发往 Host LIS**（格式串保留但为空），解析须忽略这三槽位。

---

## 9. Dream→GUI / Host LIS 通知类报文（解析方向，手册第 8 章）

> 本章对应手册**第 8 章「Messages from Dream to Host LIS or GUI」**（`/tmp/dcau.txt` 22170 行起，共 8.1–8.103，103 小节）。方向为 **Dream 主动发往 GUI 或 Host LIS**，即 UniFlow（Host LIS）的**解析侧**。行号均指 `/tmp/dcau.txt`。
>
> 第 8 章开头方向规则（22196–22200）：
> - 「All messages can be sent to GUI, **except INIT and SHUTDOWN**, which can be sent to Host LIS only」（22196）
> - 「**COMMENT, ORDER, INIT and SHUTDOWN** messages can be sent to Host LIS」（22197）
> - 「Before being sent to Host LIS, **COMMENT** messages are translated into ASTM format」（22198–22199）
> - 「Message fields shall not contain any of the ASTM separator characters (|, \, ^)」（22200，与 §2.5/§5.1 的内部矛盾相同）
>
> 据此：**只有 COMMENT / INIT / SHUTDOWN（及 ORDER）真正发往 Host LIS**；其余「xxx Notification」（SAMPLE-INFO、TRACK-STATUS、CONFIG、诊断类、固件/传感器/电机/门状态等）**仅发往 GUI / DMS IUI**，用于 GUI 屏幕显示，通常不进 Host LIS 通道。§9.1 完整清单中已逐条标注方向。

### 9.0 关键说明（结果上报 / 无 ORDER 回执 / 分隔符差异）

1. **测试结果不经 Dream**：第 8 章**无** Dream 生成的 RESULT / ASTM R 帧。带数值的结果由 **Analyzer → DMS 直接经 LIS 协议**上报（1175 行「The Analyzer sends the test results to DMS through the LIS protocol」；1170–1177 描述 Analyzer 经 LIS 收订单、LAS 报告完成、LIS 回结果、SMS 做后处理并发 Sample Process Notification）。Dream 只发**状态类**通知：**S024**（测试状态响应）、**S026**（M-HTC 已完成测试码列表）、**S009 / S025**（样本状态 I/C/D）、**S021**（排序测试 Scheduled→Sorted）。→ UniFlow 若需结果数值，须另监听 Analyzer→DMS 的 R 帧（本手册未定义其字段，属 ASTM E1394，**(手册未明示)**）。
2. **ORDER 无回执**：Dream 收到 ORDER（§2）后**不回发确认**（**(手册未明示)**，与 §1 注、§6.2 一致）。8.59 `ACK`/`NAK`（24869–24881）仅针对 **GUI 命令**（ADVANCED-CONFIG-CHANGE / CLEAR-TUBES / COMMENT I021–I024），与 Host LIS 的 ORDER 无关。
3. **消息 ID 与首字段分隔符**（解析必须注意，见 §8 第 7 条）：
   - **S 系列 / I 系列**：`COMMENT <ID>^<F0>\…\<Fn>^<TypeFlag>`（ID 后接 **`^`**），如 S001（22289）、I002（22772）。
   - **L 系列 / R 系列**：`COMMENT <ID> <F0>\…\<Fn>^<TypeFlag>`（ID 后接 **空格**），如 L001（22222）、R001（25323）。
   - 尾类型标志：`^S`=样本、`^L`=装载/生命周期、`^I`=仪器、`^R`=架，**不参与索引**。
4. **字段索引约定**：与 §3.1 一致——`^`（或空格）后的第一个 `\` 字段为索引 0，按 `\` 顺序递增；`\\`（双反斜杠）表示中间保留一个**空槽位**（该槽位仍占一个索引）；尾 `^<TypeFlag>` 不占索引。
5. **可选字段**：多数 L/S 消息的 `<Duplicate-Tube-Number>`、`<Input-Lane>`、`<Carrier-ID>`、`<Internal-Diameter>`、`<Node-ID>`、时间戳类字段**仅在对应 LIS.ini 开关启用 + 软件版本达标时发送**（各字段条件见下表行号），字段总数**不固定**（见 §8 第 9 条）。

### 9.1 第 8 章完整清单（8.1–8.103，103 小节）

> 「方向」列：**LIS**=发往 Host LIS；**GUI**=仅 GUI / DMS IUI；**跨系统**=经 LIS3/VTX/BRIDGE 通道发往另一自动化系统。「详述」列指向本文件字段表小节。

| 小节 | 报文 | 简格式（关键字 + 首字段） | 方向 | 手册行号 | 用途 / 详述 |
|-----|------|--------------------------|------|---------|-----------|
| 8.1 | COMMENT L001 | `L001 <SID>\…^L` | LIS | 22202–22281 | 样本接收通知（§9.2.1） |
| 8.2 | COMMENT S001 | `S001^<SID>\…^S` | LIS | 22283–22330 | 样本离轨定位（§9.2.2） |
| 8.3 | COMMENT S003 | `S003^<SID>\…^S` | LIS | 22332–22343 | 采样后回主轨（§9.2.3） |
| 8.4 | COMMENT S004 | `S004^<SID>\…^S` | LIS | 22345–22382 | 自动化告警/异常（§9.2.4） |
| 8.5 | COMMENT S005 | `S005^<SID>\<Event>\…^S` | LIS | 22384–22534 | 样本处理事件（§9.2.5） |
| 8.6 | COMMENT S006 | `S006^<SID>\<Node>\…^S` | LIS | 22536–22546 | 路由到 Analyzer（§9.2.6） |
| 8.7 | COMMENT S007 | `S007^<SID>\<DT>\<Pri>^S` | LIS | 22548–22598 | 优先级升级（§9.2.7） |
| 8.8 | COMMENT S009 | `S009^<SID>\…^S` | LIS | 22602–22651 | 样本状态响应（S008 应答）（§9.2.8） |
| 8.9 | COMMENT S013 | `S013^<SID>\…^S` | LIS | 22653–22770 | 分装完成（§9.2.9） |
| 8.10 | COMMENT I002 | `I002^<Node>^<Notif>^I` | LIS | 22771–22776 | Analyzer 启停（§9.3.1） |
| 8.11 | COMMENT I004 | `I004^<Node>\<Type>\…^I` | LIS/GUI | 22778–22810 | 试剂库存（§9.3.2） |
| 8.12 | COMMENT I006 | `I006^<Node>\<Type>\<T>^I` | LIS/GUI | 22812–22818 | 测试面板（§9.3.3） |
| 8.13 | COMMENT I007 | `I007^<Node>\<Test>\<Notif>^I` | LIS | 22820–22826 | 测试启用/禁用（§9.3.4） |
| 8.14 | COMMENT I010 | `I010^<Comment-Fields>^I` | LIS/GUI | 22828–22867 | 测试映射（§9.3.5） |
| 8.15 | COMMENT I012 | `I012^<Comment-Fields>^I` | LIS | 22869–22916 | 测试信息（§9.3.6） |
| 8.16 | COMMENT I014 | `I014^<Comment-Fields>^I` | LIS | 22918–23156 | 排序测试（§9.3.7） |
| 8.17 | COMMENT I016 | `I016^<Comment-Fields>^I` | LIS | 23157–23177 | 分装测试（§9.3.8） |
| 8.18 | COMMENT I018 | `I018^<Comment-Fields>^I` | LIS | 23179–23229 | 分装管配置（§9.3.9） |
| 8.19 | COMMENT I020 | `I020^<Comment-Fields>^I` | LIS | 23231–23266 | 默认测试订单（§9.3.10） |
| 8.20 | INIT | `INIT <Comm-Params>\|<Sender-ID>` | LIS | 23268–23282 | 见 §4.8 |
| 8.21 | SHUTDOWN | `SHUTDOWN` | LIS | 23284–23288 | 见 §4.9 |
| 8.22 | SAMPLE-LIST | `<SID1>^<F>…\…` | GUI | 23309–23316 | 样本清单（SAMPLE-INFO-REQUEST 应答） |
| 8.23 | SAMPLE-INFO | `p01\|…\|p126` | GUI/IUI | 23318–23442 | 样本全信息（§9.4） |
| 8.24 | TUBE-LOG | `<SID>\|<TS>^<Node>^<Evt>^<Err>^<Par>\|…\|<Dup>` | GUI | 23447–23488 | 管日志（每条目 TS^Node/Type^Evt^Err^Par，尾随 Duplicate-Tube-Number，格式串 23455–23457） |
| 8.25 | STATUS | `<Node1>^…\…` | GUI/DMS IUI | 23490–23768 | 见 §4.2 |
| 8.26 | SOFTWARE-INFO | `<Ver>\|<LIS-Name>\|…` | GUI/DMS | 23769–23810 | 软件版本/LIS 名/Automation-ID |
| 8.27 | EXCEPTIONS | `<TS>^<Node>^<Code>^<Type>\…` | GUI | 23812–23824 | 异常列表 |
| 8.28 | CONFIG | `<Entry1>^<Val1>\|…` | GUI | 23826–24035 | 自动化配置 |
| 8.29 | ANALYZER-CONFIG | `<AT1>^<Class>-<Max-Empty>^<Proc>^<TT>^<AR>\…` | GUI | 24037–24050 | Analyzer 配置（格式串 24038） |
| 8.30 | OVERLOAD-CONFIG | `<AT1>^<Thr>^<Split>\|…` | GUI | 24052–24076 | 过载配置（格式串 24053） |
| 8.31 | USER-CONFIG | `<UID1>^<Name>^<Level>\|…` | GUI | 24078–24091 | 用户配置（格式串 24079） |
| 8.32 | ADV-CFG-EMPTY-CARRIERS | `<NID1>\|<Type> <Inst>\|<EmptyCarriers>\…` | GUI | 24093–24102 | 高级配置-空载物台（格式串 24094） |
| 8.33 | ADV-CFG-EXERCISE | `<NID1>\|<Type> <Inst>\|<ExType>\|<ExStatus>\|<ExChg>\…` | GUI | 24104–24147 | 高级配置-Exercise（格式串 24130；`<ExChg>` R23 起） |
| 8.34 | ADV-CFG-LIS | `<Entry1>\|<Val1>\…` | GUI | 24149–24154 | 高级配置-LIS（格式串 24150） |
| 8.35 | ADV-CFG-TESTS-VS-CAP | `<Test1>\|<Cap-Colors>\…` | GUI | 24156–24183 | 高级配置-测试对盖色（格式串 24157） |
| 8.36 | ADV-CFG-VISION-SYSTEM | `<NID1>\|<Type> <Inst>\|<VisionStatus>\…` | GUI | 24185–24196 | 高级配置-视觉系统（格式串 24186） |
| 8.37 | LAYOUT-CONFIG | `<NID1>^<Type>^<Next/Jump>^…^<Mod-X-Tech>\|…` | GUI | 24198–24244 | 布局配置（格式串 24199） |
| 8.38 | COUNTER | `<Type> <Node> <Inst>\|<T>\|<P>\…` | GUI | 24246–24258 | 计数器（门循环/管数） |
| 8.39 | TRACK-STATUS | `<Carriers>^<Tubes>^…` | GUI | 24260–24403 | track 状态计数 |
| 8.40 | COMPUTER-STATUS | `<DB-Records>^<Memory-MB>` | GUI | 24405–24410 | 计算机状态（格式串 24406） |
| 8.41 | OVERLOAD-STATUS | `<AT1> <Inst1>^<Thr>^<Cnt>^<Y/N>\…` | GUI | 24412–24427 | 过载状态（格式串 24413） |
| 8.42 | FIRMWARE-VERSION | `<NID1>^<Type>^<Inst>^…\…` | GUI | 24429–24478 | 固件版本（按版本 3 种变体：24435/24438/24460） |
| 8.43 | SENSOR-STATUS | `<Node>\|<Type>\|<Err>^<S1>^…` | GUI | 24480–24487 | 传感器状态 |
| 8.44 | MOTOR-STATUS | `<Node>\|<Type>\|<Err>^…` | GUI | 24489–24519 | 电机状态 |
| 8.45 | GATE-STATUS | `<Node>\|<Type>\|<Err>^<G1>^…` | GUI | 24521–24528 | 门状态 |
| 8.46 | STAT-LANE-GATE-STATUS | `<Node>\|<Type>\|<Err>^…` | GUI | 24530–24538 | STAT 通道门状态 |
| 8.47 | NODE-SETTINGS | `<Node>\|<Type>\|<Err>^<S1>^…` | GUI | 24540–24547 | 节点设置 |
| 8.48 | DIAGNOSTICS-COMPLETED | `<Node>\|<Type>\|<Err>^<Cmd>^<Val>` | GUI | 24569–24578 | 诊断完成 |
| 8.49 | LOGON | `<Access-Level>` | GUI | 24580–24589 | 登录（Guest/Operator/…） |
| 8.50 | USER | `<User-Name>` | GUI | 24591–24596 | 用户名 |
| 8.51 | LOCKED | `<NID>\|<GUI-ID>\|<PC>\|<IP>\|<Allow>` | GUI | 24598–24629 | 锁定（他 Client/Web App 诊断占用，格式串 24599） |
| 8.52 | MSG | `<Message>` | GUI | 24631–24643 | 提示消息（备份/恢复等） |
| 8.53 | MOTION-INFO | `<Node>\|<Type>\|<Err>^…` | GUI | 24645–24706 | 运动信息（IOM/CM/SRM/mHVS） |
| 8.54 | ADV-CFG-CARRIER-BUFFER | `<NID1>\|<Type> <Inst>\|<Val>\|<TmpAssign>\…` | GUI | 24708–24787 | 高级配置-载物台缓冲（Golf 1 起，格式串 24737） |
| 8.55 | SRM-RACKS | `<Node>\|<Rack1>\…` | GUI | 24789–24805 | SRM 架表 |
| 8.56 | CONFIG-FILES | `START-FILE-DATA<name>\r\n…` | GUI | 24809–24815 | 配置文件内容（CRLF） |
| 8.57 | CONFIRM-SHUTDOWN | `<Nodes1>\|<Nodes2>\|<Racks>` | GUI | 24817–24860 | 关闭确认（格式串 24849） |
| 8.58 | GUI-ID | `<GUI-ID>` | GUI | 24862–24867 | GUI ID |
| 8.59 | ACK / NAK | `ACK` / `NAK` | GUI | 24869–24881 | 命令确认（仅 GUI 命令，非 ORDER） |
| 8.60 | COMMENT S014 | `S014^<SID>\<Node>\<Wt>^S` | LIS | 24924–24930 | 管称重（§9.2.10） |
| 8.61 | FW-VERS | `<Node>\|<Type>\|<Err>^…` | GUI | 24932–24965 | 固件版本头 |
| 8.62 | COMMENT I008 | `I008^<Node>\<Test>\<Notif>^I` | LIS | 24967–24975 | 试剂耗尽/加载（§9.3.11） |
| 8.63 | CONSUMABLE-COUNTER | `<Start>^<End>^<Seals>^…` | GUI | 24977–24991 | 消耗品计数 |
| 8.64 | COMMENT S015 | `S015^<SID>\<Node>\<TType>\…^S` | LIS | 25009–25033 | 血清体积（§9.2.11） |
| 8.65 | SENSORS | `<Node>\|<Type>\|<Err>^<S1>^…` | GUI | 25037–25045 | 传感器（India 1 起） |
| 8.66 | COMMENT L002 | `L002 <SID>\…^L` | LIS | 25066–25101 | 样本重载（§9.2.12） |
| 8.67 | COMMENT L003 | `L003 <SID>\…^L` | LIS | 25123–25149 | 样本重识别（§9.2.13） |
| 8.68 | CURRENT-METHOD | `<Node1>^<Type>^<M>^<Exp>^<N>\…` | GUI | 25151–25187 | 当前 Method |
| 8.69 | MOTOR-CURRENT | `<Node>\|<Type>\|<Err>^…` | GUI | 25189–25200 | 电机电流（UTC） |
| 8.70 | MOTOR-THRESHOLD | `<Node>\|<Type>\|<Err>^…` | GUI | 25202–25212 | 电机阈值（UTC） |
| 8.71 | COMMENT I028 | `I028^<Comment-Fields>^I` | LIS | 25232–25249 | Analyzer 测试码（§9.3.12） |
| 8.72 | COMMENT S017 | `S017^<PriSID>\<SecSID>\<Node>\<Err>^S` | LIS | 25251–25261 | 样本合并（Pooling）（§9.2.14） |
| 8.73 | COMMENT L004 | `L004 <SID>\…^L` | LIS | 25263–25295 | 出 Storage（§9.2.15） |
| 8.74 | COMMENT L005 | `L005 <SID>\…^L` | LIS | 25297–25320 | 分装管贴标（§9.2.16） |
| 8.75 | COMMENT R001 | `R001 <Rack>\<Node>\<Evt>\<TS>^R`（空格） | LIS | 25322–25348 | 架事件 HVS/ROM-400（§9.2.17） |
| 8.76 | COMMENT L006 | `L006 <SID>\…^L` | LIS | 25352–25397 | 经 VTM/OUM 到达（§9.2.18） |
| 8.77 | VTM-CARRIER-ARRIVES | `<VTM>\|<Carrier>\|<SID>` | 跨系统 | 25399–25417 | 经 VTM 送载物台（§9.2.22） |
| 8.78 | VTM-OVERLOAD | `<Status>` | 跨系统 | 25419–25463 | VTM 过载状态（§9.2.23） |
| 8.79 | MAINTENANCE | `<TS1>^<NID>^<Type/Inst>^<Desc>^<Status>\…` | GUI | 25465–25483 | 预防性维护（Oscar 1 起，格式串 25466） |
| 8.80 | RECOVERY-REQUIRED | `<NID>\|<Err>` | GUI | 25485–25513 | 需恢复（Oscar 1 起，格式串 25489） |
| 8.81 | FW-DETAILS | `<Node>\|<Type>\|<Err>^<Desc>` | GUI | 25515–25522 | 错误描述 |
| 8.82 | RESERVED-SETTINGS | `<E1>^<V1>\…` | GUI | 25524–25531 | Reserved.ini 设置 |
| 8.83 | SRM-TABLE | `<Node>\|<Rack1>\…` | GUI | 25533–25543 | SRM 表（SRMX） |
| 8.84 | ROUTING-INFO | `<SID1>\|<CandList>\|<TS>\…` | GUI/IUI | 25564–25585 | 候选路由节点列表 |
| 8.85 | COMMENT S021 | `S021^<SID>\<Node>\<Dup#>\<T1>\…^S` | LIS | 25588–25601 | 排序测试 Scheduled→Sorted（§9.2.19） |
| 8.86 | DISABLED-SLOTS | `<Node>\|<Type>\|<Err>^…` | GUI | 25622–25637 | 禁用槽位（HVS/SRM） |
| 8.87 | RACK-LOCATION | `<Node>\|<Type>\|<Err>^…` | GUI | 25639–25646 | 架位置（HVS） |
| 8.88 | COMMENT S022 | `S022^<SID>\<Node>\<H>\<I>\<L>^S` | LIS | 25650–25659 | HIL 指数（SIM 模块）（§9.2.20） |
| 8.89 | COMMENT S024 | `S024^<SID>\<Dup>\<TC1>;<St1>\…^S` | LIS | 25677–25699 | 测试状态响应（S023 应答）（§9.2.21） |
| 8.90 | BRIDGE | `BRIDGE <src> <dst> <cmd> […]` | 跨系统 | 25701–26028 | 见 §4.6 |
| 8.91 | COMMENT S025 | `S025^<SID>\<St>\<Dup>^S` | LIS | 26030–26046 | 样本状态变更 I/C/D（§9.2.24） |
| 8.92 | LABEL-CONFIGURATION | `<Node>\|<Type>\|<Lane>\|…` | GUI | 26050–26078 | 通道标签 |
| 8.93 | LANE-CONFIGURATION | `<Node>\|<Type>\|<Lane>\|…` | GUI | 26080–26092 | 通道配置 |
| 8.94 | SENSOR-HEADERS | `<Node>\|<Type>\|<Err>^…` | GUI | 26096–26198 | 传感器表头（R20 起） |
| 8.95 | SCHEDULED-RETRIEVALS | `<Node>\|<Mode>\|<N>` | GUI | 26200–26239 | 待取回计数 |
| 8.96 | TRACK-SWITCH-STATUS | `<NT1>^<NID1>^<Thr1>^…` | GUI | 26243–26261 | track 开关模块状态 |
| 8.97 | COMMENT S026 | `S026^<SID>\<Node>\<DT>\<Dup>\<TC>\…^S` | LIS | 26263–26298 | M-HTC 已完成测试（§9.2.25） |
| 8.98 | MODULE-STATUS | `<NT>;<NID>;<Inst>;<E>;<V>\…` | DMS | 26302–26352 | 模块计数（Track/WBB/HVS/SRM） |
| 8.99 | CONSUMABLES | `<Node>\|<Type>\|<Mod>;<Name>;<Lv>\…` | DMS | 26356–26384 | 消耗品等级（Green/Yellow/Red） |
| 8.100 | IOM-TRAY-LIST | `<Trays.json>` / `[];` | GUI | 26406–26413 | IOM 托盘列表（GPI） |
| 8.101 | IOM-SETTINGS | `<NID>^<Type>^<lc…>^<TRX…>^<D/S…>/…` | GUI | 26417–26545 | IOM 通道配置 |
| 8.102 | ADV-CFG-STORAGE-X | `<NID>\|<Type> <Inst>\|<Val>\|` | GUI | 26547–26583 | Storage X 过载停放（R23 起） |
| 8.103 | RESERVED-ENTRIES | `<E1>`<V1>`<Def>`<DType>`<Ex>~…` | GUI | 26585–26620 | Reserved 条目 |

> 说明：「简格式」列约定：`\|` = 字面 `|` 字符（表格内转义写法）；`\…` = 省略的重复字段；`<…>` 为字段缩写。部分配置/诊断消息的字段数**随软件版本变化**（如 FIRMWARE-VERSION 三种变体 24435/24438/24460、SRM-RACKS 两种变体 24791/24799），完整字段请查各小节行号原文。此类消息均为 **GUI/IUI 通知**，非 Host LIS 解析重点。

### 9.2 样本生命周期 / 状态通知（发往 Host LIS，含字段位置表）

> 索引 0 起算；`\\`=空槽位仍占索引；尾 `^S/^L` 不占索引。可选字段条件见各行号。

#### 9.2.1 L001 样本接收通知（8.1，22222–22281）

`COMMENT L001 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\<Input-Lane>\<Carrier-ID>\<Internal-Diameter>^L`（**ID 后为空格**）

样本首次载入 / 再次载入 / 重识别 / 前次 SC022/SC023 错误修正时发往 Host LIS（22224–22233）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID；R19.1 且 LIS Enhanced Protocol 启用时，不可读管/无效 SID 用 `Unreadable#nnnn` / `Invalid-SID#nnnn`（22234–22238） | `S00123` |
| 1 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22255） | `20260915212516` |
| 2 | Tube-Type（管类型） | number | 0、3..14、16..19（APPENDIX M）；India 1 起视觉未识别时用默认值尾随 `D`（22256–22259） | `3`/`3D` |
| 3 | Cap-Type（管盖类型） | enum | 0(unknown)/N(Uncapped)/C(未知盖)/1..999；LIS Name=Nemo 且源为 Aliquoter 时为分装管后缀前缀 `#`（22260–22263） | `C`/`15` |
| 4 | Spinning（是否离心） | enum | 0(unknown/未离心)、1(已离心)（22264） | `0`/`1` |
| 5 | Node-ID（接收节点ID） | number | 接收样本的节点（IOM/BIM/RIM/SUM/MIO/H81/V2T 或 R18.1 起 TTR）（22265–22266） | `1` |
| 6 | Duplicate-Tube-Number（复管号） | number | 0=原管，≥1=复管（系统对同一 ID 最多 15 根复管，SC077 见 35915）；Hotel 3 起且 “LIS Additional Field for Duplicate Tubes” 启用才发（22267–22269） | `0` |
| 7 | Input-Lane（输入通道） | number（1–20） | Papa 1 起且 “LIS Include Lane in L001 and L002” 启用才发（22270–22272） | `5` |
| 8 | Carrier-ID（载物台ID） | string | R19.1 起且 “LIS Enhanced Protocol”+“Include Lane” 均启用才发；发送时消息延迟至 RETURNED（22273–22278） | `C0001` |
| 9 | Internal-Diameter（管内径） | number | R19.1 起且 Enhanced Protocol+Include Lane+“LIS Send Internal Tube Diameter” 均启用才发（1/10 mm，见 §4.6.3 注）（22279–22281） | `13` |

#### 9.2.2 S001 样本定位通知（8.2，22289–22330）

`COMMENT S001^<Sample-ID>\<Node-ID>\<Rack-ID>\<Floor>\<RackLane>\<RackPos>\<DateTime>\<Duplicate-Tube-Number>\<Expected-Disposal-Timestamp>\<PNP-Expiration-Timestamp>^S`

样本被移到**离轨位置**（Storage/HVS/PNP Analyzer/Centrifuge）时发（22291）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID；R19.1 Enhanced Protocol 下含 `Unreadable#nnnn`/`Invalid-SID#nnnn`（22292–22312） | `S00123` |
| 1 | Node-ID（节点ID） | number | 样本所在 Node（22313） | `15` |
| 2 | Rack-ID（架ID） | string | 管所在 Rack（22314） | `R01` |
| 3 | Floor（层） | number（1–20） | 仅 SRM 有（22315） | `3` |
| 4 | RackLane（架通道） | number（1–20） | 管所在 lane（22316） | `2` |
| 5 | RackPos（架位置） | number（1–48） | 管在架内位置（22317） | `12` |
| 6 | DateTime（时间戳） | string | 管被定位到架的时间 `YYYYMMDDHHMMSS`（22318） | `20260915212516` |
| 7 | Duplicate-Tube-Number（复管号） | number | Hotel 3 起且配置启用才发；0=原管（22319–22321） | `0` |
| 8 | Expected-Disposal-Timestamp（预计丢弃时间） | string | R19.1 起且 Enhanced Protocol 启用、仅 Storage/Mosaic HVS 时发（22322–22325） | `20260916000000` |
| 9 | PNP-Expiration-Timestamp（PNP超期时间） | string | R19.1 起且 Enhanced Protocol 启用、仅配置了 overdue timeout 的 PNP Analyzer/Centrifuge 时发（22326–22330） | `20260916000000` |

#### 9.2.3 S003 采样通知（8.3，22333–22343）

`COMMENT S003^<Sample-ID>\<Node-ID>\<DateTime>\\<Carrier-ID>^S`（`\\` 中间留一个空槽位）

管被 Analyzer（推测）采样后**回主轨**时发（22334）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（22336） | `S00123` |
| 1 | Node-ID（节点ID） | number | 回送样本的 Node（22337） | `7` |
| 2 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22338） | `20260915212516` |
| 3 | （空槽位 / Duplicate-Tube-Number） | — | 保留给 Quebec 1.1 的 `<Duplicate-Tube-Number>`，空字符串；**仅当发送 `<Carrier-ID>` 时才存在**（22341–22343） | （空） |
| 4 | Carrier-ID（载物台ID） | string | R19.1 起且 “LIS Enhanced Protocol” 启用才发（22339–22340） | `C0001` |

#### 9.2.4 S004 自动化告警通知（8.4，22346–22382）

`COMMENT S004^<Sample-ID>\<Warning-Code>\<Node-ID>\<Error-Description>\<DateTime>\<Duplicate-Tube-Number>\<Automation-Error-Code>^S`

检测到需通知 Host LIS 的**异常（Exception）**时发（22364）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID；R19.1 Enhanced Protocol 下含 `Unreadable#nnnn`/`Invalid-SID#nnnn`（22365–22369） | `S00123` |
| 1 | Warning-Code（告警码） | string | Error-Handling.ini 中的异常码（22370） | `SC022` |
| 2 | Node-ID（节点ID） | number | 异常所在 Node（格式串 22346；字段描述未单独列出，**(手册未明示)** 取值范围） | `5` |
| 3 | Error-Description（告警描述） | string | Error-Handling.ini 中的异常描述（22371） | `Unknown Tube Type` |
| 4 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22372） | `20260915212516` |
| 5 | Duplicate-Tube-Number（复管号） | number | Hotel 3 起且配置启用才发；0=原管（22373–22375） | `0` |
| 6 | Automation-Error-Code（自动化错误码） | string | India 3 起；仅 “LIS S004 Field for Error Codes” 启用才发；与前一相同码连发时尾随 `\1`（22376–22382） | `SC010` |

#### 9.2.5 S005 样本处理通知（8.5，22400–22534）

`COMMENT S005^<Sample-ID>\<Event>\<DateTime>\<Duplicate-Tube-Number>\<Node-ID>\<Carrier-ID>\<Shaking-Expiration-Time>\<Number-Of-Sealings>\<Router-Expiration-Timestamp>^S`

样本在自动化上**完成某处理步骤**时发（22403）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID；R19.1 Enhanced Protocol 下含 `Unreadable#nnnn`/`Invalid-SID#nnnn`（22422–22426） | `S00123` |
| 1 | Event（事件） | 枚举 | 见 §9.2.5.1（22427–22483） | `CENTRIFUGED` |
| 2 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22484） | `20260915212516` |
| 3 | Duplicate-Tube-Number（复管号） | number | Hotel 3 起且配置启用才发；0=原管（22485–22488） | `0` |
| 4 | Node-ID（节点ID） | number | Oscar 1 起且 “LIS Include Node ID” in S005 启用才发；触发事件的模块（22489–22491） | `3` |
| 5 | Carrier-ID（载物台ID） | string | R19.1 起且 Enhanced Protocol+Include Node ID 均启用才发；无载物台（REMOVED/DISPOSED）为空串（22492–22496） | `C0001`/（空） |
| 6 | Shaking-Expiration-Time（振摇超期时间） | string | R19.1 起、条件同上、仅 Shaker 模块；`YYYYMMDDHHMMSS`（22497–22501） | `20260915220000` |
| 7 | Number-Of-Sealings（封口次数） | number | R19.1 起、条件同上、仅 Sealer 模块（22502–22505） | `2` |
| 8 | Router-Expiration-Timestamp（路由超期时间） | string | R19.1（VTX R24）起、条件同上、仅 VTM/OUM/VTX 模块；`YYYYMMDDHHMMSS`（22506–22511） | `20260915230000` |

**Brief 变体**（22531–22534）：FlexLab 将 S005 配置为 “Brief” 时，`REMOVED` 事件可一次多发：`COMMENT S005^<Sample-ID1>;…;<Sample-IDn>\REMOVED\<DateTime>^S`（此变体索引 0=分号分隔的多 SID 列表，1=REMOVED，2=DateTime）。

**9.2.5.1 Event 取值**（22427–22483）：`CENTRIFUGED`(已离心)、`DECAPPED`(已开盖)、`SEALED`(已封口)、`UNSEALED`(已开封)、`RECAPPED`(已复盖)、`ALIQUOTED`(已分装)、`REMOVED`(已移出自动化)、`DISPOSED`(已从 Storage/BOM/MOM 丢弃)、`SHAKEN`(Shaker 振摇)、`TRANSFERRED`(经 VTM/OUM/VTX 转至另一系统)、`IN-BUFFER`/`OUT-BUFFER`(WBB 缓冲进出，R19.1+Enhanced)、`IN-ROUTER`/`OUT-ROUTER`(进入/离开 VTM/OUM/VTX，R19.1/VTX R24)、`MIXED`(Mixer 混合，R23.1 起)。

#### 9.2.6 S006 样本路由通知（8.6，22537–22546）

`COMMENT S006^<Sample-ID>\<Node-ID>\<DateTime>^S`；“LIS Enhanced S006 Comment” 启用时追加第 4 字段 `\<List-of-Tests-separated-by-semicolons>`（22545，Kilo 3 起）。

样本被**路由到 Analyzer** 时发（22538）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（22539） | `S00123` |
| 1 | Node-ID（Analyzer ID） | number | 样本被偏转到的 Analyzer（22540） | `7` |
| 2 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22541） | `20260915212516` |
| 3 | List-of-Tests（测试列表，可选） | string；`;` 分隔 | 仅 Enhanced S006 启用时发；`<Test1>;…;<Testn>`（22542–22546） | `CBC;DIF` |

#### 9.2.7 S007 优先级升级通知（8.7，22554–22598）

`COMMENT S007^<Sample-ID>\<DateTime>\<New-Priority>^S`；`U` 时追加测试列表：`COMMENT S007^<Sample-ID>\<DateTime>\U\<List-of-Tests>^S`（22555）。

样本优先级升为 ASAP（R20 起）/ STAT，或手动（S010）/ DCM 延迟开盖自动升降级（R21.1 起）时发（22556–22559）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（22560） | `S00123` |
| 1 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（22561） | `20260915212516` |
| 2 | New-Priority（新优先级） | 枚举 | `A`(ASAP, R20 起)/`S`(STAT, R20 起)/`U`(开盖后 DCM 自动升 STAT, R21.1 起)/`D`(DCM 完成后降回原优先级, R21.1 起)（22562–22588） | `S`/`A`/`U`/`D` |
| 3 | List-of-Tests（测试列表，可选） | string；`;` 分隔 | 仅 `<New-Priority>=U` 时发，R21.1 起（22589–22591） | `CO2;TCO2` |

> Brief 模式（R20 起，“LIS S007 Comment”=Brief）：仅升级 STAT 时发，且**不发** `<New-Priority>` 字段（22597–22598）。

#### 9.2.8 S009 样本状态通知（8.8，22603–22651）

`COMMENT S009^<Sample-ID>\<Tube-Type>\<Cap-Type>\<Weight>\<Spinning>\<Seal>\<#Seals>\<Status>\<Analyzers>\<Location>\<Error>\<Duplicate-Tube-Number>\<Internal-Diameter>^S`

**S008（样本状态请求）的应答**（§3.2）；为 SAMPLE-INFO 的简版（22605–22606）。信息缺失时可仅发 `COMMENT S009^<Sample-ID>`（22641–22642）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID；R19.1 Enhanced 下含 `Unreadable#nnnn`/`Invalid-SID#nnnn`（22607–22611） | `S00123` |
| 1 | Tube-Type（管类型） | number | 0、3..14、16..19（APPENDIX M）（22612） | `3` |
| 2 | Cap-Type（管盖类型） | enum | 0/N/C/1..999（22613–22614） | `C` |
| 3 | Weight（管重量） | number | 毫克；未知为 0（22615） | `12` |
| 4 | Spinning（是否离心） | enum | 0/1（22616） | `1` |
| 5 | Seal（是否封口） | enum | 0(未封口)/1(已封口)（22617） | `1` |
| 6 | #Seals（封口次数） | number | 已封口次数（22618） | `2` |
| 7 | Status（样本状态） | 枚举 | `U`(unknown)/`E`(Expected)/`I`(Incomplete)/`C`(Complete)/`D`(Deferred, Oscar 2 起)（22619–22620） | `I` |
| 8 | Analyzers（送达的 Analyzer 列表） | string；**空格分隔** | 样本被送达的 Analyzer Node ID 列表（22621–22622） | `7 12` |
| 9 | Location（位置） | string | 格式 `a-bb-cccccc-dd-ee-ff`（见 SAMPLE-INFO p28，§9.4）（22623） | `1-15-00002-01-02-12` |
| 10 | Error（样本错误码） | string | 样本错误码（22624） | `0000` |
| 11 | Duplicate-Tube-Number（复管号） | number（1–15） | Quebec 1 起且配置启用才发；缺省=原管 0（22643–22648） | `0` |
| 12 | Internal-Diameter（管内径） | number | R19.1 起且 “LIS Additional Field for Duplicate Tubes”+“LIS Send Internal Tube Diameter” 启用才发（22649–22651） | `13` |

#### 9.2.9 S013 分装完成通知（8.9，22657–22770）

`COMMENT S013^<Sample-ID>\<Node-ID>\<Primary-Tube-Type>\<Patient-Name>\<Birth-Date>\<Patient-Gender>\<Patient-ID>\<Secondary-Tube-ID1>;<Microliters1>;<Destination1>;<Test-Codes1>\…\<Secondary-Tube-IDn>;<Microlitersn>;<Destinationn>;<Test-Codesn>\<Aliquoting-Test1>\…\<Aliquoting-Testm>^S`

样本被**分装**时发（22661）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（主管ID） | string | 主管 Sample ID（22662） | `S00123` |
| 1 | Node-ID（分装模块ID） | number | Aliquoter 模块 ID（22663） | `9` |
| 2 | Primary-Tube-Type（主管类型） | number | 0、3..11（APPENDIX M）（22664） | `3` |
| 3 | Patient-Name（患者姓名） | string | 打印在分装管标签上的患者信息；“Aliquoter Print Patient ID on Label”=No 时为空（22665–22667） | `Doe^John` |
| 4 | Birth-Date（日期） | string | 依 “Aliquoter Print Date on Label”=Birth/Draw/Both/No 而定（22668–22671） | `19800101` |
| 5 | Patient-Gender（性别） | string | “Aliquoter Print Patient Gender on Label”=Yes 时含性别；其后按 LIS.ini 配置还可尾随 Sample Priority / Sample Material 子分量（以空格分隔）（22672–22675） | `M` |
| 6 | Patient-ID（患者ID） | string | 患者 ID（22666） | `P001` |
| 7… | Secondary-Tube-i（分装管，可变） | string；组内 `;` 分隔 | 每根分装管一组 `<SID>;<μl>;<Destination>;<Test-Codes>`，以 `\` 连接（22658–22660） | `S00123A;150;DSX;CBC` |
| 末尾 | Aliquoting-Test-1…m（分装测试，可变） | string | 分装测试列表，以 `\` 连接（22659–22660） | `ALIQUOT` |

> ⚠️ 本消息**字段数可变**（分装管数 n、分装测试数 m 不定），解析须按 `\` 分组动态处理（**(手册未明示)** 固定上限）。
>
> 其它变体：① LIS.ini “LIS S013 Comment”=Brief（R19.1 起）时，省略尾部 `<Aliquoting-Test1>…m` 组（同 R19 及以前格式，22703–22707）；② 使用 **Extended Aliquot Label Template**（GWC 板不可用）时，每根分装管组扩展为 16 个子分量 `<Secondary-Tube-ID>;<Microliters>;<Test-Codes>;<Dept>;<DC>;<Worklist>;<Test-Name>;<Priority>;<Temp>;<SpecRecd>;<SpecNbr>;<AliqInd>;<Rack>;<SpecCond>;<PSite>`（格式串 22710–22715，字段说明 22716–22761），并有对应 Brief 变体（22762–22769）。

#### 9.2.10 S014 管称重通知（8.60，24926–24930）

`COMMENT S014^<Sample-ID>\<Node-ID>\<Tube-Weight>^S`（Golf 2 起）

管被 **Centrifuge 模块称重**时发（24927）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（主管ID） | string | 主管 Sample ID（24928） | `S00123` |
| 1 | Node-ID（离心机ID） | number | Centrifuge 模块 ID（24929） | `4` |
| 2 | Tube-Weight（管重量） | number | 毫克（24930） | `45` |

#### 9.2.11 S015 血清体积通知（8.64，25011–25033）

`COMMENT S015^<Sample-ID>\<Node-ID>\<Tube-Type>\<Serum-Volume>\<Sample-Level>^S`（Hotel 3 起）。

样本体积被 **SVD 模块**测得时发（25012）。SIM 模块（R18.1 起）改为 `<Serum-Volume>\<High-Level>\<Low-Level>`（25023–25026）；R24.1 起接收 SIM 的 SAMPLE-VOLUME 时再追加 `<spun/unspun>`（25028–25033）。

> ⚠️ 手册原文 SIM/R24.1 格式串在尾 `^S` 前多一个 `|`（`…\<Low-Sample-Level>|^S`，25023、25030），与基本形式不一致，疑为手册笔误；解析建议对此容错（**(手册未明示)** 实际是否发送该 `|`）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（主管ID） | string | 主管 Sample ID（25014） | `S00123` |
| 1 | Node-ID（测体积模块ID） | number | Volume Detector 模块 ID（25015） | `6` |
| 2 | Tube-Type（管类型） | number | 0..14、16..19（APPENDIX M）（25016） | `3` |
| 3 | Serum-Volume（血清体积） | number | 微升 μl（25017） | `500` |
| 4 | Sample-Level / High-Sample-Level（样本水平） | number | 基本形=decimillimeter（25018–25019）；SIM 形=High Level（25025） | `12` |
| 5 | Low-Sample-Level（低样本水平，SIM 形） | number | decimillimeter，仅 SIM（R18.1 起）（25026） | `8` |
| 6 | spun/unspun（离心状态，R24.1） | enum | 空 / `U`(unspun) / `S`(spun)，仅 SIM SAMPLE-VOLUME（25030–25033） | `S` |

#### 9.2.12 L002 样本重载通知（8.66，25072–25101）

`COMMENT L002 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\<Input-Lane>\<Carrier-ID>\<Internal-Diameter>^L`（**ID 后为空格**；India 2 起）

字段结构与 **L001（§9.2.1）完全一致**（25072–25101 逐字段对应 22222–22281）；触发条件为“再次载入且 ‘Use L002 and L003 Message’ 启用”（25074–25076）。索引 0–9 同 L001。

#### 9.2.13 L003 样本重识别通知（8.67，25124–25149）

`COMMENT L003 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\\<Carrier-ID>^L`（**ID 后为空格**；India 2 起；`\\` 中间留空槽位）

样本**重识别确认**时发（‘Use L002 and L003 Message’ 启用，25126–25128）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（25129） | `S00123` |
| 1 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（25130） | `20260915212516` |
| 2 | Tube-Type（管类型） | number | 0、3..14、16..19（25131） | `3` |
| 3 | Cap-Type（管盖类型） | enum | 0/N/C/1..999（25132–25135） | `C` |
| 4 | Spinning（是否离心） | enum | 0/1（25136） | `1` |
| 5 | Input-Module/Node-ID（接收节点） | number | 接收样本的节点（25137） | `1` |
| 6 | Duplicate-Tube-Number（复管号） | number | Hotel 3 起且配置启用才发；0=原管（25138–25140） | `0` |
| 7 | （空槽位） | — | 格式串中 `\\` 保留的空槽位（25125） | （空） |
| 8 | Carrier-ID（载物台ID） | string | R19.1 起且 Enhanced+Include Lane 启用才发（25141–25146） | `C0001` |

> ⚠️ 格式串含 `<Internal-Diameter>` 描述（25147–25149）但**未出现在 25125 的格式串字段列表中**（与 L001 同，见 §4.6.3 注），实际是否发送 **(手册未明示)**。

#### 9.2.14 S017 样本合并通知（8.72，25253–25261）

`COMMENT S017^<Primary-Sample-ID>\<Secondary-Sample-ID>\<Node-ID>\<Error-Code>^S`（Kilo 3 起）

Integrated Robotics Platform 执行**样本合并（Pooling）**或手动命令时发（25254–25256）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Primary-Sample-ID（主管ID） | string | 主管 Sample ID（25257） | `S00123` |
| 1 | Secondary-Sample-ID（次管ID） | string | 次管 Sample ID（25258） | `S00456` |
| 2 | Node-ID（模块ID） | number | 执行合并的 Integrated Robotics Platform（25259–25260） | `11` |
| 3 | Error-Code（错误码） | string | 合并操作错误码；无错 `0000`（25261） | `0000` |

#### 9.2.15 L004 出 Storage 通知（8.73，25265–25295）

`COMMENT L004 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\\<Carrier-ID>^L`（**ID 后为空格**；India 2 起；`\\` 留空槽位）

样本**离开 Storage / HVS 模块**时发（25267）。⚠️ `<Tube-Type>`、`<Cap-Type>`、`<Spinning>` **不发往 Host LIS**（25286，保留为空槽位）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID | `S00123` |
| 1 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS` | `20260915212516` |
| 2 | Tube-Type（管类型） | — | **不发送**（保留空槽位）（25286） | （空） |
| 3 | Cap-Type（管盖类型） | — | **不发送**（保留空槽位）（25286） | （空） |
| 4 | Spinning（是否离心） | — | **不发送**（保留空槽位）（25286） | （空） |
| 5 | Node-ID（节点ID） | number | 节点 ID | `15` |
| 6 | Duplicate-Tube-Number（复管号） | number | 恒为 0，仅配置启用才发（25287–25289） | `0` |
| 7 | （空槽位） | — | `\\` 保留空槽位（25266） | （空） |
| 8 | Carrier-ID（载物台ID） | string | R19.1 起且 Enhanced+Include Lane 启用才发（25290–25295） | `C0001` |

#### 9.2.16 L005 分装管贴标通知（8.74，25298–25320）

`COMMENT L005 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\\<Carrier-ID>^L`（**ID 后为空格**；November 2 起；`\\` 留空槽位）

Aliquoter 模块**生成并贴标**分装管时发（作为 L001 的替代，需 LIS.ini “LIS L005 Comment” 启用，25301–25302）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（25303） | `S00123A` |
| 1 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（25304） | `20260915212516` |
| 2 | Tube-Type（管类型） | number | 0、3..11（APPENDIX M）（25305） | `3` |
| 3 | Cap-Type（管盖类型） | enum | 0/N/C/1..999 或 Nemo 分装后缀（25306–25309） | `C` |
| 4 | Spinning（是否离心） | enum | 0/1（25310） | `0` |
| 5 | Input-Module/Node-ID（接收节点） | number | 接收样本的节点（25311） | `9` |
| 6 | Duplicate-Tube-Number（复管号） | number | 仅配置启用才发；0=原管（25312–25314） | `0` |
| 7 | （空槽位） | — | `\\` 保留空槽位（25299） | （空） |
| 8 | Carrier-ID（载物台ID） | string | R19.1 起且 Enhanced+Include Lane 启用才发（25315–25320） | `C0001` |

#### 9.2.17 R001 架事件通知（8.75，25323–25348）

`COMMENT R001 <Rack-ID>\<Node-ID>\<Rack-Event>\<DateTime>^R`（**ID 后为空格**；November 2 起）

HVS 或 ROM-400 模块发生**架事件**时发（25341–25342）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Rack-ID（架ID） | string | 架 ID（25343） | `R01` |
| 1 | Node-ID（节点ID） | number | High Volume Storage 的 ID（25344） | `15` |
| 2 | Rack-Event（架事件） | 枚举 | `DISPOSED`(架内全部管已丢弃，仅 HVS)/`REMOVED`(架被移出，仅 ROM-400)（25345–25347） | `REMOVED` |
| 3 | DateTime（时间戳） | string | 军时 `YYYYMMDDHHMMSS`（25348） | `20260915212516` |

#### 9.2.18 L006 经 VTM 到达通知（8.76，25354–25397）

`COMMENT L006 <Sample-ID>\<DateTime>\<Tube-Type>\<Cap-Type>\<Spinning>\<Node-ID>\<Duplicate-Tube-Number>\\<Carrier-ID>\<Internal-Diameter>^L`（**ID 后为空格**；Oscar 1 起；`\\` 留空槽位）

样本管**经 VTM / OUM / VTX 从相连自动化系统到达**时发（25357–25359）。字段语义与 L001（§9.2.1）对应（0=Sample-ID、1=DateTime、2=Tube-Type、3=Cap-Type、4=Spinning、5=Node-ID、6=Duplicate-Tube-Number、7=空槽位、8=Carrier-ID、9=Internal-Diameter，详见 25360–25397 各行）。

#### 9.2.19 S021 排序测试状态变更（Scheduled→Sorted）（8.85，25590–25601）

`COMMENT S021^<Sample-ID>\<Node-ID>\<Dest-Duplicate#>\<Test-1>\…\<Test-n>^S`（R17.1 起）

某样本管的**排序测试（Sorting Tests）**状态由 Scheduled 变为 Sorted（管从 IOM/ROM/ROM-400/BOM 移出，或整管移入 Storage/HVS 架）时发往 DMS（25592–25596）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（25597） | `S00123` |
| 1 | Node-ID（节点ID） | number | Node ID（25598） | `5` |
| 2 | Dest-Duplicate#（目标复管号） | number（0–15） | 0 或空=原管；有 ≥1 排序测试的复管（25599–25600） | `0` |
| 3… | Test-1…Test-n（排序测试，可变） | string | 关联该管的分号？——**以 `\` 分隔**（格式串 25590）的排序测试（25601） | `STORE-A` |

> 排序测试（Test-i）间以 `\` 连接（25590），非 `;`；**(手册未明示)** 是否亦可用 `;`。

#### 9.2.20 S022 样本指数通知（HIL）（8.88，25652–25659）

`COMMENT S022^<Sample-ID>\<Node-ID>\<H-index>\<I-index>\<L-index>^S`（R18.1 起）

**Sample Integrity (SIM) 模块**测得 HIL 指数时发（25653–25654）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（主管ID） | string | 主管 Sample ID（25655） | `S00123` |
| 1 | Node-ID（模块ID） | number | Volume Detector 模块 ID（25656） | `6` |
| 2 | H-index（溶血指数） | number | Hemolysis Index（25657） | `2.1` |
| 3 | I-index（黄疸指数） | number | Icterus Index（25658） | `0.5` |
| 4 | L-index（脂血指数） | number | Lipemia Index（25659） | `1.3` |

#### 9.2.21 S024 测试状态响应（8.89，25683–25699）

`COMMENT S024^<Sample-ID>\<Duplicate-Tube-Number>\<ASTM-Test-Code1>;<Status1>\…\<ASTM-Test-Coden>;<Statusn>^S`（R19.1 起）

**S023（测试状态请求，手册第 9 章 9.86，28295–28300）的应答**；返回该样本管的测试列表及当前状态（25685；28298–28300）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | S023 请求的样本 ID（25686） | `S00123` |
| 1 | Duplicate-Tube-Number（复管号） | number | S023 请求的复管号（25687） | `0` |
| 2… | ASTM-Test-Codei;Statusi（测试;状态，可变） | string；组内 `;` 分隔 | 第 i 个测试的 ASTM 码 + 状态，以 `\` 连接各组（25689–25690） | `CBC;5` |

**Statusi 取值**（25690–25699）：`0`=To Do、`1`=Scheduled、`2`=Sampled、`3`=Processed、`4`=Sorted、`5`=Completed、`6`=Aliquoted、`7`=Canceled、`8`=Deferred。

> ⚠️ **注意**：S024 仅回**测试状态**，**不含结果数值**（结果数值经 Analyzer→DMS 的 R 帧，见 §9.0）。

#### 9.2.22 VTM-CARRIER-ARRIVES（8.77，25407–25417）

`VTM-CARRIER-ARRIVES <VTM-Instance>|<Carrier-ID>|<Sample-ID>`（Oscar 1 起；经 LIS3 通道发往**相连自动化系统**）

载物台经 VTM 送出时通知对方系统（25409–25410）；对方若因不活动自动停止会据此自动启动（25416–25417）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|--------|--------|------|------|---------------------|
| 0 | VTM-Instance（VTM实例） | number | 两端 VTM 必须相同的实例号（25411–25412） | `1` |
| 1 | Carrier-ID（载物台ID） | string | 载物台 ID（25413） | `C0001` |
| 2 | Sample-ID（样本ID） | string | 满载物台的样本 ID；空载物台为空串（25414–25415） | `S00123`/（空） |

#### 9.2.23 VTM-OVERLOAD（8.78，25431–25463）

`VTM-OVERLOAD <Status>`（Oscar 1 起；经 LIS3 通道，每分钟或收到 SYSTEM START/PAUSE 后发往相连系统，25433–25435）

告知对方系统当前是否可通过 VTM 送载物台（25434–25435）。

| 参数索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|--------|--------|------|------|---------------------|
| 0 | Status（过载状态） | 枚举 | 见 §9.2.23.1（25436–25463） | `OFF` |

**9.2.23.1 Status 取值**（25453–25463）：
- `TOTAL`：载物台总数超 “Maximum Carriers on Track” 阈值，或系统处于手动 Paused——对方系统禁止送**任何**载物台并关闭 VTM divert/load gate（25453–25457）。
- `EMPTY`：空载物台数超 “Maximum Empty Carriers on Track” 阈值——对方系统禁止送**空**载物台（25458–25460）。
- `OFF`：两项阈值均未超——对方系统可送**任意**载物台（25461–25463）。

#### 9.2.24 S025 样本状态变更通知（8.91，26036–26046）

`COMMENT S025^<Sample-ID>\<Sample-Status>\<Duplicate-Tube-Number>^S`（R19.1 起）

样本状态在 **Incomplete(I)/Complete(C)/Deferred(D)** 之间**自动变更**时发往 DMS/Host LIS；`Expected I → Complete I` 也发；**`Expected I → Incomplete` 不发**（26037–26039）。需 LIS.ini “**LIS Enhanced Protocol**”=Enabled：Enabled 时 S025 发往 DMS，Disabled 时不发（6324–6349）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（26040） | `S00123` |
| 1 | Sample-Status（样本状态） | 枚举 | `I`=Incomplete、`C`=Complete、`D`=Deferred（26041–26044） | `C` |
| 2 | Duplicate-Tube-Number（复管号） | number | 0=原管；>0=第 i 复管（26045–26046） | `0` |

#### 9.2.25 S026 样本测试状态变更（M-HTC）（8.97，26268–26298）

`COMMENT S026^<Sample-ID>\<Node-ID>\<DateTime>\<Duplicate-Tube-Number>\<ASTM-TEST1>;..<ASTM-TESTn>\<Carrier-ID>^S`（R22 起）

样本管**从 Hamilton Turnstile Connection (M-HTC) 模块返回**时，发往 DMS/Host LIS，附 FlexLab 推测在该模块执行的 ASTM 测试码列表（26270–26272）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Sample-ID（样本ID） | string | 样本 ID（26273） | `S00123` |
| 1 | Node-ID（节点ID） | number | Node ID（26274） | `12` |
| 2 | DateTime（时间戳） | string | `YYYYMMDDHHMMSS`（26275） | `20260915212516` |
| 3 | Duplicate-Tube-Number（复管号） | number | Hotel 3 起且配置启用才发；0=原管（26294–26296） | `0` |
| 4 | ASTM-TEST1;…;TESTn（已完成测试列表） | string；`;` 分隔 | 推测在 M-HTC 执行的 ASTM 测试码（26297） | `CBC;DIF` |
| 5 | Carrier-ID（载物台ID） | string | 仅 “LIS Enhanced Protocol” 启用才发（26298） | `C0001` |

### 9.3 仪器 / 配置响应通知（I0xx，发往 Host LIS，含字段位置表）

> I 系列：`COMMENT I0xx^<F0>\…\<Fn>^I`（ID 后接 `^`，尾 `^I` 不占索引）。I010/I012/I014/I016/I018/I020/I028 用 `<Comment-Fields>`——即若干以 `\` 分隔的“组”，组内以 `;` 分隔。

#### 9.3.1 I002 仪器启用通知（8.10，22772–22776）

`COMMENT I002^<Node-ID>^<Notification>^I`（**字段间用 `^` 而非 `\`**）

Analyzer 被 GUI 设为 On-line/Off-line 时发（22773）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（22774） | `7` |
| 1 | Notification（通知） | 枚举 | `ENABLED`/`DISABLED`（22775–22776） | `ENABLED` |

#### 9.3.2 I004 试剂库存通知（8.11，22782–22810）

`COMMENT I004^<Node-ID>\<Node-Type>\<Test1>;<Reagent-Level1>\…\<Testn>;<Reagent-Leveln>^I`；R22 起且发往 DMS 且 “LIS Enhanced I004 Comment” 启用时每测试组追加 `;<Module-IDi>`（22783–22784、22807–22810）。

应 I003 请求或按配置自动通知 Analyzer 库存（22785–22786）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（22787） | `7` |
| 1 | Node-Type（Analyzer 类型） | string | Analyzer Node Type（22788） | `LXL` |
| 2… | Testi;Reagent-Leveli(;Module-IDi)（可变） | string；组内 `;` 分隔 | 第 i 个测试的 ASTM 码 + 试剂水平（+可选模块 ID）（22789–22810） | `CBC;45` |

#### 9.3.3 I006 测试面板通知（8.12，22813–22818）

`COMMENT I006^<Node-ID>\<Node-Type>\<Test1>\…\<Testn>^I`

应 I005 请求通知 Analyzer 测试面板（启用的测试）（22814）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（22816） | `7` |
| 1 | Node-Type（Analyzer 类型） | string | Analyzer Node Type（22817） | `LXL` |
| 2… | Testi（测试码，可变） | string | 第 i 个启用测试的 ASTM 码（22818） | `CBC` |

#### 9.3.4 I007 测试启用/禁用通知（8.13，22821–22826）

`COMMENT I007^<Node-ID>\<Test>\<Notification>^I`

测试被 GUI 启用/禁用时发（22822）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（22823） | `7` |
| 1 | Test（测试码） | string | ASTM 码（LIS）/测试码（GUI）（22824） | `CBC` |
| 2 | Notification（通知） | 枚举 | `ENABLED`/`DISABLED`（22825–22826） | `DISABLED` |

#### 9.3.5 I010 测试映射通知（8.14，22829–22867）

`COMMENT I010^<Comment-Fields>^I`，其中 Comment-Fields = `<Node-ID>\<Node-Type>\<ASTM-Test-Code1>;<Automation-Test-Code1>;<Test-Description1>;<Status1>;<Reagent-Level1>\…`（22833–22835）。

应 I009 请求发往 Host LIS/GUI（22830）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（22836） | `12` |
| 1 | Node-Type（Analyzer 类型） | string | 2/3 字符 Analyzer Node Type（22837） | `LIA` |
| 2… | Test 组（可变，组内 `;`） | string | `<ASTM-Code>;<Automation-Code>;<Description>;<Status>;<Reagent-Level>`（22838–22862） | `25VITD;25-D;VITAMINA D;1;30` |

> 示例（22864–22867）：`COMMENT I010^12\LIA\25VITD;25-D;VITAMINA D;1;30\ACTHDS;ACTH;ACTH;1;45^I`。

#### 9.3.6 I012 测试信息通知（8.15，22874–22916）

`COMMENT I012^<Comment-Fields>^I`，Comment-Fields = `<ASTM-Test-Code1>;<Automation-Test-Code1>;<Test-Description1>;<Analyzer-Type1>;<Disposing-Time1>\…`（22878–22880）。

应 I011 请求发往 Host LIS（22875）。

| 索引 | 字段名（组内 `;` 子分量） | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| i | ASTM-Test-Codei | string | 第 i 个测试 ASTM 码（22881–22882） | `BL10` |
| i | Automation-Test-Codei | string | 自动化测试码（22883） | `BL10` |
| i | Test-Descriptioni | string | 测试描述（22884） | `Leucocyten` |
| i | Analyzer-Typei / Instrument-Typei | string | 2/3 字符 Analyzer Node Type（22885–22886） | `A21` |
| i | Disposing-Timei | string | 该测试样本在 Storage 的最短保留时间（`n Workdays`/`n Days`/`n Hours`/`n Minutes`）（22887–22896） | `7 Days` |

> 示例（22915–22916）：`COMMENT I012^BL10;BL10;Leucocyten;A21;7 Days\BT12;BT12;Trombocyten;A21;5 Hours\…^I`。

#### 9.3.7 I014 排序测试通知（8.16，22988–23156）

`COMMENT I014^<Comment-Fields>^I`，Comment-Fields = `<ASTM-Test-Code1>;<Automation-Test-Code1>;<Test-Description1>;<Sorting-Type1>;<Tube-Processing-Type1>\…`（22991–22993）。

应 I013（Sorting Tests Request）请求发往 Host LIS（22989）。

| 索引 | 字段名（组内 `;` 子分量） | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| i | ASTM-Test-Codei | string | ASTM 码；错误排序用 `#`+错误码（如 `#SC00C`），复管排序用 `!`+分装管型（如 `!A`）（22994–23012） | `STORE-A`/`#SC00C` |
| i | Automation-Test-Codei | string | 自动化测试码（23013） | `STORE-A` |
| i | Test-Descriptioni | string | 测试描述（23014） | `Store A` |
| i | Sorting-Typei | string | 排序目标，空格分隔（IOM lane `n`/`nx`、`m-n`、Storage、HVS 等，详见 23015–23145） | `5`/`STORE-A` |
| i | Tube-Processing-Typei | string | 管处理类型（详见 23015 起） | `SC` |

> Sorting-Type 具体取值表较长（23015–23145），此处仅列结构；**(手册未明示)** 全部枚举请查 23015 起原文。

#### 9.3.8 I016 分装测试通知（8.17，23158–23177）

`COMMENT I016^<Comment-Fields>^I`，Comment-Fields = `<ASTM-Test-Code1>;<Automation-Test-Code1>;<Test-Description1>;<Secondary-Tube-Type1>;<μl1>\…`（23161–23163）。

应 I015（Aliquoting Tests Request）请求发往 Host LIS（23159）。

| 索引 | 字段名（组内 `;` 子分量） | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| i | ASTM-Test-Codei | string | ASTM 码（23164–23165） | `LPFGN1` |
| i | Automation-Test-Codei | string | 自动化测试码（23166） | `IGGG-LPF-AS` |
| i | Test-Descriptioni | string | 测试描述（23167） | `IGG-LPF AS` |
| i | Secondary-Tube-Typei | string | 次管类型（23168–23169） | `A` |
| i | μli（分装体积） | number | 该测试分装的微升数（23170） | `150` |

#### 9.3.9 I018 分装管配置通知（8.18，23180–23229）

`COMMENT I018^<Comment-Fields>^I`。Hotel 1 及以前：`<Aliquot-Tube1>;<Suffix1>;<Suffix-Depth1>;<Dead-Volume1>;<Destination1>\…`（23200–23202）；Hotel 2 起：第 2 子分量改为 `<Prefix/Suffix>`（23211–23213）。

应 I017 请求发往 Host LIS（23198）。

| 索引 | 字段名（组内 `;` 子分量） | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| i | Aliquot-Tubei | string | 分装管类型 I（23203/23214） | `A` |
| i | Suffix / Prefix/Suffix | string | 条码后缀（Hotel 2 起可为前缀：`[&P]`+空格+前缀字符串；复管为空串）（23204–23205/23215–23217） | `54`/（空） |
| i | Suffix-Depth / Prefix/Suffix-Depth | number | 需忽略的原条码位数（复管为 0）（23206–23207/23218–23219） | `2` |
| i | Dead-Volume | number | 次管死体积（微升）（23208/23220） | `0` |
| i | Destination | string | 次管标签上的目标实验室（23209/23221） | `AMFI` |

#### 9.3.10 I020 默认测试订单通知（8.19，23253–23266）

`COMMENT I020^<Comment-Fields>^I`，Comment-Fields = `<Cap-Type1>;<Cap-Color1>;<Tests1>\…\<Cap-Typen>;<Cap-Colorn>;<Testsn>`（23256）。

应 I019 请求发往 Host LIS（23254）。

| 索引 | 字段名（组内 `;` 子分量） | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| i | Cap-Typei | string | 盖类型（数字）或 `N`(未盖)/`C`(通用盖)/`#x`(分装后缀)（23257 起） | `01`/`N` |
| i | Cap-Colori | string | 盖颜色（23257 起） | `Yellow green` |
| i | Testsi | string | 该盖对应自动生成的默认测试（23257 起） | `SORT-D` |

> 示例（23263–23264）：`COMMENT I020^01;Yellow green;SORT-D\02;Old gold;SORT-C\…^I`。

#### 9.3.11 I008 试剂耗尽/加载通知（8.62，24968–24975）

`COMMENT I008^<Node-ID>\<Test>\<Notification>^I`

测试试剂库存**由 >0 变 0**（`DISABLED`）或**由 0 变 >0**（`ENABLED`）时发往 Host LIS（24969–24971）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0 | Node-ID（Analyzer ID） | number | Analyzer Node ID（24972） | `7` |
| 1 | Test（测试码） | string | ASTM 码（LIS）/测试码（GUI）（24973） | `CBC` |
| 2 | Notification（通知） | 枚举 | `DISABLED`（耗尽）/`ENABLED`（加载）（24974–24975） | `DISABLED` |

#### 9.3.12 I028 Analyzer 测试码通知（8.71，25233–25249）

`COMMENT I028^<Comment-Fields>^I`，Comment-Fields = `<Analyzer-Type1>\<Analyzer-Test-Code1>\<ASTM-Test-Code1>\<Test-Description1>\<Order-Despite-Zero-Inventory1>\<Combi-Tests1>\…`（25236–25239）。

应 I027（§3.2）请求发往 Host LIS（25234）。

| 索引 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| 0…（每 Analyzer-Type 组，`\` 分隔，组内 `\`→实为字段） | Analyzer-Typei | string | Analyzer 类型码（25240） | `LXL` |
| | Analyzer-Test-Codei | string | Analyzer 试剂库存消息返回的测试码（25241–25242） | `BL10` |
| | ASTM-Test-Codei | string | 用于 Host LIS 的 ASTM 码（25243） | `CBC` |
| | Test-Descriptioni | string | 助记测试描述（25244） | `Leucocyten` |
| | Order-Despite-Zero-Inventoryi | 枚举 | `Yes`/`No`——零库存时是否仍调度（25245–25247） | `Yes` |
| | Combi-Testsi | string | 可选 Combi 测试列表，**空格分隔**（25248–25249） | `CBC DIF` |

> ⚠️ 每个 Analyzer-Type 块的子字段与块之间**同为 `\` 分隔**（格式串 25236–25239）；因每块固定 **6 个子字段**，解析器可按“6 字段一组”确定块边界（手册未给出其它显式块边界标记）。

### 9.4 SAMPLE-INFO 样本全信息（8.23，23319–23442，GUI / DMS IUI）

`SAMPLE-INFO p01|…|p126`（`|` 分隔，共 **126** 个位置参数；由 GUI 用 SAMPLE-INFO-REQUEST 请求，23320–23322）。此为样本**最完整**信息源，S009 的 `<Location>` 即引用其 p28 语法（23371）。**仅发往 GUI/IUI**（非 Host LIS 通道）。关键字段：

| 位置 | 字段名 | 类型 | 说明 | 取值 / 示例（含行号） |
|-----|--------|------|------|---------------------|
| p01 | Sample-ID | string | 样本 ID（23323） | `S00123` |
| p02 | Patient | `PA-PID^LAB-PID^Last^First^MI` | 患者（23324） | `^^Doe^John^` |
| p03 | Sample-Type | 枚举/字符串 | `S,C,P,U,W,A,O` 或其它（23325） | `S` |
| p04 | Sample-Priority | 枚举 | `S`/`A`/`R`（Dream 可改）（23326） | `R` |
| p05 | Sample-Dilution | number | 稀释倍数（23328） | `1` |
| p06 | Sample-Collection | string | `YYYYMMDDHHMMSS`（23329） | `20260915212516` |
| p07 | Patient-Birth-Date | string | `YYYYMMDD`（23330） | `19800101` |
| p08 | Patient-Gender | 枚举 | `F`/`M`/`U`（23331） | `M` |
| p09 | Patient-Race | string | 种族（23332） | — |
| p10 | Attending-Physician | `Code^Last^First^MI^Suffix` | 主治医生（23333） | — |
| p11 | Admission/Discharge Date | `YYYYMMDD^YYYYMMDD` | 入院^出院（23334） | — |
| p12 | Patient Location | `code^text` | 患者位置（23335） | — |
| p13 | Action-Code | 枚举 | `N`/`A`/`C`（23336） | `N` |
| p14 | Sample-Source | `Centri^TubeType^AutoQC` | 见 §2.3（23337） | — |
| p15 | Ordering-Physician | `Code^Last^First^MI^Suffix` | 开单医生（23338） | — |
| p16 | Tube-Status | 枚举 | `E`(Expected)/`I`(Incomplete)/`C`(Complete)/`U`(Unknown)/`D`(Deferred)（23339–23340） | `I` |
| p17 | Tube-Input-Module | number | 最近进入的模块 ID（未知 0）（23341–23342） | `1` |
| p18 | Tube-Type | number | 0..14、16..19（APPENDIX M）（23343） | `3` |
| p19 | Tube-Spinning | enum | 0/1（23344） | `1` |
| p20 | Tube-Cap | enum | 0/N/C（23345） | `C` |
| p21 | Tube-Cap Type | number | 0/1..99（原文括注 “capped with cap type 1..999”，手册自相矛盾，待核实）（23363） | `15` |
| p22 | Tube-To-Spin | enum | 1/0（23364） | `1` |
| p23 | Tube-To-Decap | enum | 1/0（23365–23366） | `1` |
| p24 | Tube-Category | 枚举 | `P`(Primary)/`S`(Secondary)（23367） | `P` |
| p25 | Tube-Weight | number | 毫克（23368） | `45` |
| p26 | Tube-Sealed | enum | 0/1（23369） | `0` |
| p27 | Tube-Number-Of-Seals | number | 0..99（23370） | `0` |
| p28 | **Tube-Location** | string | `&a-bb-cccccc-dd-ee-ff`：a=0(off-line)/1(on-line)/2(removed)/3(disposed)；bb=Node ID(00=Track)；cccccc=Rack ID 或 Carrier ID；dd=floor/carrier queue；ee=lane/sub-queue；ff=position（23371–23381） | `&1-15-00002-01-02-12` |
| p29 | Tube-Error-Code | string | 样本错误码（23382） | `0000` |
| p30 | Tube-Previous-Error-Code | string | 前一个错误码（23383） | `0000` |
| p31 | Creation-Timestamp | string | 记录生成时间（23384–23385） | — |
| p32 | Update-Timestamp | string | 记录修改时间（23386–23387） | — |
| p33 | Store-Timestamp | string | 入 Storage 时间（23388–23389） | — |
| p34 | Storing-Time | number | 存储时间（分钟）（23390） | `120` |
| p35 | Flags | string | 标志位串（D/L/K/P/Q/H/R…/C/S/Zx/MR…/F/A/B 等，23391–23421） | `D` |
| p36 | Node ID | number | 管前位置 Node（23422） | `5` |
| p37 | Carrier ID | string | 管前位置载物台（23423） | `C0001` |
| p38 | Sample Volume | number | 微升（23424） | `500` |
| p39–p40 | Tube-Log | string | 管日志（23425） | — |
| p41–p120 | **Test-01…Test-40** | string | 每测试 `a-bb-ccc-d-s`：a=状态 0(TO DO)/1(SCHEDULED)/2(SAMPLED)/3(PROCESSED)/4(SORTED)/5(COMPLETED)/6(ALIQUOTED)/7(CANCELLED)/8(DEFERRED)；bb=Analyzer Node ID；ccc=Analyzer Type；d=Ordered By 0(LIS)/1(GUI)/2(Dream)/3(Instrument)；s=测试码（`&xx`=指定节点、`#yy`=排除节点）（23426–23438） | `1-07-LXL-0-CBC` |
| p125 | HIL Indices | string | 溶血;黄疸;脂血指数（SIM）（23439–23440） | `2.1;0.5;1.3` |
| p126 | Serum Levels | string | 血清高低水平（SIM）（23441–23442） | `12;8` |

> 注：p04–p15 与 ORDER（§2.1）字段一一对应；p16–p40 为自动化运行时状态；p41–p120 承载最多 40 个测试的完整状态。**此消息不直接发往 Host LIS**（GUI/IUI 请求），UniFlow 作 Host LIS 时通常不接收（**(手册未明示)** 是否有 DMS 变体）。
