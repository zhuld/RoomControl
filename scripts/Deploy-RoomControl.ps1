param(
    # 待部署的发布文件目录；发布包内的安装脚本默认使用脚本所在目录。
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDir = (Join-Path $env:ProgramFiles "RoomControl"),
    [string]$DataDir = (Join-Path $env:ProgramData "RoomControl"),
    [string]$ListenUrl = "http://0.0.0.0:5000",
    # 由 install.ps1 传入已校验的管理员凭据；缺省时交互输入（发布包内的安装脚本）。
    [System.Management.Automation.PSCredential]$Credential
)

# 任何非零错误都终止部署，避免失败后继续启动不完整的安装目录。
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
# 默认安装目录位于 Program Files，且稍后需要设置数据目录 ACL，因此要求管理员权限。
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行 PowerShell，再执行此安装脚本。"
}

if ([string]::IsNullOrWhiteSpace($SourceDirectory) -or -not (Test-Path -LiteralPath $SourceDirectory)) {
    throw "找不到发布文件目录：$SourceDirectory"
}
$sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "RoomControl.Api.dll"))) {
    throw "发布文件目录缺少 RoomControl.Api.dll：$sourcePath"
}

# 框架依赖发布要求目标机已安装运行时；缺失时直接提示，而不是等到启动阶段才失败。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet 命令。请安装 .NET 10 ASP.NET Core 运行时后重试。"
}
$runtimeList = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimeList -match 'Microsoft\.AspNetCore\.App 10\.')) {
    throw "需要 .NET 10 ASP.NET Core 运行时。请安装后重新运行此安装脚本。"
}

function Read-AdminCredential {
    # 空用户名使用默认值；去除首尾空格以避免创建后难以登录的账号。
    $adminUsername = Read-Host "管理员用户名 [admin]"
    if ([string]::IsNullOrWhiteSpace($adminUsername)) {
        $adminUsername = "admin"
    }
    $adminUsername = $adminUsername.Trim()

    # 密码不回显；仅在数据库首次创建管理员时使用，不写入启动脚本。
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
    return [System.Management.Automation.PSCredential]::new($adminUsername, $password)
}

if ($null -eq $Credential) {
    $Credential = Read-AdminCredential
}
$adminUsername = $Credential.UserName
$plainPassword = $Credential.GetNetworkCredential().Password
if ($plainPassword.Length -lt 12) {
    throw "管理员密码至少需要 12 个字符。"
}

$logDirectory = Join-Path $DataDir "logs"
# 先创建目标目录，确保后续复制与日志重定向有可写位置。
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir, $logDirectory | Out-Null
$dataPath = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')
# 拒绝磁盘根目录，避免 ACL 命令意外作用于整块磁盘。
if ($dataPath -eq [System.IO.Path]::GetPathRoot($dataPath).TrimEnd('\')) {
    throw "数据目录不能设置为磁盘根目录。"
}
# SQLite 文件包含账号哈希和站点数据，仅允许当前用户、SYSTEM 与本机管理员访问。
# 只对目录设置可继承规则：对文件同样附带 (OI)(CI) 会使其丢失权限，升级后数据库无法读取。
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $dataPath /inheritance:r /grant:r `
    "$($currentUser):(OI)(CI)F" `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw "无法限制 SQLite 数据目录的访问权限。" }

# 已有数据库与日志改为继承目录规则，修复旧安装残留的受限权限。
if (Get-ChildItem -LiteralPath $dataPath -Force -ErrorAction SilentlyContinue) {
    & icacls.exe (Join-Path $dataPath "*") /reset /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "无法修复 SQLite 数据目录的访问权限。" }
}

