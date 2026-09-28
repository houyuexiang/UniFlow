# UniFlow v1.0.6.5 部署安装手册

实验室自动化工作流服务 (.NET 10)，单文件自包含部署，即插即用。

---

## 一、安装包内容

### Linux 包 (`UniFlow-1.0.6.5-linux-x64.tar.gz`)

| 文件 | 说明 |
|------|------|
| `UniFlow` | 单文件可执行程序（含 .NET 运行时，51MB） |
| `appsettings.json` | 配置文件 |
| `wwwroot/` | Web 管理界面资源 |
| `libe_sqlite3.so` | SQLite 原生库（已内嵌于主程序，无需单独部署） |
| `install.sh` | 一键安装脚本（systemd 服务） |
| `INSTALL.md` | 本安装手册 |
| `README.md` | 使用说明 |

### Windows 包 (`UniFlow-1.0.6.5-win-x64.zip`)

| 文件 | 说明 |
|------|------|
| `UniFlow.exe` | 单文件可执行程序（含 .NET 运行时，49MB） |
| `appsettings.json` | 配置文件 |
| `wwwroot/` | Web 管理界面资源 |
| `e_sqlite3.dll` | SQLite 原生库（已内嵌于主程序，无需单独部署） |
| `install.ps1` / `install-enable.ps1` / `uninstall.ps1` | 安装 / 安装并自启 / 卸载 脚本（LocalSystem） |
| `web.config` | IIS 托管配置（可选） |
| `INSTALL.md` | 本安装手册 |
| `README.md` | 使用说明 |

---

## 二、Linux 部署

### 系统要求
- Linux x64（glibc 2.17+），2GB 内存以上
- 无需预装 .NET 运行时

### 安装步骤

```bash
# 1. 解压
tar -xzf UniFlow-1.0.6.5-linux-x64.tar.gz
cd uniflow

# 2. 修改配置（编辑 appsettings.json，见第四节）
vim appsettings.json

# 3. 安装为 systemd 服务
sudo ./install.sh            # 安装并启动（默认不开机自启）
sudo ./install.sh --enable   # 同时启用开机自启
```

### 验证

```bash
# 健康检查
curl http://localhost:5100/api/health
# 期望输出: {"status":"healthy","modules":{...}}

# 服务状态
systemctl status uniflow
```

浏览器访问 `http://<服务器IP>:5100`

### 日常管理

```bash
systemctl start uniflow      # 启动
systemctl stop uniflow       # 停止
systemctl restart uniflow    # 重启
systemctl status uniflow     # 状态
journalctl -fu uniflow       # 实时日志
```

### 卸载

```bash
sudo systemctl stop uniflow && sudo systemctl disable uniflow
sudo rm -f /etc/systemd/system/uniflow.service
sudo rm -rf /usr/share/uniflow
sudo userdel uniflow
```

---

## 三、Windows 部署

### 系统要求
- Windows 10/11 或 Windows Server 2012+
- x64 架构
- 无需预装 .NET 运行时

### 安装步骤

```powershell
# 1. 解压到目标目录，如 C:\UniFlow

# 2. 修改配置（用记事本编辑 C:\UniFlow\appsettings.json，见第四节）

# 3. 右键 PowerShell → "以管理员身份运行"，执行：
cd C:\UniFlow
.\install.ps1              # 安装为 Windows 服务（LocalSystem），手动启动
.\install-enable.ps1       # 安装并设为开机自启
.\uninstall.ps1            # 卸载
```

### 验证

```powershell
# 服务状态
Get-Service UniFlow

# 健康检查（浏览器访问）
# http://<服务器IP>:5100/api/health
```

### 日常管理

```powershell
Start-Service UniFlow     # 启动
Stop-Service UniFlow      # 停止
Restart-Service UniFlow   # 重启
Get-Service UniFlow       # 状态
```

### 卸载

```powershell
.\uninstall.ps1
```

### 注意事项
- 服务以 **LocalSystem** 账户运行，无需配置账号
- 防火墙需放行 5100 端口
- 若 5100 端口被占用，修改 `appsettings.json` 中 `WebAdmin:Port`

---

## 四、配置文件说明 (`appsettings.json`)

### 功能开关（修改后 3-5 秒热加载，无需重启）

