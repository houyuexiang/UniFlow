# UniFlow Windows 前置依赖

## WorkListCleaner 模块

如果使用 `ImmuliteWorkOrderClean` 功能（MS Access 数据库），需要安装：

**Microsoft Access Database Engine 2016 Redistributable**

### 自动安装

以管理员身份运行：

```powershell
.\install-prereqs.ps1
```

### 手动安装

1. 打开下载页面：https://www.microsoft.com/en-us/download/details.aspx?id=54920
2. 点击 **Download** 按钮
3. 勾选 `AccessDatabaseEngine_X64.exe`（64位）或 `AccessDatabaseEngine.exe`（32位）
4. 点击 **Next** 下载
5. 双击安装包安装

### 注意事项

- 64 位系统请安装 64 位版本，否则 UniFlow 的 OleDb 连接会失败
- 如果已安装 Office 的 32 位版本，只能安装 32 位的 Access Database Engine
- 安装后无需重启即可使用