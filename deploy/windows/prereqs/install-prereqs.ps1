#Requires -RunAsAdministrator
param(
    [ValidateSet("X64", "X86")]
    [string]$Arch = "X64"
)

$urls = @{
    "X64" = "https://download.microsoft.com/download/3/5/1/3510E0F5-6C0F-4C4E-9B9A-6F6E8B7F8E8A/AccessDatabaseEngine_X64.exe"
    "X86" = "https://download.microsoft.com/download/3/5/1/3510E0F5-6C0F-4C4E-9B9A-6F6E8B7F8E8A/AccessDatabaseEngine.exe"
}

$outFile = Join-Path $PSScriptRoot "AccessDatabaseEngine_$Arch.exe"
$url = $urls[$Arch]

Write-Host "Downloading Access Database Engine ($Arch)..." -ForegroundColor Cyan
Write-Host "URL: $url" -ForegroundColor Gray

try {
    Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -ErrorAction Stop
    Write-Host "Downloaded: $outFile" -ForegroundColor Green
} catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    Write-Host "Please download manually from: https://www.microsoft.com/en-us/download/details.aspx?id=54920" -ForegroundColor Yellow
    exit 1
}

Write-Host "Installing..." -ForegroundColor Cyan
$proc = Start-Process -Wait -FilePath $outFile -ArgumentList "/quiet /norestart" -PassThru
if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
    Write-Host "Installation completed successfully." -ForegroundColor Green
    if ($proc.ExitCode -eq 3010) {
        Write-Host "A reboot is recommended." -ForegroundColor Yellow
    }
} else {
    Write-Host "Installation failed with exit code: $($proc.ExitCode)" -ForegroundColor Red
}