```json
"Features": {
  "AptioAutoProcess": true,      // 样本丢弃 + 导出
  "Delivery": false,             // 标本递送
  "Priority": false,             // 优先级控制
  "TestNameDispose": false,      // 按测试名丢弃
  "ImmuliteWorkOrderClean": false, // WorkListCleaner
  "DmsAutoOrder": false          // DMS 自动下单
}
```

> Web 管理界面（仪表盘页）可以直接用滑块开关这些功能。

### 仪器连接（修改后需重启）

```json
"Aptio": {
  "Ip": "192.168.1.200",        // Aptio 服务器地址
  "Port": 2055,
  "SrmNodeIds": ["09", "16"],   // SRM 节点编号
  "Database": {
    "Host": "192.168.1.200",    // MySQL 地址
    "Port": 3306,
    "User": "root",
    "Password": "root",
    "Database": "flexlab"
  }
}
```

### DMS 配置（需先开启 `Features:DmsAutoOrder`）

```json
"DMS": {
  "Enabled": true,
  "DbHost": "192.168.1.200",
  "DbPort": 3306,
  "DbUser": "root",
  "DbPassword": "",
  "DbName": "dms",
  "LoopIntervalSeconds": 60,
  "AutoModifyTestStatus": "1",   // 1=不修改, 2/3=修正模式
  "EnablePitStopMonitor": false,
  "TestTriggerSampleDeletion": ""
}
```

### Web 管理界面

```json
"WebAdmin": {
  "Enabled": true,
  "BindIp": "0.0.0.0",     // 绑定所有网卡
  "Port": 5100,
  "ErrorRetentionDays": 30,
  "HealthCheckIntervalSeconds": 60
}
```

---

## 五、日志说明

日志按**每天一个目录**存放：

```
Linux:  /usr/share/uniflow/logs/2026-09-03/DisposeSample_0.log
Windows: C:\UniFlow\logs\2026-09-03\DisposeSample_0.log
```

- 每个模块独立文件：`DisposeSample_0.log`, `SrmExport_0.log`, `DMSAutoOrder_0.log`, `UniFlow_Error_0.log` 等
- 记录增删改操作会写入条码/样本号等特征；SQL 只在失败时记录
- 默认保留 30 天，超过自动清理
- 错误日志有两条路径（同时生效）：文件日志 `UniFlow_Error_<日期>.log`（ERROR 级别）+ SQLite 管理库（Web"错误日志"页 / `GET /api/errors`）

---

## 六、版本记录

### v1.0.5.4
- 新增：SRM 导出 `OutputPath` 支持绝对路径（如 `/data/exports` 或 `D:\exports`）
- 更新：Windows 安装改为三个独立脚本 `install.ps1` / `install-enable.ps1` / `uninstall.ps1`

### v1.0.5.2
- 修复：SRM 导出文件路径双重 `DisposeFile` 嵌套（导出文件现在写到 `OutputPath` 单一目录）

### v1.0.5.1
- 修复：Web"日志文件"页无法读取正在写入中的日志文件（改用 `FileShare.ReadWrite` 读取）

### v1.0.5
- 修复：SRM 导出连接 utf8mb4 数据库报 `COLLATION latin1_swedish_ci` 错误（连接串改 utf8mb4，去除 SQL 排序规则）
- 修复：`SrmExport` 健康状态未反映数据库连通性（DB 不可达时现显示 degraded）
- 修复：`/api/logs/list` 支持按天目录递归列出日志

### v1.0.4
- 修复：`/api/logs/view` 支持按天目录路径读取日志，增加路径穿越防护（非法路径返回 400）

### v1.0.3
- 新增：Windows 服务支持（`UseWindowsService`，LocalSystem 账户）
- 新增：DMS 状态修正记录受影响样本号 (SID)
- 新增：日志改为每天目录存放

### v1.0.2
- 新增：健康仪表盘各模块启停开关、配置热加载、错误日志
- 修复：SRM 导出每月 1 号缺上月最后一天数据（`curdate()-1` bug）
- 修复：数据库膨胀（健康记录封顶 5000 条/模块）

---

## 七、配置参考

完整逐字段配置说明见 `CONFIG-REFERENCE.md`（字段含义、默认值、取值范围、是否热加载）。

*安装完成后如有问题，查看日志定位：`journalctl -fu uniflow` (Linux) 或事件查看器 (Windows)*