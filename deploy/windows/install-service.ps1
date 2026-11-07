#Requires -RunAsAdministrator
param(
    [ValidateSet("install","uninstall","status","docker")][string]$Action = "install",
    # 显式跳过 publish（仅用于"只想重装已有二进制"的现场场景）。
    # 默认 = 始终 publish —— 旧逻辑"exe 已存在就跳过 publish"会把旧二进制装成服务。
    [switch]$SkipPublish
)

$ServiceName = "UniFlow"
$DisplayName = "UniFlow - Laboratory Automation Workflow Service (.NET 10)"
$Project = "UniFlow"
# 源码目录：<repo>\code\src（本脚本在 <repo>\deploy\windows 下 → 需上两级）
# 旧写法 "..\src" 指向 <repo>\deploy\src —— 不存在，dotnet publish 必然失败
$SrcDir = Join-Path $PSScriptRoot "..\..\code\src"
# 发布输出目录：<repo>\release\publish（与 deploy/build.sh、.gitignore 的 release/publish/ 一致）
$OutDir = Join-Path $PSScriptRoot "..\..\release\publish"
$Csproj = Join-Path $SrcDir "$Project\$Project.csproj"

function Get-ProjectVersion {
    # 从 csproj 读 <Version>（发布版本）；读不到返回 "unknown"
    if (Test-Path $Csproj) {
        $m = Select-String -Path $Csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m -and $m.Matches.Count -gt 0) { return $m.Matches[0].Groups[1].Value }
    }
    return "unknown"
}

function Get-BinaryVersion {
    # 已发布 exe 的文件版本（读不到返回 "unknown"）
    param([string]$Path)
    if (Test-Path $Path) {
        try {
            $v = (Get-Item $Path).VersionInfo.FileVersion
            if ($v) { return $v }
        } catch { }
    }
    return "unknown"
}

function Publish-Service {
    # 发布并**校验**结果；任何一步失败都 exit 1（绝不返回半个结果让调用方继续装）。
    # 返回值 = 通过校验的发布目录（临时目录，正式目录由调用方在校验通过后替换）。
    if (-not (Test-Path $Csproj)) {
        Write-Host "Source project not found: $Csproj" -ForegroundColor Red
        Write-Host "Expected layout: <repo>\code\src\$Project\$Project.csproj" -ForegroundColor Yellow
        exit 1
    }
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

    # 发布到**临时目录**：publish 失败时不会覆盖正式目录里的旧 exe，
    # 校验通过后才替换正式目录 → 不可能出现"publish 失败却把旧二进制装成服务"。
    $stagingDir = Join-Path $OutDir "$Project.staging"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    Write-Host "Building $Project for .NET 10..." -ForegroundColor Green
    Write-Host "  project: $Csproj" -ForegroundColor DarkGray
    Write-Host "  output : $stagingDir" -ForegroundColor DarkGray

    $publishStarted = Get-Date
    # 注意：先把 publish 的输出整体捕获，再单独读 $LASTEXITCODE。
    # （直接管道到 Select-Object 时拿不到可靠的退出码 → publish 失败会被当成成功。）
    $publishOutput = & dotnet publish $Csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -o $stagingDir 2>&1
    $publishExitCode = $LASTEXITCODE
    $publishOutput | Select-Object -Last 5 | ForEach-Object { Write-Host $_ }

    # ★ 关键闸门：publish 失败必须**立即退出**。
    # 历史缺陷（复核 D3）：旧实现不检查 $LASTEXITCODE → publish 失败时旧 exe 仍在，
    # Test-Path $exePath 通过 → 继续 New-Service，把**旧二进制**装成服务，
    # 还打印 "Published … version" 看起来像成功了。
    if ($publishExitCode -ne 0) {
        Write-Host "dotnet publish FAILED (exit code $publishExitCode) - aborting, nothing installed." -ForegroundColor Red
        Write-Host "  project: $Csproj" -ForegroundColor Red
        Write-Host "  staging: $stagingDir" -ForegroundColor Red
        Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
        exit 1
    }

    # 校验产物：exe 必须真的存在。
    # 临时目录在 publish 前是**清空**的 → 这里的 exe 必然来自本次 publish（不是残留旧文件）。
    $stagedExe = Join-Path $stagingDir "$Project.exe"
    if (-not (Test-Path $stagedExe)) {
        Write-Host "Publish did not produce the expected executable:" -ForegroundColor Red
        Write-Host "  $stagedExe" -ForegroundColor Red
        Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
        exit 1
    }

    # 版本/时间戳只做**提示**（不作为失败条件）：
    # FileVersion 与 csproj <Version> 格式可能不同；增量编译时 exe 的时间戳也可能偏旧。
    $stagedExeItem = Get-Item $stagedExe
    Write-Host ("  built at : {0}  (publish started {1})" -f $stagedExeItem.LastWriteTime, $publishStarted) -ForegroundColor DarkGray

    return $stagingDir
}

