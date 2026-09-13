#Requires -RunAsAdministrator
param([ValidateSet("install","uninstall","status","docker")][string]$Action = "install")

$ServiceName = "UniFlow"
$DisplayName = "UniFlow - Laboratory Automation Workflow Service (.NET 10)"
$Project = "UniFlow"
$SrcDir = Join-Path $PSScriptRoot "..\src"
$OutDir = Join-Path $PSScriptRoot "..\publish"

function Publish-Service {
    Write-Host "Building $Project for .NET 10..." -ForegroundColor Green
    & dotnet publish (Join-Path $SrcDir "$Project\$Project.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $OutDir $Project) 2>&1 | Select-Object -Last 3
    return Join-Path $OutDir $Project
}

switch ($Action) {
    "install" {
        $exePath = Join-Path (Join-Path $OutDir $Project) "$Project.exe"
        if (-not (Test-Path $exePath)) { $out = Publish-Service; $exePath = Join-Path $out "$Project.exe" }
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
            sc.exe delete $ServiceName 2>&1 | Out-Null
            Start-Sleep 2
        }
        New-Service -Name $ServiceName -DisplayName $DisplayName `
            -BinaryPathName "$exePath --contentRoot $exePath" -StartupType Automatic
        # 自动重启策略：故障后 60s 内重启（1st/2nd/3rd），计数器每日重置
        sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000
        Start-Service $ServiceName
        $s = Get-Service $ServiceName
        Write-Host "Service: $($s.Status)" -ForegroundColor Green
        Write-Host "Web Admin: http://localhost:5100" -ForegroundColor Cyan
    }
    "uninstall" {
        $s = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($s) { Stop-Service $ServiceName -Force; sc.exe delete $ServiceName; Write-Host "Removed" -ForegroundColor Green }
        else { Write-Host "Not found" -ForegroundColor Yellow }
    }
    "status" {
        $s = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($s) { Write-Host "$($s.Status)" -ForegroundColor $(if($s.Status -eq 'Running'){'Green'}else{'Red'}) }
        else { Write-Host "Not Installed" -ForegroundColor Yellow }
    }
    "docker" {
        Write-Host "Building Docker image..." -ForegroundColor Green
        & docker build -t uniflow:latest -f (Join-Path $PSScriptRoot "..\Dockerfile") (Join-Path $PSScriptRoot "..")
        Write-Host "Run with: docker compose up -d" -ForegroundColor Cyan
    }
}