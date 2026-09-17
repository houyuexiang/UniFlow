# UniFlow ↔ FlexLab(Dream) 数据库结构备查文档

> **来源**：Inpeco FlexLab 软件架构手册 `DCAU-H00.816.12.01.02`（FL-SA.62，790 页）。
> 主要章节：**3.3 Sample Database Structure**（含 3.3.1 / 3.3.2）、**3.4 Direct Access to DMS Database**、**3.5 Counter Folder**（手册页码 187–192）。
> 说明：本文件由手册 `pdftotext -layout` 文本（`/tmp/dcau.txt`，38102 行）核对整理而成，所有行号指该文本文件行号，便于回查原文。字段名、取值保留英文原样；手册未明示处标「(手册未明示)」并附行号。
> 关联文档：`APIO-PROTOCOL.md`（报文接口，含 8.23 SAMPLE-INFO 字段详解 §9.4、STATUS 消息 §4.2、CLOCK 消息 §4.3）。

---

## 1. t_sample 表（3.3，8345–8429，页码 187–188）

### 1.1 背景（8333–8347）

- 样本库为 **MySQL 数据库**，存放来自 LIS/Middleware 的样本记录与处理信息（FXSMS-592，8335–8337）。
- 专用表记录样本管未决事件（FXSMS-1370，8338–8339）；**tube_log 条目异步写入**（FXSMS-13383，8340–8341）。
- Dream 启动时若库/表不存在则自动创建（FXSMS-12881，8342–8343）。
- 「The Sample Database is structured a table called **t_sample**」（8345）；**R18 起新增 tube_log 表**存管日志（8346）。
- 手册只给出 **Max Length (chars)**，**未给出各字段 MySQL 数据类型 / 主键 / 建表 DDL**（(手册未明示)，8349–8429）。
- 各字段「Description (see Section 8.23)」——含义以 **8.23 SAMPLE-INFO**（23319–23442，即 APIO-PROTOCOL.md §9.4）为准，下表「说明」列已并入 8.23 详情。

### 1.2 字段表（共 127 个索引；41–120 为 test_1…test_80 共 80 列，合计 127 列）

