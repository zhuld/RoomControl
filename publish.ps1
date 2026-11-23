param(
    # 版本号用于发布目录与压缩包命名；缺省取 git describe 结果。
    [string]$Version = "",
    [string]$OutputDir = (Join-Path $PSScriptRoot "artifacts"),
    # 目标运行时决定压缩包里是否带平台可执行文件；发布包按框架依赖方式生成。
    [string]$Runtime = "win-x64",
    # 本机已安装前端依赖且无需重新还原时（例如正在运行 Vite 开发服务器）跳过 npm ci。
    [switch]$SkipNpmInstall
)

# 任何非零错误都终止打包，避免生成缺少前端或后端的半成品压缩包。
$ErrorActionPreference = "Stop"
$repositoryRoot = $PSScriptRoot
$apiProject = Join-Path $repositoryRoot "backend\RoomControl.Api\RoomControl.Api.csproj"
$webDirectory = Join-Path $repositoryRoot "web"
$deployScript = Join-Path $repositoryRoot "scripts\Deploy-RoomControl.ps1"
$buildRequirements = Join-Path $repositoryRoot "scripts\BuildRequirements.ps1"

# 缺少任一项目文件通常表示脚本不是从仓库根目录执行。
if (-not (Test-Path $apiProject) -or -not (Test-Path $webDirectory) -or
    -not (Test-Path $deployScript) -or -not (Test-Path $buildRequirements)) {
    throw "请从项目根目录运行 publish.ps1。"
}

. $buildRequirements
Assert-BuildRequirements

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& git -C $repositoryRoot describe --tags --always 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-Date -Format "yyyyMMdd"
    }
    $Version = $Version.Trim()
}

$packageName = "RoomControl-$Version-$Runtime"
$packageDirectory = Join-Path $OutputDir $packageName
$zipPath = Join-Path $OutputDir "$packageName.zip"
$stagingDirectory = Join-Path $env:TEMP "RoomControl-package-$PID"

# 发布目录可能已存在，先清空避免旧文件混入发布包。
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

try {
    # npm 命令需要在 web 目录执行；内层 finally 保证失败时也恢复原工作目录。
    Push-Location $webDirectory
    try {
        # 用锁文件还原依赖，确保构建结果与仓库依赖版本一致。
        Write-Host "正在安装前端依赖并构建网页..."
        if (-not $SkipNpmInstall) {
            & npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci 执行失败。" }
        }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "前端构建失败。" }
    }
    finally {
        Pop-Location
    }

    Write-Host "正在发布后端（框架依赖，$Runtime）..."
    & dotnet publish $apiProject -c Release -r $Runtime --self-contained false -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) { throw "后端发布失败。" }

    # 发布包只应包含程序文件，避免把本机开发数据库带进发布包。
    Get-ChildItem -LiteralPath $stagingDirectory -Filter "roomcontrol.db*" -Force -ErrorAction SilentlyContinue |
        Remove-Item -Force

    # EF Core Design 的构建主机只在设计时生成迁移用，运行期不需要。
    foreach ($designHost in @("BuildHost-net472", "BuildHost-netcore")) {
        $designHostPath = Join-Path $stagingDirectory $designHost
        if (Test-Path -LiteralPath $designHostPath) {
            Remove-Item -LiteralPath $designHostPath -Recurse -Force
        }
    }

    # 将前端静态文件放入 API 的 wwwroot，由同一进程提供网页、API 和 SignalR。
    $webRoot = Join-Path $stagingDirectory "wwwroot"
    New-Item -ItemType Directory -Force -Path $webRoot | Out-Null
    Copy-Item -Path (Join-Path $webDirectory "dist\*") -Destination $webRoot -Recurse -Force

    # 发布包自带部署脚本，目标机无需 .NET SDK 或 Node.js 即可安装。
    Copy-Item -LiteralPath $deployScript -Destination (Join-Path $stagingDirectory "Install-RoomControl.ps1") -Force

    # 分发的软件需附带许可证文件。
    $licensePath = Join-Path $repositoryRoot "License"
    if (Test-Path -LiteralPath $licensePath) {
        Copy-Item -LiteralPath $licensePath -Destination $stagingDirectory -Force
    }

    New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
    Copy-Item -Path (Join-Path $stagingDirectory "*") -Destination $packageDirectory -Recurse -Force
}
finally {
    # 无论成功与否都清理暂存目录，避免残留半成品文件。
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $zipPath -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Write-Host ""
Write-Host "发布目录：$packageDirectory"
Write-Host "压缩包：$zipPath"
Write-Host "SHA256：$hash"
Write-Host "目标机需预装 .NET 10 ASP.NET Core 运行时，解压后以管理员身份运行 Install-RoomControl.ps1。"
