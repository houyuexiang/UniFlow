# UniFlow Windows Uninstall Script
# Run PowerShell as Administrator, then execute: .\uninstall.ps1
# Stops and removes the UniFlow Windows service.

$ErrorActionPreference = 'Stop'
$SERVICE_NAME = 'UniFlow'

function Write-Info { Write-Host "[INFO] $args" -ForegroundColor Green }
function Write-Warn { Write-Host "[WARN] $args" -ForegroundColor Yellow }
function Write-Error { Write-Host "[ERROR] $args" -ForegroundColor Red }

# Check for administrator privileges
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Please run this script as Administrator (right-click -> Run as administrator)"
    exit 1
}

Write-Info "Uninstalling UniFlow service..."
if (Get-Service $SERVICE_NAME -ErrorAction SilentlyContinue) {
    Stop-Service $SERVICE_NAME -Force
    sc.exe delete $SERVICE_NAME
    Write-Info "Service removed: $SERVICE_NAME"
} else {
    Write-Warn "Service not found: $SERVICE_NAME"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " UniFlow Uninstall Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Note: The files under install dir are kept."
Write-Host " To fully remove: delete the install directory manually."
Write-Host "========================================" -ForegroundColor Cyan