| 索引 | 字段名 | 最大长度(chars) | 说明（8.23 行号） | 取值/示例 |
|-----|--------|------|------------------|-----------|
| 1 | sample_id | 24 | Sample-ID（23323） | `S00123` |
| 2 | patient | 60 | Patient：`PA-PID^LAB-PID^Last^First^MI`（23324） | `^^^Doe^John^` |
| 3 | s_type | 3 | Sample-Type（23325） | `S,C,P,U,W,A,O`（或其它字符串） |
| 4 | s_priority | 1 | Sample-Priority（23326） | `S`/`A`/`R` |
| 5 | s_diluition ⚠️ | 3 | Sample-Dilution（23328）（字段名手册原文即拼写错误，正确为 dilution） | 十进制数 |
| 6 | s_collection | 13 | Sample-Collection（23329） | `YYYYMMDDHHMMSS` |
| 7 | p_birth_date | 8 | Patient-Birth-Date（23330） | `YYYYMMDD` |
| 8 | p_gender | 1 | Patient-Gender（23331） | `F`/`M`/`U` |
| 9 | p_race | 9 | Patient-Race（23332） | string |
| 10 | att_physic | 40 | Attending-Physician：`Code^Last^First^MI^Suffix`（23333） | — |
| 11 | adm_disc_date | 17 | Admission/Discharge Date：`YYYYMMDD^YYYYMMDD`（23334） | `19800101^19800102` |
| 12 | p_location | 30 | Patient Location：`code^text`（23335） | — |
| 13 | act_code | 1 | Action-Code（23336） | `N`(New)/`A`(Add-on)/`C`(Cancel) |
| 14 | s_souce ⚠️ | 20 | Sample-Source：`Centrifugation^Lab Tube Type^AutoQC`（23337）（字段名手册原文即拼写错误，正确为 source） | `U^^` |
| 15 | ord_physic | 40 | Ordering-Physician：`Code^Last^First^MI^Suffix`（23338） | — |
| 16 | t_status | 1 | Tube-Status（23339–23340） | `E`(Expected)/`I`(Incomplete)/`C`(Complete)/`U`(Unknown)/`D`(Deferred，Oscar 2 起) |
| 17 | t_in_module | 5 | Tube-Input-Module：最近一次进入的模块 ID（23341–23342） | `0`=未知 |
| 18 | t_type | 3 | Tube-Type（23343，APPENDIX M） | `0..14, 16..19` |
| 19 | t_spinning | 2 | Tube-Spinning（23344） | `0`(unknown/unspun)/`1`(spun) |
| 20 | t_cap | 1 | Tube-Cap（23345） | `0`(unknown)/`N`(not capped)/`C`(capped) |
| 21 | t_cap_type | 2 | Tube-Cap Type（23363）⚠️ 8.23 原文「0 (unknown), 1..99 (capped with cap type 1..999)」自相矛盾（99 vs 999，且长度仅 2） | `15` |
| 22 | t_to_spin | 1 | Tube-To-Spin：需要/曾经需要离心（23364） | `1`/`0` |
| 23 | t_to_dec | 2 | Tube-To-Decap：需要/曾经需要开盖（23365–23366） | `1`/`0` |
| 24 | t_categ | 1 | Tube-Category（23367） | `P`(Primary)/`S`(Secondary) |
| 25 | t_weigth ⚠️ | 6 | Tube-Weight：毫克（23368）（字段名手册原文即拼写错误，正确为 weight） | 十进制数 |
| 26 | t_sealed | 1 | Tube-Sealed（23369） | `0`/`1` |
| 27 | t_num_seal | 2 | Tube-Number-Of-Seals（23370） | `0..99` |
| 28 | t_location | 33 | Tube-Location：`&a-bb-cccccc-dd-ee-ff`（23371–23381，语法见 §1.3） | `&1-15-00002-01-02-12` |
| 29 | t_err_code | 9 | Tube-Error-Code：样本错误码（23382） | `0000` |
| 30 | t_prev_err_code | 9 | Tube-Previous-Error-Code（23383） | `0000` |
| 31 | creation_time | 14 | Creation-Timestamp：记录生成时更新（23384–23385） | `YYYYMMDDHHMMSS` |
| 32 | update_time | 14 | Update-Timestamp：每次修改记录时更新（23386–23387） | `YYYYMMDDHHMMSS` |
| 33 | res_1 | 11 | Store-Timestamp：每次入 Storage 时更新（23388–23389） | `YYYYMMDDHHMM` |
| 34 | res_2 | 11 | Storing-Time：存储时长（分钟）（23390） | `120` |
| 35 | res_3 | 24 | Flags：标志位串（23391–23421） | `D`(decapped)/`L`(Long Term Storage)/`K`/`P`/`Q`/`H`(to shake)/`R…Sxx`(shake/mixing 超时)/`C`(Complete)/`S`(Skip Centrifugation)/`Zx`(DMS 复制管号)/`MR…`/`F`/`A`/`B` 等 |
| 36 | res_4 | 11 | Node ID：管前位置 Node（23422） | `15` |
| 37 | res_5 | 121 | Carrier ID：管前位置载物台（23423） | `C0001` |
| 38 | res_6 | 11 | Sample Volume：微升（23424） | `500` |
| 39 | res_7 | 255 | Tube-Log（23425，p39） | — |
| 40 | res_8 | 516 | Tube-Log（23425，p40） | — |
| 41–120 | test_1 … test_80 | 29 | Test-01…Test-80：`a-bb-ccc-d-s`（23426–23438，语法见 §1.4）⚠️ 8.23 的 SAMPLE-INFO 只暴露 p41–p120（即 Test-01…**Test-40**，每测试 2 个位置）；库中实际存 80 列（与 §2.2「每样本最多 80 测试」一致，APIO-PROTOCOL.md 92 行） | `1-07-LXL-0-CBC` |
| 121 | res_9 | 516 | Tube-Log | — |
| 122 | res_10 | 516 | Tube-Log | — |
| 123 | res_11 | 516 | Tube-Log | — |
| 124 | PNP_time | 12 | **Overdue Timestamp**（8410）⚠️ 8.23 字段清单无此参数（8.23 的 p121–p124 缺失，直接从 p120 跳到 p125），但见 2908「PNP Time = Timestamp for Overdue Timeout in PNP Analyzer」；PNP 相关（Pick-and-Place Analyzer 超期，参见 APIO §3.1 `RESET-TIME` 恢复 SC075/SC0A3/SC0A1 场景） | — |
| 125 | HIL | 29 | HIL Indices：溶血;黄疸;脂血指数（SIM 报告）（23439–23440） | `2.1;0.5;1.3` |
| 126 | S_Levels | 29 | Serum Levels：血清高/低水平（SIM 报告）（23441–23442） | `12;8` |
| 127 | Int_diam | 3 | Internal Diameter：管内径（1/10 mm；见 L001 的 `<Internal-Diameter>`，R19.1 起；另见 2912「Internal Diameter = Internal Diameter of Sample in 1/10 mm」）⚠️ 8.23 字段清单（p01–p126）无此参数 | `130` |