switch ($Action) {
    "install" {
        $finalDir = Join-Path $OutDir $Project
        $exePath = Join-Path $finalDir "$Project.exe"
        $projectVersion = Get-ProjectVersion
        $stagedDir = $null

        if ($SkipPublish) {
            # 显式跳过 publish：必须已有 exe，且必须打印版本警告（否则会装上旧程序）
            if (Test-Path $exePath) {
                $existingVersion = Get-BinaryVersion $exePath
                Write-Host "WARNING: -SkipPublish specified - NOT rebuilding!" -ForegroundColor Yellow
                Write-Host "  installed binary : $exePath" -ForegroundColor Yellow
                Write-Host "  binary version   : $existingVersion" -ForegroundColor Yellow
                Write-Host "  project version  : $projectVersion" -ForegroundColor Yellow
                if ($existingVersion -ne $projectVersion -and $existingVersion -ne "unknown") {
                    Write-Host "  version mismatch - the installed binary may be STALE." -ForegroundColor Red
                }
            } else {
                Write-Host "-SkipPublish was given but no existing binary was found at:" -ForegroundColor Red
                Write-Host "  $exePath" -ForegroundColor Red
                Write-Host "Run without -SkipPublish to build and install." -ForegroundColor Yellow
                exit 1
            }
        } else {
            # 默认：始终 publish（即使 exe 已存在）——
            # 旧逻辑"exe 存在就跳过 publish" → 现场升级后装的仍是旧二进制。
            # Publish-Service 内部会校验 $LASTEXITCODE 与产物存在，失败即 exit 1。
            $out = Publish-Service
            $stagedDir = $out
            $stagedExe = Join-Path $out "$Project.exe"
            if (-not (Test-Path $stagedExe)) {
                Write-Host "Publish did not produce the expected executable:" -ForegroundColor Red
                Write-Host "  $stagedExe" -ForegroundColor Red
                exit 1
            }
            Write-Host "Published $Project version $(Get-BinaryVersion $stagedExe) (project version $projectVersion)" -ForegroundColor Green
        }

        # 停掉旧服务（正式目录里的 exe 可能正被进程占用 → 替换前必须先停）
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
            sc.exe delete $ServiceName 2>&1 | Out-Null
            Start-Sleep 3
        }

        if ($stagedDir) {
            # 用**本次** publish 的产物替换正式目录 → 装的一定是新二进制（不是残留旧文件）
            try {
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    try {
                        if (Test-Path $finalDir) { Remove-Item $finalDir -Recurse -Force -ErrorAction Stop }
                        break
                    } catch {
                        if ($attempt -ge 3) { throw }
                        Write-Host "Old output directory is still in use - retry in 2s (attempt $attempt/3)..." -ForegroundColor Yellow
                        Start-Sleep 2
                    }
                }
                Move-Item $stagedDir $finalDir -ErrorAction Stop
            } catch {
                Write-Host "Failed to replace the published output directory:" -ForegroundColor Red
                Write-Host "  $finalDir" -ForegroundColor Red
                Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
                Write-Host "Stop the running service manually, then re-run this script." -ForegroundColor Yellow
                exit 1
            }
            $exePath = Join-Path $finalDir "$Project.exe"
            if (-not (Test-Path $exePath)) {
                Write-Host "Publish did not produce the expected executable:" -ForegroundColor Red
                Write-Host "  $exePath" -ForegroundColor Red
                exit 1
            }
        }

        New-Service -Name $ServiceName -DisplayName $DisplayName `
            -BinaryPathName "$exePath --contentRoot $exePath" -StartupType Automatic
        # 自动重启策略：故障后 60s 内重启（1st/2nd/3rd），计数器每日重置
        sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000
        Start-Service $ServiceName
        $s = Get-Service $ServiceName
        Write-Host "Service: $($s.Status)" -ForegroundColor Green
        Write-Host "Installed binary version: $(Get-BinaryVersion $exePath)" -ForegroundColor Cyan
        Write-Host "Web Admin: http://localhost:5100" -ForegroundColor Cyan
    }
    "uninstall" {
        $s = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($s) { Stop-Service $ServiceName -Force; sc.exe delete $ServiceName; Write-Host "Removed" -ForegroundColor Green }
        else { Write-Host "Not Installed" -ForegroundColor Yellow }
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
