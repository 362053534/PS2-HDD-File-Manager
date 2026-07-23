#Requires -Version 5.0
# 手动编译 Release（兼容 Windows PowerShell 5.1 / 右键“使用 PowerShell 运行”）：
# 1) 默认从 pfsshell 源码编译，产物直接放入 tools（不经过 BAT）
# 2) 编译本 C# 项目，更新 bin\Release\net8.0-windows
# 3) 打包 zip（不含 pdb）
param(
    [switch]$SkipPfsshell,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$exitCode = 0

# 必须在脚本作用域读取；函数内的 $MyInvocation 不可靠。
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root) -and $MyInvocation.MyCommand.Path) {
    $root = Split-Path -Parent -Path $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrEmpty($root) -and $PSCommandPath) {
    $root = Split-Path -Parent -Path $PSCommandPath
}
if ([string]::IsNullOrEmpty($root)) {
    $root = (Get-Location).Path
}

function Test-NativeSuccess {
    param($ExitCodeValue)
    if ($null -eq $ExitCodeValue) {
        return $true
    }
    return (([int]$ExitCodeValue) -eq 0)
}

try {
    Set-Location -LiteralPath $root

    $csproj = Join-Path $root 'PS2-HDD-File-Manager.csproj'
    $outDir = Join-Path $root 'bin\Release\net8.0-windows'
    $zipDir = Join-Path $root 'bin\Release'
    $zipPath = Join-Path $zipDir 'PS2-HDD-File-Manager.zip'
    $toolsSrc = Join-Path $root '..\PFS-BatchKit-Manager\BAT'
    $toolsDst = Join-Path $outDir 'tools'
    $batToolNames = @('hdl_dump.exe', 'wnbd-client.exe', 'libwnbd.dll')

    $pfsshellBuildScript = 'C:\GitHub\BuildTools\scripts\build-pfsshell-win32.ps1'
    $pfsshellBuiltExe = 'C:\GitHub\pfsshell\build-local-win32\pfsshell.exe'
    $pfsshellToolsExe = Join-Path $toolsDst 'pfsshell.exe'

    if (-not (Test-Path -LiteralPath $csproj)) {
        throw "未找到项目文件：$csproj"
    }

    if (-not $SkipPfsshell) {
        if (-not (Test-Path -LiteralPath $pfsshellBuildScript)) {
            throw "未找到 pfsshell 编译脚本：$pfsshellBuildScript"
        }

        Write-Host "Building pfsshell from source..."
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pfsshellBuildScript
        if (-not (Test-NativeSuccess -ExitCodeValue $LASTEXITCODE)) {
            throw "pfsshell 编译失败，退出码：$LASTEXITCODE"
        }
    }

    if (-not (Test-Path -LiteralPath $pfsshellBuiltExe)) {
        throw "未找到 pfsshell 编译产物：$pfsshellBuiltExe（请先编译，或去掉 -SkipPfsshell）"
    }

    Write-Host "Building Release -> $outDir"
    & dotnet build $csproj -c Release --nologo
    if (-not (Test-NativeSuccess -ExitCodeValue $LASTEXITCODE)) {
        throw "编译失败，退出码：$LASTEXITCODE"
    }

    if (-not (Test-Path -LiteralPath $outDir)) {
        throw "编译成功，但未找到输出目录：$outDir"
    }

    if (-not (Test-Path -LiteralPath $toolsDst)) {
        New-Item -ItemType Directory -Force -Path $toolsDst | Out-Null
    }

    foreach ($name in $batToolNames) {
        $src = Join-Path $toolsSrc $name
        if (-not (Test-Path -LiteralPath $src)) {
            throw "未找到工具文件：$src"
        }
        Copy-Item -LiteralPath $src -Destination (Join-Path $toolsDst $name) -Force
    }

    Copy-Item -LiteralPath $pfsshellBuiltExe -Destination $pfsshellToolsExe -Force
    Write-Host "Updated tools\pfsshell.exe <- $pfsshellBuiltExe"

    Write-Host "Packaging zip from net8.0-windows..."
    if (-not (Test-Path -LiteralPath $zipDir)) {
        New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $stage = Join-Path $env:TEMP ('pfs-zip-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    try {
        $items = @(Get-ChildItem -LiteralPath $outDir -Force | Where-Object { $_.Extension -ne '.pdb' })
        foreach ($item in $items) {
            $dest = Join-Path $stage $item.Name
            Copy-Item -LiteralPath $item.FullName -Destination $dest -Recurse -Force
        }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw "打包失败，未生成：$zipPath"
    }

    Write-Host "Done."
    Write-Host "Output: $outDir"
    Write-Host "Zip:    $zipPath"
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host ("失败：" + $_.Exception.Message) -ForegroundColor Red
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
        Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkRed
    }
}
finally {
    if (-not $NoPause) {
        Write-Host ""
        try {
            Read-Host "Press Enter to exit / 按 Enter 退出"
        }
        catch {
            Start-Sleep -Seconds 8
        }
    }
}

exit $exitCode