> **Tube-Log 合计容量**：res_7(255) + res_8/res_9/res_10/res_11(4×516) = **2319 chars**（8404–8405、8407–8409）。
> **⚠️ 手册字段名拼写错误（原文如此，勿"纠正"后去库里找）**：`s_diluition`(8354)、`s_souce`(8379)、`t_weigth`(8390)。
> **主键 (手册未明示)**：手册未说明 t_sample 主键（推测为 sample_id，待核实）。

### 1.3 t_location（索引 28）语法（23371–23381）

`&a-bb-cccccc-dd-ee-ff`：

| 分量 | 含义 |
|------|------|
| `a` | `0`(off-line) / `1`(on-line) / `2`(removed from rack) / `3`(disposed) |
| `bb` | 管所在 Node ID（`00`=Track） |
| `cccccc` | Rack ID（IOM/ROM/SRM/HVS/RM4）或 Carrier ID（Track）或 `00000`（其它节点） |
| `dd` | Rack floor 01–20（SRM）/ carrier queue（Track）/ `00`（其它） |
| `ee` | Rack lane 01–20（SRM/IOM）/ carrier sub-queue（Track）/ `00`（其它） |
| `ff` | Rack position 01–48（SRM/IOM）/ `00`（其它） |

示例：`&1-15-00002-01-02-12`、`&1-00-00000-15-01-00`、`0-00-00000-00-00-00`（23381）。

### 1.4 test_i（索引 41–120）语法（23426–23438）

`a-bb-ccc-d-s`：

| 分量 | 含义 |
|------|------|
| `a` | Test Status：`0`(TO DO)/`1`(SCHEDULED)/`2`(SAMPLED)/`3`(PROCESSED)/`4`(SORTED)/`5`(COMPLETED)/`6`(ALIQUOTED)/`7`(CANCELLED，Hotel 3 起)/`8`(DEFERRED，Oscar 1.1 起) |
| `bb` | Analyzer Node ID |
| `ccc` | Analyzer Type |
| `d` | Ordered By：`0`(Host LIS)/`1`(FlexLab GUI)/`2`(FlexLab Dream)/`3`(Instrument) |
| `s` | Test Code 字符串；`&xx` 前缀=定向到 Node ID xx；`#yy` 前缀=排除 Node ID yy |

---

## 2. tube_log 表（3.3，8431–8439）

- **R18 起**新增，存放管日志数据（8346）；条目**异步**写入样本库（FXSMS-13383，8340–8341）。
- **R23 起** tube_log 操作使用**专用数据库连接**（8439）。
- 手册索引从 **2** 起（8433），**索引 1 未列出**（推测为主键/自增列，(手册未明示)，8431–8439）。

