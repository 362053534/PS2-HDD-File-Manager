# 需要: PS2 toolchain + SDK，或 Docker 镜像 ghcr.io/362053534/ps2homebrew:main
param(
    [switch]$Docker,
    [string]$Image = "ghcr.io/362053534/ps2homebrew:main",
    [string]$Ps2Sdk = "C:\GitHub\ps2sdk",
    [string]$Ps2Dev = "/usr/local/ps2dev",
    # 容器内已安装的 SDK 路径（ps2dev 基镜像惯例）
    [string]$DockerPs2Sdk = "/usr/local/ps2dev/ps2sdk"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path (Join-Path $root "Makefile"))) {
    $root = Split-Path -Parent $root
}

if (-not (Test-Path (Join-Path $root "Makefile"))) {
    throw "Cannot locate ps2-server Makefile from $root"
}

function Find-DockerExe {
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe",
        "$env:ProgramFiles\Docker\Docker\resources\bin\com.docker.cli.exe",
        "$env:LOCALAPPDATA\Programs\Docker\Docker\resources\bin\docker.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

if ($Docker) {
    $dockerExe = Find-DockerExe
    if (-not $dockerExe) {
        throw @"
Docker 未安装或不在 PATH。

请先安装 Docker Desktop:
  winget install Docker.DockerDesktop
安装后重启并确认 `docker version` 可用，再运行:
  .\scripts\build.ps1 -Docker
"@
    }

    Write-Host "Building ApaNetServer.ELF via Docker"
    Write-Host "  image=$Image"
    Write-Host "  PS2SDK(in container)=$DockerPs2Sdk"
    Write-Host "  dir=$root"

    # 本地已有镜像则跳过 pull（避免 docker-credential-desktop 不在 PATH 时失败）
    & $dockerExe image inspect $Image 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & $dockerExe pull $Image
        if ($LASTEXITCODE -ne 0) {
            throw "docker pull failed: $Image"
        }
    } else {
        Write-Host "  using local image (skip pull)"
    }

    # 镜像已设置 PS2DEV/PS2SDK/PATH；避免在 PowerShell 里嵌多行 bash（CRLF 会破坏脚本）
    & $dockerExe run --rm `
        -e "PS2SDK=$DockerPs2Sdk" `
        -v "${root}:/src" `
        -w /src `
        $Image `
        sh -c 'make clean || true; make release; ls -la ApaNetServer.ELF'

    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed (exit $LASTEXITCODE)"
    }

    $elf = Join-Path $root "ApaNetServer.ELF"
    if (-not (Test-Path $elf)) {
        throw "Build finished but ELF not found: $elf"
    }
    Write-Host "OK: $elf"
    return
}

# --- 本机 / WSL 路径（原逻辑）---
if (-not (Test-Path $Ps2Sdk)) {
    throw "PS2SDK not found: $Ps2Sdk (或改用 -Docker)"
}

$irxProbe = Join-Path $Ps2Sdk "iop\irx\ps2fs.irx"
if (-not (Test-Path $irxProbe)) {
    throw @"
PS2SDK IRX missing: $irxProbe

可选方案:
  1) 用 Docker 镜像编译（推荐，无需本机工具链）:
       .\scripts\build.ps1 -Docker
  2) 在 WSL 里先编译/安装 forked SDK，再无 -Docker 构建。
"@
}

$wslSdk = ($Ps2Sdk -replace '\\', '/' -replace '^C:', '/mnt/c' -replace '^c:', '/mnt/c')
$wslDir = ($root -replace '\\', '/' -replace '^C:', '/mnt/c' -replace '^c:', '/mnt/c')

Write-Host "Building ApaNetServer.ELF"
Write-Host "  PS2SDK=$Ps2Sdk"
Write-Host "  PS2DEV=$Ps2Dev"
Write-Host "  dir=$root"

# 用 LF 单行，避免 CRLF；变量用单引号交给 bash 展开
$bash = "set -e; export PS2SDK='$wslSdk'; export PS2DEV='$Ps2Dev'; export PATH=`"`$PS2DEV/bin:`$PS2DEV/ee/bin:`$PS2DEV/iop/bin:`$PS2SDK/bin:`$PATH`"; command -v mips64r5900el-ps2-elf-gcc >/dev/null; command -v bin2c >/dev/null; cd '$wslDir'; make clean || true; make release PS2SDK='$wslSdk'; ls -la ApaNetServer.ELF"
wsl -e bash -lc $bash
