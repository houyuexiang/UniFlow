# UniFlow Windows Install Script
# Run PowerShell as Administrator, then execute: .\install.ps1
# Installs UniFlow as a Windows service (LocalSystem account, manual start).

$ErrorActionPreference = 'Stop'
$APP_DIR = $PSScriptRoot
$SERVICE_NAME = 'UniFlow'
$APP_EXE = Join-Path $APP_DIR 'UniFlow.exe'

function Write-Info { Write-Host "[INFO] $args" -ForegroundColor Green }
function Write-Warn { Write-Host "[WARN] $args" -ForegroundColor Yellow }
function Write-Error { Write-Host "[ERROR] $args" -ForegroundColor Red }

# Check for administrator privileges
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Please run this script as Administrator (right-click -> Run as administrator)"
    exit 1
}

# Check files
if (-not (Test-Path $APP_EXE)) {
    Write-Error "UniFlow.exe not found. Please run from the correct directory."
    exit 1
}

# Create Windows service (runs as LocalSystem account)
Write-Info "Creating service $SERVICE_NAME ..."
if (Get-Service $SERVICE_NAME -ErrorAction SilentlyContinue) {
    Stop-Service $SERVICE_NAME -Force
    sc.exe delete $SERVICE_NAME
}

sc.exe create $SERVICE_NAME binPath= "`"$APP_EXE`"" obj= LocalSystem start= auto DisplayName= "UniFlow Laboratory Automation Service"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create service"
    exit 1
}

sc.exe description $SERVICE_NAME "Laboratory Automation Workflow Service (.NET 10)"

# Service Recovery: 进程异常退出(含在线升级 Exit(1))后自动拉起，1天无失败则重置计数
sc.exe failure $SERVICE_NAME reset= 86400 actions= restart/5000/restart/10000/restart/15000
sc.exe failure-flag $SERVICE_NAME 1
sc.exe config $SERVICE_NAME start= demand obj= LocalSystem

Write-Info "Starting service..."
Start-Service $SERVICE_NAME
Start-Sleep -Seconds 5

# Health check
$healthy = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:5100/api/health" -UseBasicParsing -TimeoutSec 3
        if ($resp.StatusCode -eq 200) { $healthy = $true; break }
    } catch {}
    Start-Sleep -Seconds 2
}

if ($healthy) {
    Write-Info "UniFlow started successfully: http://localhost:5100"
    Write-Info "Web Admin: http://<this-IP>:5100"
} else {
    Write-Warn "Service created but health check failed. Check the Event Viewer."
    Write-Warn "Note: auto-start is NOT enabled. To enable, run .\install-enable.ps1"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " UniFlow Installation Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Service: $SERVICE_NAME"
Write-Host " Install Dir: $APP_DIR"
Write-Host " Config File: $APP_DIR\appsettings.json"
Write-Host " Web Admin: http://<this-IP>:5100"
Write-Host ""
Write-Host " Common Commands:"
Write-Host "   Start-Service UniFlow          Start"
Write-Host "   Stop-Service UniFlow           Stop"
Write-Host "   Restart-Service UniFlow        Restart"
Write-Host "   Get-Service UniFlow            Status"
Write-Host "   .\install-enable.ps1           Install with auto-start"
Write-Host "   .\uninstall.ps1                Uninstall"
Write-Host "========================================" -ForegroundColor Cyan