| 索引 | 字段名 | 最大长度(chars) | 说明 |
|-----|--------|------|------|
| 2 | sample_id | 24 | Sample-ID |
| 3 | event | 11 | Type of event（事件类型；取值清单 (手册未明示)） |
| 4 | sender | 9 | Sender of event（事件发送方；取值清单 (手册未明示)） |
| 5 | error_code | 5 | Error Message（错误码） |
| 6 | message | 99 | More informations（补充信息） |
| 7 | timestamp | 14 | datetime |

---

## 3. aliquot_request 表（3.3.1，8441–8455）

- **Mike 1 起**新增，存放分装请求（对应 COMMENT **S018** 消息，手册 9.75 节，见 APIO-PROTOCOL.md §3.2）。
- 表中旧条目按 **Configuration.ini 的 `Expected Record Dwell Time`** 配置清除（8455）。
- 手册未列出索引/主键（字段顺序按原文 8445–8453）。

| 字段名 | 数据类型 | 说明 |
|--------|----------|------|
| Primary_SID | Varchar(24) | 主管 Sample ID（对应 S018 的 `<Primary-Sample-ID>`） |
| Aliquot_SID | Varchar(21), **Not Null** | 分装管 Sample ID（对应 `<Aliquot-SID-i>`） |
| Label_Type | Varchar(10) | 标签类型（对应 `<Label-Type-i>`） |
| Microliters | Integer | 分装量（对应 `<Microliters-i>`） |
| Label | Varchar(255) | 标签内容（(手册未明示) 具体格式） |
| Creation_Timestamp | Datetime | 创建时间 |
| Update_Timestamp | Datetime | 更新时间 |
| Reserved | Varchar(12) | 保留 |

---

## 4. automation_status 表（3.3.2，8459–8495）

- 与 errorhandling 表一样，**由 Synoptic Panel Tool 读取**（8460）。
- 字段语义对应 **STATUS 通知消息**（APIO-PROTOCOL.md §4.2，8.25 节 23490–23568）：node_mode 取值 `ON/PA/OFF/GTO/STB/EXE/FR/SH`、run_status `0..15`、error_type `G/P/Y/R/R+` 等。
- 手册未列出索引/主键（字段顺序按原文 8462–8495）。

| 字段名 | 数据类型 | 说明（对应 STATUS 字段，23535–23568） |
|--------|----------|------|
| node_id | Integer | Node ID（23535） |
| node_type | Varchar(4) | Node 类型（23536） |
| node_istance ⚠️ | Integer | Node 实例号（23537）（字段名手册原文即拼写错误，正确为 instance） |
| node_mode | Varchar(4) | Node 模式（23538–23539） |
| run_status | Integer | 运行状态（23543–23545） |
| error_code | Varchar(10) | 错误码，无错误 `0000`/空（23540–23541） |
| error_type | Varchar(3) | 错误类型（23542） |
| consumables | Varchar(20) | 消耗品计数（按 Node 类型语义不同，23551–23554） |
| consumable_sts | Char(1) | 消耗品状态（23555–23561） |
| full_car_in_main | Integer | 主 track 队列满载物台数（对应 Number-of-Carriers-in-Main-Queue，R19 起，23562–23564）⚠️ 表内拆为 full/empty 两列，STATUS 消息为单字段（载物台总数），二者的换算关系 (手册未明示) |
| empty_car_in_main | Integer | 主 track 队列空载物台数（同上） |
| full_car_in_sec | Integer | 次级 lane 满载物台数（对应 Number-of-Carriers-in-Secondary-Queue，R19 起，23565–23566） |
| empty_car_in_sec | Integer | 次级 lane 空载物台数（同上） |
| tube_in_module | Integer | 模块内载物台数（仅 WBB 模块，23567–23568） |
| last_update_tmp | Datetime | 最后更新时间（STATUS 消息无对应字段） |

