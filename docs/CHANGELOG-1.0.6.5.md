# UniFlow v1.0.6.5 改动记录

- 日期：2026-09-28
- 版本：1.0.6.4 → **1.0.6.5**
- 部署包：`UniFlow-1.0.6.5-linux-x64.tar.gz` / `UniFlow-1.0.6.5-win-x64.zip`
- 概述：修复 S010 优先级升级报文缺失 `<New-Priority>` 字段；修复 Linux 全新安装脚本对已内嵌 SQLite 原生库的硬依赖；重写并清理测试工程；新增可复现打包脚本。

---

## 一、S010 优先级升级报文修复（Priority）

**手册依据**：FlexLab `DCAU-H00.816.12.01.02.pdf` §9.5（第 9 章 Messages from Host LIS or GUI to Dream）

```
COMMENT S010^<Sample-ID>\<New-Priority>^S
```

`<New-Priority>` = `A`(ASAP) / `S`(STAT)（ASAP 自软件版本 R20 起）。

**问题**：`AptioCommandService.SendStatPriorityAsync` 原发送 `COMMENT S010^{barcode}^S`，**漏掉了 `<New-Priority>` 子字段**，不符合手册格式（该缺陷自原始 Delphi 程序 `DoUpdatePriority` 延续而来）。

**修复**：`code/src/UniFlow/DisposeSample/Services/AptioCommandService.cs`

```csharp
// 修复前
var cmd = $"COMMENT S010^{barcode}^S";
// 修复后（S=STAT，保持原语义）
var cmd = $"COMMENT S010^{barcode}\\S^S";
```

**影响面**：`PriorityWorker`（由 `Aptio.Priority.TestName` 触发的把样本升级为 STAT 优先）现在发送的报文符合手册。若现场需要升级为 **ASAP**，把值改为 `A` 即可（前提 Dream 版本 ≥ R20）。

> 手册中 Dream → Host LIS 的反向通知为 §8.7 `COMMENT S007`，本次未涉及。

---

## 二、Linux 安装脚本修复

`deploy/linux/install.sh` 原无条件执行 `cp -f "$SCRIPT_DIR/libe_sqlite3.so"`。但自 1.0.5.6 起单文件发布已**将 SQLite 原生库内嵌**（`IncludeNativeLibrariesForSelfExtract`），安装包不再单独附带该文件，导致 **Linux 全新安装时脚本在 `set -euo pipefail` 下直接中断**。

**修复**：改为存在才拷贝，兼容旧包。

```bash
# SQLite 原生库已内嵌于单文件主程序；仅当安装包额外附带时才拷贝（兼容旧包）
if [ -f "$SCRIPT_DIR/libe_sqlite3.so" ]; then
    cp -f "$SCRIPT_DIR/libe_sqlite3.so" "$INSTALL_DIR/"
fi
```

---

## 三、测试工程修复（45/45 通过）

### `DisposeSampleWorkerTests` 重写

原测试基于旧架构，早已无法编译（引用已删除的 `AptioAutoProcessConfig` / `AptioAutoDisposeConfig`、旧接口 `SendAndReceiveAsync`、7 参构造函数）。改为对接现行架构：

- 配置改用 `AptioConfig` / `AptioDisposeSampleConfig`，并经新增的 `DisposeSampleWorker.LoadConfig(...)` 显式注入，不再依赖真实 `appsettings.json`；
- socket mock 改用 `SendAsync`；
- 构造函数改为现行 6 参（`logger, socket, db, commands, statusDecoder, HealthStore`），用临时库构造真实 `HealthStore`；
- 修正 `HandleReady_NoAckReceived_StaysInReady`，使其真正走到「无 ACK」分支。

### `ExportFileServiceTests` 断言更新

导出格式早已与原始 `SRM_ExportDisposeSample.exe` 对齐（表头 `Barcode\tDispose time`，无 `Total:`/`Location`/`Export Time:`）。3 处过期断言更新为当前真实输出。

- 为测试新增的生产代码仅 `DisposeSampleWorker.LoadConfig`（公开注入点），无运行时行为变更。

---

## 四、可复现打包

- 新增 `deploy/package.sh`：一条命令完成 Linux/Windows 双平台单文件发布 + 组装 `uniflow/` + 产出 `tar.gz`/`zip`。
  - 关键参数 `-p:IncludeNativeLibrariesForSelfExtract=true`（SQLite 原生库内嵌，安装包无需单独提供）。
  - 版本号默认取 `UniFlow.csproj` 的 `<Version>`，可用 `VERSION=` 覆盖；`APP_SETTINGS=` 指定打进包内的配置。
- 安装辅助文件纳入版本管理：`deploy/INSTALL.md`、`deploy/windows/README.md`、`deploy/windows/uninstall.ps1`。
- `deploy/build.sh` 的 `VERSION` 由陈旧的 `1.0.5` 更新为 `1.0.6.5`。

---

## 五、验证摘要

- 编译：`Build succeeded, 0 Error(s)`（仅存量 warning）。
- 测试：`Passed! - Failed: 0, Passed: 45, Total: 45`。
- 打包：Linux `46,210,192 B` / Windows `47,037,591 B`，目录结构与 1.0.6.4 一致。
- 1.0.6.5 Linux 包实测启动（沙箱，**不含** `libe_sqlite3.so`）：
  - 正常创建 `uniflow_admin.db`（内嵌 SQLite 可用）；
  - `GET /api/health` → `healthy`；
  - `GET /api/update/status` → `currentVersion = 1.0.6.5+0feb562`。
