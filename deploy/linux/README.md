# UniFlow Linux 安装包

实验室自动化工作流服务 (.NET 10)，单文件部署，即插即用。

## 包含内容

| 文件 | 说明 |
|------|------|
| `UniFlow` | 单文件可执行程序（含 .NET 运行时） |
| `appsettings.json` | 默认配置文件 |
| `libe_sqlite3.so` | SQLite 原生库（已内嵌于主程序，无需单独部署） |
| `wwwroot/` | Web 管理界面资源 |
| `install.sh` | 一键安装脚本（systemd 服务） |
| `INSTALL.md` | 部署安装手册 |
| `CONFIG-REFERENCE.md` | 配置逐字段参考 |

## 系统要求

- Linux x64（glibc 2.17+），2GB 内存以上
- 无需预装 .NET 运行时（自包含）

## 安装

1. 解压安装包到临时目录
2. 按需修改 `appsettings.json`（见《配置》一节）
3. 进入目录并执行安装:

```bash
tar -xzf UniFlow-<版本>-linux-x64.tar.gz
cd uniflow
sudo ./install.sh            # 安装并启动（默认不开机自启）
sudo ./install.sh --enable   # 同时启用开机自启
```

安装脚本会将程序安装到 `/usr/share/uniflow`，并注册 systemd 服务 `uniflow`。

## 使用

浏览器访问 `http://<服务器IP>:5100` 打开管理界面。

| 操作 | 命令 |
|------|------|
| 启动 | `systemctl start uniflow` |
| 停止 | `systemctl stop uniflow` |
| 重启 | `systemctl restart uniflow` |
| 状态 | `systemctl status uniflow` |
| 日志 | `journalctl -fu uniflow` |

## 配置

编辑 `/usr/share/uniflow/appsettings.json`：

- **Features** 段控制功能模块启停（修改后 3-5 秒热加载生效）
- **Aptio** 段配置仪器连接参数（修改后需重启服务）

完整逐字段说明见 `CONFIG-REFERENCE.md`。

## 直接运行（不装服务）

```bash
cd uniflow
./UniFlow
```

## 卸载

```bash
sudo systemctl stop uniflow && sudo systemctl disable uniflow
sudo rm -f /etc/systemd/system/uniflow.service
sudo rm -rf /usr/share/uniflow
sudo userdel uniflow
```

## 注意事项

- 防火墙需放行 5100 端口
- 若 5100 端口被占用，修改 `appsettings.json` 中 `WebAdmin:Port`
- 配置热加载基于文件监控，部分环境可能需要重启生效