---

## 5. errorhandling 表（3.3.2，8497–8505）

- 由 Synoptic Panel Tool 读取（8460）。手册未列出索引/主键（字段顺序按原文 8498–8504）。

| 字段名 | 数据类型 | 说明 |
|--------|----------|------|
| errorc_id | Integer | 错误记录 ID |
| Node | Varchar(4) | 出错 Node（Node ID/类型，(手册未明示) 具体语义） |
| Errorcode | Varchar(10) | 错误码（如 `SC001`） |
| Errormessage | Text | 错误描述文本 |
| Errorcolor | Varchar(7) | 错误颜色（绿/粉/黄/红等级别，对应 Gate/Node/LongRunning 严重度，参见 APIO §4.2.2） |
| RequiresUI | Varchar(15) | 是否需要 GUI 处理（取值 (手册未明示)） |

---

## 6. Direct Access to DMS Database（3.4，8508–8571）

**Kilo 2** 起引入（8509–8510）：FlexLab 可**直连 DMS 数据库**与 DMS 通信，作为 **ASTM 协议（APIO-PROTOCOL.md）的替代方式**。

### 6.1 配置项（按软件版本）

| 软件版本 | 文件 | 配置项 | 值格式 | 示例（手册原文） | 手册行号 |
|----------|------|--------|--------|------------------|----------|
| **仅 Kilo 2 / Kilo 2.1** | Reserved.ini | `LIS DB` | `LIS DB<TAB>DB user;DB password;DB Name` | `LIS DB	user;password-pwd;DMS-DB` | 8512–8515 |
| **Kilo 3 – R24** | LIS.ini | `LIS Database Access` | `DB user;DB password;DB Name` | `LIS Database Access	user;password -pwd;DMS-DB` | 8516–8519；2.13 节 6267–6270 |
| **R24.1 起** | LIS.ini | `LIS Database User` / `LIS Database Password` / `LIS Database Name` | 三个独立条目 | `LIS Database User	DB user` 等 | 8537–8544；2.13 节 6271–6303 |
| **R24.1 起** | Vault（可选） | — | 用户名/密码可从 **Vault** 加载（APPENDIX L，37164 行起）；启用后建议手动删掉 ini 中对应行；Vault 参数（Vault/Vault Host/Vault Port=8091 等）存于 Reserved.ini | — | 8545–8547；37164–37230 |
| **R21 起** | LIS.ini | `LIS Database Port` | 端口号 | `LIS Database Port	3306`（默认 3306；非默认值需 R21 的 `SMS.DL.BC.Runtime.dll`） | 8548–8551；2.13 节 6404–6408 |
| **所有版本** | LIS.ini | `LIS1 Communication Parameters` | `DB <IP-address>` | `LIS1 Communication Parameters	DB 192.168.100.218` | 8553–8556 |
| **所有版本** | LIS.ini | `LIS1 Sender` | DMS 中的 Sender 名 | `LIS1 Sender	flexlabR` | 8557–8559 |
| **所有版本** | LIS.ini | `LIS2 Sender` | **空字符串** | `LIS2 Sender`（空） | 8560–8562 |
| **所有版本** | LIS.ini | `LIS Channels` | `2` | `LIS Channels	2` | 8563–8565 |

> 注（2.13 节 6269–6274）：运行中修改上述 LIS Database 条目，**须重启 Flexlab** 才生效。

### 6.2 DMS 侧对接的表（8567–8571）

配置为直连模式后，FlexLab 与 DMS **不再使用 ASTM 协议**，改经 DMS 数据库的以下表通信：

| 表名 | 用途/条件 | 手册行号 |
|------|-----------|----------|
| **Dispatch** | DMS 侧调度/订单交换表；`LIS Frequency Polling`（R20 起，LIS.ini，10–2000 ms，默认 10 ms）控制**轮询 dispatch 表**的间隔（6393–6403） | 8569 |
| **Eventautomation** | 事件表，`LIS Use Alternative Event Table` = **Disabled**（默认）时使用 | 8570 |
| **Exchangeautomation** | 事件表，`LIS Use Alternative Event Table` = **Enabled**（R17.1 起）时使用 | 8571 |

