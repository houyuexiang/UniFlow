# UniFlow Windows Prerequisites

## WorkListCleaner Module

If you use the `ImmuliteWorkOrderClean` feature (MS Access database), you need to install:

**Microsoft Access Database Engine 2016 Redistributable**

### Automatic Installation

Run as administrator:

```powershell
.\install-prereqs.ps1
```

### Manual Installation

1. Open the download page: https://www.microsoft.com/en-us/download/details.aspx?id=54920
2. Click the **Download** button
3. Select `AccessDatabaseEngine_X64.exe` (64-bit) or `AccessDatabaseEngine.exe` (32-bit)
4. Click **Next** to download
5. Double-click the installer to install

### Notes

- On 64-bit systems, install the 64-bit version, otherwise UniFlow's OleDb connection will fail
- If 32-bit Office is installed, only the 32-bit Access Database Engine can be installed
- No reboot required after installation