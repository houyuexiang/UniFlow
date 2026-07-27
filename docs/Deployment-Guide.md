# UniFlow 部署配置手册

## 1. 环境要求

| 组件 | 要求 |
|------|------|
| .NET 运行时 | .NET 10.0+ |
| 数据库 | MySQL 5.7+ (Aptio FlexLab / DMS) |
| 操作系统 | Windows 10/Server 2016+ / Linux (Ubuntu 20.04+, CentOS 8+, Debian 11+) |
| 网络 | 可访问 Aptio 服务器(端口 2055)、MySQL(3306)、Centralink(可配置) |

---

## 2. 构建

```bash
# 克隆项目后
cd src

# 构建所有项目（含测试）
dotnet build UniFlow.sln

# 运行测试
dotnet test UniFlow.Tests/UniFlow.Tests.csproj

# 发布 - Linux (systemd)
dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /opt/uniflow

# 发布 - Windows (Windows Service)
dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o C:\UniFlow

# Docker 构建（无需本地 .NET 10 SDK）
docker build -t uniflow:latest .
```

---

## 3. 部署

### 3.1 Linux (systemd)

```bash
# 创建用户
sudo useradd -r -s /sbin/nologin -M uniflow

# 部署二进制
sudo mkdir -p /usr/share/uniflow/UniFlow
sudo cp -r publish/* /usr/share/uniflow/UniFlow/
sudo chown -R uniflow:uniflow /usr/share/uniflow

# 安装 systemd 服务
sudo cp deploy/linux/uniflow.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now uniflow.service

# 查看日志
sudo journalctl -fu uniflow.service
```

**systemd unit 文件** (`deploy/linux/uniflow.service`)：

```ini
[Unit]
Description=UniFlow - Laboratory Automation Workflow Service
After=network.target mysql.service mariadb.service

[Service]
Type=notify
ExecStart=/usr/share/uniflow/UniFlow/UniFlow
WorkingDirectory=/usr/share/uniflow/UniFlow
Restart=on-failure
RestartSec=10
User=uniflow
Group=uniflow
Environment=ASPNETCORE_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

### 3.2 Windows (Windows Service)

```powershell
# 使用部署脚本
.\deploy\windows\install-service.ps1

# 或手动安装
sc.exe create UniFlow binPath="C:\UniFlow\UniFlow.exe" start=auto
sc.exe start UniFlow

# 查看状态
Get-Service UniFlow
```

---

### 3.3 Docker

```bash
# 构建镜像
docker build -t uniflow:latest .

# 使用 docker-compose 启动（含 MySQL）
docker compose up -d

# 查看日志
docker compose logs -f

# 停止
docker compose down

# 仅启动 UniFlow（外部 MySQL）
docker run -d --name uniflow \
    -p 5100:5100 \
    -v /path/to/appsettings.json:/app/appsettings.json:ro \
    -v uniflow_logs:/app/logs \
    -v uniflow_db:/app/uniflow_admin.db \
    -e UniFlow__Database__Host=192.168.1.200 \
    -e UniFlow__Database__Password=your_password \
    -e UniFlow__Aptio__Ip=192.168.1.200 \
    uniflow:latest