相关运行参数：

| 配置项 | 文件 | 说明 | 手册行号 |
|--------|------|------|----------|
| `LIS Use Alternative Event Table` | LIS.ini | Enabled/Disabled，默认 Disabled；决定用 exchangeautomation 还是 eventautomation（R17.1 起） | 6304–6309；11845 |
| `LIS Frequency Polling (milliseconds)` | LIS.ini | 10–2000 ms，默认 10 ms；「Number of milliseconds to wait until a new query to dispatch table is done」（R20 起） | 6393–6403；11849 |
| `DMS Dispatch Timeout` | Reserved.ini | 默认 45 s；超时未响应则报 **SC0A7（DMS Database not responding）**，适用于 Dispatch / EventAutomation / ExchangeAutomation 三表；设 0 则永不报 SC0A7（R20 起） | 14372–14375；12411–12412 |

> ⚠️ **Dispatch / Eventautomation / Exchangeautomation 三表的字段结构手册未给出**（(手册未明示)，8567–8571）——这三张表定义在 **DMS（LIS）侧**，FlexLab 手册只引用表名。UniFlow 若采用直连模式，需按 DMS 侧定义建表并与 FlexLab 联调确认字段。

### 6.3 Vault 集成（APPENDIX L，37164 行起，R24.1）

- FXSMS-18987：Vault 用于避免凭证明文存于配置文件（37165–37167）。
- 可存 Vault 的项（37184–37194）：`Database User Name`/`Database Password`（Reserved.ini）、`LIS Database User`/`LIS Database Password`（LIS.ini，R24.1 新增）、`LIS Database Access`（**R24.1 起已从文件移除**）等；启用后建议手动删除 ini 中已入 Vault 的配置行（37210–37211）。
- 注意（37214–37215，另见 Reserved.ini 节 14960–14961）：Vault 启用时，HVS、SMS Database、**LIS-DMS Database** 连接均使用 Vault 凭据；连接异常先排查 Vault。

---

## 7. Counter Folder（3.5，8593–8628）

计数文件位于 **Counter 文件夹**；写入时机与 `CLOCK 5MIN` 活动相关（「save the Counter files」，APIO-PROTOCOL.md §4.3，26649–26654）。

### 7.1 Counter Files（3.5.1，8595–8616）

- 每日一文件，命名 **`Counter-YYYYMMDD.txt`**（8596–8597）。
- **电子表格格式**（spreadsheet format，具体列顺序/分隔符 (手册未明示)），含每个 Node 的当日/累计计数：

| 数据项 | 含义 | 适用范围 | 手册行号 |
|--------|------|----------|----------|
| Gates Today | 当日收到的 gate 事件数 | 所有 Node | 8599 |
| Gates Prev Days | 模块首次启动至前一天的 gate 事件总数 | 所有 Node | 8600–8601 |
| Tubes Today | 当日该模块处理的管数 | 所有 Node | 8602 |
| （原文误印为 "Gates Prev Days"）⚠️ | 模块首次启动至前一天的**处理管数**（应为 "Tubes Prev Days"，8603 行原文笔误） | 所有 Node | 8603–8604 |
| Unloaded Today | 当日该模块从 Track 卸载的管数 | 仅 Pick-and-Place 模块/Analyzer | 8605–8606 |
| Unloaded Prev Days | 同上，累计 | 仅 Pick-and-Place 模块/Analyzer | 8607–8608 |
| Disposed Today | 当日该模块丢弃的管数 | 仅 Storage/HVS 模块 | 8609–8610 |
| Disposed Prev Days | 同上，累计 | 仅 Storage/HVS 模块 | 8611–8612 |

另含系统级数据（8613–8616）：