$installPath = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
# 发布包内的安装脚本与安装目录不同；同路径重复安装时跳过复制自身。
if (-not [string]::Equals($sourcePath.TrimEnd('\'), $installPath, [StringComparison]::OrdinalIgnoreCase)) {
    # 网页资源带内容哈希，先移除旧 wwwroot 避免升级后残留过期文件。
    $webRoot = Join-Path $InstallDir "wwwroot"
    if (Test-Path -LiteralPath $webRoot) {
        Remove-Item -LiteralPath $webRoot -Recurse -Force
    }
    Copy-Item -Path (Join-Path $sourcePath "*") -Destination $InstallDir -Recurse -Force
}

$startScript = Join-Path $InstallDir "Start-RoomControl.ps1"
$quotedDataDir = $DataDir.Replace("'", "''")
$quotedListenUrl = $ListenUrl.Replace("'", "''")
$quotedAdminUsername = $adminUsername.Replace("'", "''")
# 生成不含明文密码的重启脚本；数据库已有管理员时，空密码不会重置该账号。
$startScriptContent = @"
`$ErrorActionPreference = "Stop"
`$env:ASPNETCORE_ENVIRONMENT = "Production"
`$env:Database__Provider = "Sqlite"
`$env:ConnectionStrings__RoomControl = 'Data Source=$quotedDataDir\roomcontrol.db'
`$env:Authentication__AdminUser = '$quotedAdminUsername'
# 数据库首次启动时已创建管理员；重启时留空密码，避免把密码保存到磁盘。
`$env:Authentication__AdminPassword = ""
`$env:ASPNETCORE_URLS = '$quotedListenUrl'
Set-Location -LiteralPath "`$PSScriptRoot"
& dotnet "`$PSScriptRoot\RoomControl.Api.dll"
"@
Set-Content -Path $startScript -Value $startScriptContent -Encoding UTF8

# 生成停止脚本，只终止命令行指向本次安装路径的 dotnet 进程。
$stopScript = Join-Path $InstallDir "Stop-RoomControl.ps1"
$stopScriptContent = @"
`$ErrorActionPreference = "Stop"
`$installPath = (Resolve-Path -LiteralPath "`$PSScriptRoot\RoomControl.Api.dll").Path
# 按应用 DLL 路径匹配，避免误停机器上的其他 dotnet 进程。
`$process = Get-CimInstance Win32_Process | Where-Object {
    `$_.Name -eq "dotnet.exe" -and `$_.CommandLine -like "*`$installPath*"
} | Select-Object -First 1
if (`$null -eq `$process) {
    Write-Host "RoomControl 当前未运行。"
    exit 0
}
Stop-Process -Id `$process.ProcessId
Write-Host "RoomControl 已停止。"
"@
Set-Content -Path $stopScript -Value $stopScriptContent -Encoding UTF8

$databasePath = Join-Path $DataDir "roomcontrol.db"
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Database__Provider = "Sqlite"
$env:ConnectionStrings__RoomControl = "Data Source=$databasePath"
$env:Authentication__AdminUser = $adminUsername
$env:Authentication__AdminPassword = $plainPassword
$env:ASPNETCORE_URLS = $ListenUrl

# 子进程继承 SQLite、管理员初始化和监听配置；重定向日志便于诊断启动问题。
try {
    $apiDll = Join-Path $InstallDir "RoomControl.Api.dll"
    $stdoutLog = Join-Path $logDirectory "api-out.log"
    $stderrLog = Join-Path $logDirectory "api-error.log"
    $apiProcess = Start-Process -FilePath "dotnet" `
        -ArgumentList @("`"$apiDll`"", "--urls", "`"$ListenUrl`"") `
        -WorkingDirectory $InstallDir `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -PassThru
}
finally {
    # 无论进程是否成功启动，都清除安装进程中的临时配置和明文密码引用。
    Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Remove-Item Env:Database__Provider -ErrorAction SilentlyContinue
    Remove-Item Env:ConnectionStrings__RoomControl -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__AdminUser -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__AdminPassword -ErrorAction SilentlyContinue
    Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    $plainPassword = $null
}

$healthUrl = $ListenUrl.TrimEnd("/") -replace "^(https?://)(0\.0\.0\.0|\+|\*|\[::\]|::)", '$1localhost'
$ready = $false
# 0.0.0.0 等通配监听地址不能用于浏览器访问，健康检查和浏览器改用本机回环地址。
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($apiProcess.HasExited) {
        throw "RoomControl 启动失败。请查看日志：$stderrLog"
    }
    try {
        $health = Invoke-RestMethod -Uri "$healthUrl/health" -TimeoutSec 2
        if ($health.status -eq "ok") {
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $ready) {
    throw "RoomControl 未能在预期时间内启动。请查看日志：$stderrLog"
}

# 服务通过数据库健康检查后再打开网页，避免浏览器先于应用启动。
$browserUrl = $healthUrl
Start-Process $browserUrl
Write-Host ""
Write-Host "RoomControl 已安装并启动：$browserUrl"
Write-Host "SQLite 数据库：$databasePath"
Write-Host "标准输出日志：$stdoutLog"
Write-Host "错误日志：$stderrLog"
Write-Host "停止服务：powershell -ExecutionPolicy Bypass -File `"$stopScript`""
Write-Host "再次启动：powershell -ExecutionPolicy Bypass -File `"$startScript`""