# 访问 Web 管理控制台
# http://localhost:5100
```

**Docker 环境变量覆盖**：

| 环境变量 | 默认值 | 说明 |
|----------|--------|------|
| `UniFlow__Database__Host` | mysql | MySQL 地址 |
| `UniFlow__Database__Port` | 3306 | MySQL 端口 |
| `UniFlow__Database__User` | root | 数据库用户 |
| `UniFlow__Database__Password` | root | 数据库密码 |
| `UniFlow__Database__Database` | flexlab | 数据库名 |
| `UniFlow__Aptio__Ip` | 127.0.0.1 | Aptio 服务器地址 |
| `UniFlow__WebAdmin__BindIp` | 0.0.0.0 | Web 管理绑定地址 |

---

## 4. 配置文件

所有配置在 `appsettings.json`，部署后直接编辑重启服务即可。

### 4.1 完整配置模板

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Warning",
      "UniFlow": "Information"
    },
    "UniFlowFile": {
      "FileLoggingEnabled": true,
      "LogDirectory": "logs",
      "MaxFileSizeMb": 10,
      "RetentionDays": 30,
      "LogLevel": "Information"
    }
  },
  "Features": {
    "DisposeSampleEnabled": true,
    "SrmExportEnabled": true,
    "WorkListCleanerEnabled": false,
    "DmsAutoOrderEnabled": false
  },
  "Aptio": {
    "Ip": "192.168.1.200",
    "Port": 2055
  },
  "Database": {
    "Host": "192.168.1.200",
    "Port": 3306,
    "User": "root",
    "Password": "your_password",
    "Database": "flexlab"
  },
  "Dispose": {
    "SrmNodeId": "09",
    "CommandType": 0,
    "CommandName": "view_overtimestoragesample",
    "DiscardRunDate": "1,2,3,4,5,6,7",
    "DiscardTimeRange": "01:30-05:45,3800;",
    "LoopIntervalSeconds": 3,
    "MaxWaitDiscardCount": 2,
    "MaxOnetimeSelectDiscardCount": 8,
    "AllowSrmErrorCode": "0000,0F0A,0E59,AAAA",
    "EnableDeliver": false,
    "DeliverTestName": "",
    "EnableUpdatePriority": false,
    "PriorityTestName": "",
    "DeliveryListFilePath": ""
  },
  "Export": {
    "SrmNodeId": "04",
    "ExportTime": "08:30",
    "LoopIntervalSeconds": 60,
    "LogRetentionDays": 30
  },
  "WorkListCleaners": [
    {
      "Enabled": false,
      "MdbFilePath": "",
      "CentralinkIp": "",
      "CentralinkPort": 0,
      "CentralinkDriver": "AUTO",
      "LoopIntervalSeconds": 60,
      "TimeSpanMinutes": 5,
      "Prefix": "ERR",
      "NaPrefix": "NA",
      "NoSampleLasFlag": "NS",
      "NaResultLasFlag": "NA"
    }
  ],
  "DMS": {
    "Enabled": false,
    "DbHost": "127.0.0.1",
    "DbPort": 3306,
    "DbUser": "root",
    "DbPassword": "",
    "DbName": "dms",
    "LoopIntervalSeconds": 60,
    "EnablePitStopMonitor": false,
    "PitStopTimeoutMinutes": 1,
    "AutoModifyTestStatus": "1",
    "TestTriggerSampleDeletion": ""
  }
}
```

### 4.2 配置节说明

#### Features — 功能开关

| 开关 | 默认值 | 说明 |
|------|--------|------|
| DisposeSampleEnabled | true | 标本丢弃/递送/优先级 |
| SrmExportEnabled | true | 丢弃记录每日导出 |
| WorkListCleanerEnabled | false | 工作列表清理 |
| DmsAutoOrderEnabled | false | DMS 自动工单 |

#### Aptio — Aptio 服务器

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Ip | 127.0.0.1 | Aptio 服务器 IP |
| Port | 2055 | Socket 通信端口 |

#### Database — MySQL 数据库（FlexLab）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Host | 127.0.0.1 | MySQL 地址 |
| Port | 3306 | MySQL 端口 |
| User | root | 用户名 |
| Password | root | 密码 |
| Database | flexlab | 数据库名 |

#### Dispose — 标本丢弃

| 参数 | 默认值 | 说明 |
|------|--------|------|
| SrmNodeId | 09 | SRM 冰箱节点编号 |
| CommandType | 0 | 0=SQL视图, 1=存储过程 |
| CommandName | view_overtimestoragesample | 视图/存储过程名称 |
| DiscardRunDate | 1,2,3,4,5,6,7 | 运行日(1=周一..7=周日) |
| DiscardTimeRange | 01:30-05:45,3800; | 时间段+阈值,多段用;分隔 |
| LoopIntervalSeconds | 3 | 轮询间隔(秒) |
| EnableDeliver | false | 启用递送命令 |
| EnableUpdatePriority | false | 启用优先级标记 |