| 数据项 | 含义 |
|--------|------|
| Laboratory | 实验室名（Configuration/Settings 屏配置） |
| Software Version | 当日运行的软件版本 |
| Database Records | **样本库（Sample Database）中的记录数**（即 t_sample 行数） |

### 7.2 Hourly Counter Files（3.5.2，8618–8628）

- 每日一文件，命名 **`CounterByHourYYYYMMDD.txt`**（8619–8620）。
- 电子表格格式，按 Node × 小时（0–23，系统活跃的小时）统计（8621–8622）：

| 数据项 | 含义 | 适用范围 |
|--------|------|----------|
| `<Node Name>/<Node Instance> Processed` | 该小时内模块处理的管数 | 所有 Node |
| `<Node Name>/<Node Instance> Unloaded` | 该小时内从 Track 卸载的管数 | 仅 Pick-and-Place 模块/Analyzer |
| `<Node Name>/<Node Instance> Disposed` | 该小时内丢弃的管数 | 仅 Storage/HVS 模块 |

---

## 8. 手册未明确 / 待核实项（汇总）

1. **t_sample / tube_log 的 MySQL 数据类型、主键、建表 DDL 均未给出**（只有 Max Length (chars)，8349–8439）；t_sample 主键推测为 sample_id。
2. **tube_log 索引 1 未列出**（索引从 2 起，8433–8438）；`event` / `sender` 字段取值清单未给出。
3. **aliquot_request / automation_status / errorhandling 未列主键**；`errorhandling.Node`、`RequiresUI` 语义/取值未明示；`aliquot_request.Label` 格式未明示。
4. **t_cap_type 长度 2 与 8.23「1..999」矛盾**（8386 vs 23363）。
5. **8.23 的 p121–p124 缺失**：8.23 SAMPLE-INFO（23319–23442）仅列 p01–p126，t_sample 的 res_9/res_10/res_11（121–123，Tube-Log）与 PNP_time（124）在此缺失；其含义另见 GUI 字段清单 2907–2908（「121..123 fields reserved for Tube Log」；「124 PNP Time = Timestamp for Overdue Timeout in PNP Analyzer」）。
6. **手册字段名拼写错误（原文如此）**：`s_diluition`、`s_souce`、`t_weigth`（t_sample）、`node_istance`（automation_status）、3.5.1 第二个 "Gates Prev Days" 应为 "Tubes Prev Days"。
7. **Direct Access 模式三张 DMS 表（Dispatch/Eventautomation/Exchangeautomation）的字段结构手册未给出**——由 DMS 侧定义，需与 DMS（UniFlow 所在侧）约定。
8. **Counter 文件的"spreadsheet format"具体列序/分隔符未定义**（8598、8621）。
9. **automation_status 的 full/empty_car 拆分与 STATUS 消息单字段"载物台总数"的对应关系未明示**（8490–8493 vs 23562–23566）。
10. Kilo 2 的 Reserved.ini `LIS DB` 条目中 `<TAB>` 为字面制表符分隔（示例 8515 佐证），手册文字描述与示例格式略有出入，以示例为准。

---

## 9. 与 UniFlow 代码相关的观察

### 9.1 UniFlow 直读 Dream 样本库（t_sample）

UniFlow 的 `Aptio.Database` 配置（`appsettings.json`）直接连接 **Dream 的 MySQL 样本库**（库名 `flexlab`），多个服务**绕过 ASTM 协议直接查询 t_sample**：

