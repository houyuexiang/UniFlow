# UniFlow Windows 安装包

实验室自动化工作流服务 (.NET 10)，单文件部署，即插即用。

## 包含内容

| 文件 | 说明 |
|------|------|
| `UniFlow.exe` | 单文件可执行程序 (含 .NET 运行时) |
| `appsettings.json` | 默认配置文件 |
| `e_sqlite3.dll` | SQLite 原生库（已内嵌于主程序，无需单独部署） |
| `wwwroot/` | Web 管理界面资源 |
| `install.ps1` | 一键安装脚本 (Windows 服务) |
| `web.config` | IIS 托管配置 (可选) |

## 系统要求

- Windows 10/11 或 Windows Server 2012+
- x64 架构
- 无需预装 .NET 运行时 (自包含)

## 安装

1. 解压安装包到目标目录，如 `C:\UniFlow\`
2. 右键 PowerShell → **以管理员身份运行**
3. 进入目录并执行安装:

```powershell
cd C:\UniFlow
.\install.ps1             # 安装并启动 (默认手动启动)
.\install.ps1 -Enable     # 安装并设为开机自启
.\install.ps1 -Uninstall  # 卸载服务
```

## 使用

浏览器访问 `http://<服务器IP>:5100` 打开管理界面。

| 操作 | PowerShell 命令 |
|------|----------------|
| 启动 | `Start-Service UniFlow` |
| 停止 | `Stop-Service UniFlow` |
| 重启 | `Restart-Service UniFlow` |
| 状态 | `Get-Service UniFlow` |

## 配置

编辑 `C:\UniFlow\appsettings.json`：

- **Features** 段控制功能模块启停（修改后 3-5 秒热加载生效）
- **Aptio** 段配置仪器连接参数（修改后需重启服务）

## 直接运行（不装服务）

```powershell
cd C:\UniFlow
.\UniFlow.exe
```

## 注意事项

- 防火墙需放行 5100 端口
- 若 5100 端口被占用，修改 `appsettings.json` 中 `WebAdmin:Port`
- 配置热加载基于文件监控，部分环境可能需要重启生效