#### DiscardTimeRange 格式

```
HH:MM-HH:MM,threshold;HH:MM-HH:MM,threshold;
```

示例：`01:30-05:45,3800;18:00-07:00,7500;`
- 01:30~05:45 期间，标本数超过 3800 时丢弃
- 18:00~07:00（跨天）期间，标本数超过 7500 时丢弃

#### Export — 记录导出

| 参数 | 默认值 | 说明 |
|------|--------|------|
| SrmNodeId | 04 | SRM 节点号 |
| ExportTime | 08:30 | 每日导出时间 |

#### WorkListCleaners — 工作列表清理（数组）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Enabled | false | 启用 |
| MdbFilePath | "" | MS Access 数据库路径 |
| CentralinkIp | "" | Centralink 服务器 IP |
| LoopIntervalSeconds | 60 | 轮询间隔 |

#### DMS — DMS 自动工单

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Enabled | false | 启用 |
| DbHost | 127.0.0.1 | DMS MySQL 地址 |
| DbName | dms | DMS 数据库名 |
| EnablePitStopMonitor | false | 启用 PitStop 监控 |
| AutoModifyTestStatus | 1 | 状态修正模式(1/2/3) |

---

## 5. 日志

### 5.1 文件位置

默认在程序运行目录下的 `logs/` 子目录，可自定义。

### 5.2 文件结构

```
logs/
├── DisposeSample_2026-07-27.log
├── SrmExportWorker_2026-07-27.log
├── DisposeDatabaseService_2026-07-27.log
├── ImmuliteCleaner_2026-07-27.log
├── DmsAutoOrderWorker_2026-07-27.log
├── DmsDatabaseService_2026-07-27.log
├── ...                               ← 其他模块
└── UniFlow_Error_2026-07-27.log      ← 所有 ERROR/FATAL 汇总
```

### 5.3 日志格式

```
2026-07-27 12:00:00.123 [INFO] [DisposeSampleWorker] State → Ready (3 records)
2026-07-27 12:00:00.456 [WARN] [DmsDatabaseService] PitStop stuck, cleaned table pitstop_01
2026-07-27 12:00:00.789 [ERROR] [AptioSocketClient] Connect failed: Connection refused
```

### 5.4 轮转策略

- **按天分割**：每天一个新文件
- **按大小轮转**：超过 10MB 自动拆分（`_1.log`, `_2.log`）
- **按时间清理**：超过 30 天的日志自动删除

---

## 6. 运维命令

### 6.1 Linux

```bash
# 服务管理
sudo systemctl start|stop|restart|status uniflow.service
sudo systemctl enable|disable uniflow.service

# 实时日志
sudo journalctl -fu uniflow.service

# 查看最近日志
sudo journalctl -u uniflow.service --since "1 hour ago"

# 查看错误日志
sudo journalctl -u uniflow.service -p err

# 配置文件修改后重启
sudo systemctl restart uniflow.service

# 卸载
sudo systemctl stop uniflow.service
sudo systemctl disable uniflow.service
sudo rm /etc/systemd/system/uniflow.service
sudo rm -rf /usr/share/uniflow
```

### 6.2 Windows

```powershell
# 服务管理
Start-Service UniFlow
Stop-Service UniFlow
Restart-Service UniFlow
Get-Service UniFlow

# 配置修改后重启
Restart-Service UniFlow

# 卸载
Stop-Service UniFlow
sc.exe delete UniFlow
Remove-Item -Recurse C:\UniFlow
```

---

## 7. 性能建议

| 场景 | 建议 |
|------|------|
| 丢弃轮询间隔 | 3~6 秒（短间隔增加数据库压力） |
| 导出时间 | 设为业务低峰期（如凌晨） |
| 日志保留 | 30 天为宜，根据磁盘空间调整 |
| 数据库连接 | UniFlow 使用短连接+连接池，无需额外配置 |
