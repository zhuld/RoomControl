param(
    # 应用文件、持久数据和监听地址可按部署环境覆盖。
    [string]$InstallDir = (Join-Path $env:ProgramFiles "RoomControl"),
    [string]$DataDir = (Join-Path $env:ProgramData "RoomControl"),
    [string]$ListenUrl = "http://0.0.0.0:5000"
)

# 任何非零错误都终止安装，避免失败后继续启动不完整的发布目录。
$ErrorActionPreference = "Stop"
# 使用脚本所在目录定位项目文件，因此可从任意当前目录调用本脚本。
$repositoryRoot = $PSScriptRoot
$apiProject = Join-Path $repositoryRoot "backend\RoomControl.Api\RoomControl.Api.csproj"
$webDirectory = Join-Path $repositoryRoot "web"
$deployScript = Join-Path $repositoryRoot "scripts\Deploy-RoomControl.ps1"
$buildRequirements = Join-Path $repositoryRoot "scripts\BuildRequirements.ps1"
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
# 默认安装目录位于 Program Files，且部署时需要设置数据目录 ACL，因此在构建前先检查权限。
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行 PowerShell，再执行此安装脚本。"
}

# 缺少任一项目文件通常表示脚本不是从仓库根目录执行。
if (-not (Test-Path $apiProject) -or -not (Test-Path $webDirectory) -or
    -not (Test-Path $deployScript) -or -not (Test-Path $buildRequirements)) {
    throw "请从项目根目录运行 install.ps1。"
}

. $buildRequirements
Assert-BuildRequirements

# 空用户名使用默认值；去除首尾空格以避免创建后难以登录的账号。
$adminUsername = Read-Host "管理员用户名 [admin]"
if ([string]::IsNullOrWhiteSpace($adminUsername)) {
    $adminUsername = "admin"
}
$adminUsername = $adminUsername.Trim()

# 密码不回显；构建前完成校验，避免长时间构建后才发现输入无效。
$password = Read-Host "设置管理员密码（至少 12 个字符）" -AsSecureString
$passwordAgain = Read-Host "再次输入管理员密码" -AsSecureString
$plainPassword = (New-Object System.Net.NetworkCredential("", $password)).Password
$plainPasswordAgain = (New-Object System.Net.NetworkCredential("", $passwordAgain)).Password
if ($plainPassword.Length -lt 12) {
    throw "管理员密码至少需要 12 个字符。"
}
if (-not [string]::Equals($plainPassword, $plainPasswordAgain, [StringComparison]::Ordinal)) {
    throw "两次输入的管理员密码不一致。"
}
# 凭据以 PSCredential 传给部署脚本，明文密码只在安装进程中短暂存在。
$credential = [System.Management.Automation.PSCredential]::new($adminUsername, $password)
$plainPassword = $null
$plainPasswordAgain = $null

# 发布暂存目录按当前进程 PID 命名，避免与并发安装冲突。
$publishDirectory = Join-Path $env:TEMP "RoomControl-publish-$PID"
try {
    # npm 命令需要在 web 目录执行；内层 finally 保证失败时也恢复原工作目录。
    Push-Location $webDirectory
    try {
        # 用锁文件还原依赖，确保构建结果与仓库依赖版本一致。
        Write-Host "正在安装前端依赖并构建网页..."
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci 执行失败。" }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "前端构建失败。" }
    }
    finally {
        Pop-Location
    }

    # 先输出到暂存目录，成功后再交给部署脚本复制到安装目录。
    Write-Host "正在发布后端..."
    & dotnet publish $apiProject -c Release -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "后端发布失败。" }

    # 将前端静态文件放入 API 的 wwwroot，由同一进程提供网页、API 和 SignalR。
    $webRoot = Join-Path $publishDirectory "wwwroot"
    New-Item -ItemType Directory -Force -Path $webRoot | Out-Null
    Copy-Item -Path (Join-Path $webDirectory "dist\*") -Destination $webRoot -Recurse -Force

    & $deployScript `
        -SourceDirectory $publishDirectory `
        -InstallDir $InstallDir `
        -DataDir $DataDir `
        -ListenUrl $ListenUrl `
        -Credential $credential
}
finally {
    # 部署完成后清理暂存目录；失败时同样清理，避免残留半成品发布文件。
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $credential = $null
}