| UniFlow 位置 | SQL | 用到的 t_sample 字段 | 与手册对应 |
|--------------|-----|----------------------|-----------|
| `DisposeSample/Services/DisposeDatabaseService.cs:84`（`CheckSrmSampleCountAsync`） | `SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE '&1-{nodeId}-%'` | `t_status`、`t_location` | `t_status='C'`=Complete（23339）；`&1-` = on-line（23372） |
| `DisposeDatabaseService.cs:182`（`CheckDisposedAsync`） | `SELECT sample_id FROM t_sample WHERE sample_id=@B AND t_location LIKE '&3-{nodeId}-%'` | `sample_id`、`t_location` | `&3-` = disposed（23372） |
| `DisposeDatabaseService.cs:241–244`（`GetAllScanableSamplesAsync`） | `SELECT sample_id, CONCAT_WS('^', test_1..test_80) FROM t_sample WHERE t_status='C' AND t_location LIKE '&1-%'` | `sample_id`、`test_1..test_80`、`t_status`、`t_location` | 80 个 test 列与 8406 行一致 |
| `SrmExport/Services/ExportDatabaseService.cs:47`（`GetDisposedSamplesAsync`） | `SELECT sample_id, t_location, update_time FROM t_sample WHERE … AND t_location LIKE '&3-{nodeId}-%'` | `sample_id`、`t_location`、`update_time` | `update_time` 为 Update-Timestamp（23386） |

- **t_location 前缀匹配与手册语法一致**：`&1-{nodeId}-`（在轨/在架上）与 `&3-{nodeId}-`（已丢弃）符合 8.23 的 `&a-bb-cccccc-dd-ee-ff`（23371–23381），`a` 位 1=on-line、3=disposed。
- **⚠️ 待核实（代码注释与手册不一致）**：`DisposeDatabaseService.cs:238` 注释称 test 列格式为「状态;序号;类型;子类型;测试名;...」（**分号**分隔），而手册 8.23（23426）为 `a-bb-ccc-d-s`（**短横线**分隔，示例 `1-07-LXL-0-CBC`）。代码用 `CONCAT_WS('^', …)` 拼接后按 `^` 拆列、列内模糊匹配测试名（`LIKE %name%`），对两种分隔格式都能工作，但列内结构应以实测库数据为准。
- UniFlow 自建的 `sam_dispose_status` 表（`DisposeDatabaseService.cs:40–58`）为 UniFlow 侧状态表，**不属于手册定义的任何表**。

### 9.2 DMS 直连模式（§6）与 UniFlow 现状

- UniFlow 当前走 **ASTM over TCP**（`AptioCommandService` + `AptioSocketClient`，见 APIO-PROTOCOL.md §6），**未使用 3.4 的数据库直连模式**；代码库中**无** `Dispatch` / `Eventautomation` / `Exchangeautomation` 表引用。
- UniFlow 侧 DMS 数据库（`DMS.*` 配置）使用的表为 `reqtest` / `reqtube` / `reqtestresult` 等（`DmsDatabaseService.cs`、`SampleCleanupService.cs`，按 `codsid`/`codoid`/`codtest` 列识别）——这些是 **DMS 侧**业务表，与 §6.2 的三张对接表**不同**；若将来切直连模式，需另建 Dispatch/Eventautomation（或 Exchangeautomation）并与 FlexLab 联调（字段结构手册未给出，见 §8.7）。
- 注意区分 UniFlow 的两个数据库连接：`Aptio.Database`（Dream 样本库，读 t_sample）与 `DMS.*`（DMS 业务库，读写 reqtest 等）。§6 的直连模式指 **FlexLab 侧连 DMS 库**，方向与 UniFlow 读 Dream 库相反，二者可并存。

### 9.3 字段对应速查（UniFlow 视角）

| UniFlow 需要 | t_sample 字段 | 手册依据 |
|--------------|---------------|----------|
| 样本条码 | `sample_id`（24 chars） | 8350 / 23323 |
| 完成态判断 | `t_status='C'` | 8381 / 23339 |
| 位置（节点/架/位） | `t_location` | 8393 / 23371–23381 |
| 已丢弃判断 | `t_location` 首段 `&3-` | 23372 |
| 测试列表 | `test_1..test_80`（每列 29 chars） | 8406 / 23426–23438 |
| 记录更新时间 | `update_time`（14 chars，`YYYYMMDDHHMMSS` 字符串） | 8397 / 23386 |
| 错误码 | `t_err_code` / `t_prev_err_code` | 8394–8395 / 23382–23383 |
