param(
    # publish.ps1 生成的发布目录；缺省取 artifacts 下最新的 RoomControl-*-win-x64。
    [string]$SourceDirectory = "",
    [string]$InstallDir = "C:\inetpub\RoomControl",
    [string]$DataDir = (Join-Path $env:ProgramData "RoomControl"),
    [string]$SiteName = "RoomControl",
    [string]$AppPoolName = "RoomControl",
    [int]$Port = 80,
    [string]$HostHeader = "",
    # 提供证书指纹时会额外创建 HTTPS 绑定；证书需已导入「本地计算机\个人」。
    [string]$CertificateThumbprint = "",
    [int]$HttpsPort = 443,
    [string]$AdminUsername = "admin",
    # 仅在数据库尚不存在（首次部署）时需要；已有数据库时忽略密码，不会重置账号。
    [System.Management.Automation.PSCredential]$Credential,
    # 排查启动问题时临时打开 ANCM 标准输出日志；正常运行时保持关闭。
    [switch]$EnableStdoutLog
)

# 任何一步失败都终止，避免留下半配置的站点。
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "请以管理员身份运行 PowerShell，再执行此脚本。"
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    # 与 publish.ps1 的输出约定保持一致，直接取最近一次打包结果。
    $candidate = Get-ChildItem (Join-Path $repositoryRoot "artifacts") -Directory `
        -Filter "RoomControl-*-win-x64" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $candidate) {
        throw "找不到发布目录。请先在项目根目录运行 .\publish.ps1，或用 -SourceDirectory 指定发布目录。"
    }
    $SourceDirectory = $candidate.FullName
}
$sourcePath = (Resolve-Path -LiteralPath $SourceDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "RoomControl.Api.dll"))) {
    throw "发布目录缺少 RoomControl.Api.dll：$sourcePath"
}
# web.config 使用进程内托管的 apphost；缺少 exe 时 IIS 会返回 500.0。
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "RoomControl.Api.exe"))) {
    throw "发布目录缺少 RoomControl.Api.exe。请用 .\publish.ps1（win-x64）重新打包后再部署。"
}
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "wwwroot\index.html"))) {
    throw "发布目录缺少 wwwroot\index.html。请使用 .\publish.ps1 打包，它会一并构建前端。"
}

if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
    throw "未安装 IIS 管理工具。请在「启用或关闭 Windows 功能」中勾选 Internet Information Services → Web 管理工具 → IIS 管理控制台。"
}
Import-Module WebAdministration

# ASP.NET Core 模块随 .NET Hosting Bundle 安装；只有 ASP.NET Core 运行时并不够。
$ancmPath = Join-Path $env:ProgramFiles "IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
if (-not (Test-Path -LiteralPath $ancmPath) -and
    $null -eq (Get-WebGlobalModule -Name "AspNetCoreModuleV2" -ErrorAction SilentlyContinue)) {
    throw "未找到 ASP.NET Core 模块（AspNetCoreModuleV2）。请安装 .NET 10 Hosting Bundle 并执行 iisreset 后重试。"
}

# SignalR 需要 WebSocket 协议；Linux 或未安装该功能时实时推送会退化为轮询。
$websocketRestartRequired = $false
try {
    $websocketFeature = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets
    if ($websocketFeature.State -ne "Enabled") {
        Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart | Out-Null
        $websocketRestartRequired = $true
    }
}
catch {
    Write-Warning "无法确认 IIS WebSocket 功能状态，请手工确认已安装 WebSocket 协议。"
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw "未找到 curl.exe，无法执行部署后的健康检查。请使用 Windows 10 1803 或更高版本。"
}

$dataPath = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')
if ($dataPath -eq [System.IO.Path]::GetPathRoot($dataPath).TrimEnd('\')) {
    throw "数据目录不能设置为磁盘根目录。"
}
$installPath = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$keysPath = Join-Path $dataPath "keys"
$logPath = Join-Path $dataPath "logs"
$databasePath = Join-Path $dataPath "roomcontrol.db"
New-Item -ItemType Directory -Force -Path $installPath, $dataPath, $keysPath, $logPath | Out-Null

function Read-AdminCredential {
    $username = Read-Host "管理员用户名 [$AdminUsername]"
    if (-not [string]::IsNullOrWhiteSpace($username)) {
        $script:AdminUsername = $username.Trim()
    }
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
    return [System.Management.Automation.PSCredential]::new($script:AdminUsername, $password)
}

# 应用池必须先于 ACL 创建，否则 "IIS AppPool\<名称>" 虚拟账号无法解析。
$existingPool = Get-IISAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
if ($null -eq $existingPool) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value "ApplicationPoolIdentity"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false

# SQLite 与数据保护密钥必须由应用池身份读写，否则会出现 unable to open database file。
$poolAccount = "IIS AppPool\$AppPoolName"
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $dataPath /inheritance:r /grant:r `
    "$($currentUser):(OI)(CI)F" `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    "${poolAccount}:(OI)(CI)M" /C | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "无法设置数据目录权限。请确认应用池「$AppPoolName」已存在并重试。"
}
# 已存在的数据库与日志改为继承新的目录规则，保证升级后应用池身份仍有写权限。
if (Get-ChildItem -LiteralPath $dataPath -Force -ErrorAction SilentlyContinue) {
    & icacls.exe (Join-Path $dataPath "*") /reset /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "无法修复数据目录内文件的访问权限。"
    }
}
& icacls.exe $installPath /grant "${poolAccount}:(OI)(CI)RX" /C | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "无法授予应用池读取安装目录的权限。"
}

# 首次部署需要管理员凭据；实际初始化在复制完文件后执行，避免用旧版本程序建库。
$needsInit = -not (Test-Path -LiteralPath $databasePath)
if ($needsInit -and $null -eq $Credential) {
    $Credential = Read-AdminCredential
}
if ($needsInit -and $null -ne $Credential) {
    # 无人值守安装时以 -Credential 中的用户名写入生产配置，避免与初始管理员不一致。
    $AdminUsername = $Credential.UserName
}
if ($needsInit -and $Credential.GetNetworkCredential().Password.Length -lt 12) {
    throw "管理员密码至少需要 12 个字符。"
}

# 覆盖代码文件前先停止应用池，避免正在运行的应用锁定 DLL。
$existingPool = Get-IISAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
if ($null -ne $existingPool -and $existingPool.State -ne "Stopped") {
    Stop-WebAppPool -Name $AppPoolName
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ((Get-IISAppPool -Name $AppPoolName).State -eq "Stopped") { break }
        Start-Sleep -Milliseconds 500
    }
}

if (-not [string]::Equals($sourcePath.TrimEnd('\'), $installPath, [StringComparison]::OrdinalIgnoreCase)) {
    # 前端文件名带内容哈希，先清空 wwwroot 以免升级后残留旧资源。
    $webRoot = Join-Path $installPath "wwwroot"
    if (Test-Path -LiteralPath $webRoot) {
        Remove-Item -LiteralPath $webRoot -Recurse -Force
    }
    Copy-Item -Path (Join-Path $sourcePath "*") -Destination $installPath -Recurse -Force
}
# 开发数据库不应进入安装目录。
Get-ChildItem -LiteralPath $installPath -Filter "roomcontrol.db*" -Force -ErrorAction SilentlyContinue |
    Remove-Item -Force

# 首次部署先以控制台方式初始化数据库与管理员，密码不会写入任何配置文件。
if ($needsInit) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $queryPort = $listener.LocalEndpoint.Port
    $listener.Stop()

    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $env:Database__Provider = "Sqlite"
    $env:ConnectionStrings__RoomControl = "Data Source=$databasePath"
    $env:Authentication__AdminUser = $Credential.UserName
    $env:Authentication__AdminPassword = $Credential.GetNetworkCredential().Password
    $env:DataProtection__KeysPath = $keysPath
    try {
        $initOut = Join-Path $logPath "init-out.log"
        $initError = Join-Path $logPath "init-error.log"
        $initProcess = Start-Process -FilePath "dotnet" `
            -ArgumentList @("`"$(Join-Path $installPath 'RoomControl.Api.dll')`"", "--urls", "http://127.0.0.1:$queryPort") `
            -WorkingDirectory $installPath `
            -PassThru -RedirectStandardOutput $initOut -RedirectStandardError $initError

        $initialized = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($initProcess.HasExited) {
                throw "数据库初始化失败。请查看日志：$initError"
            }
            $body = & curl.exe --silent --max-time 3 "http://127.0.0.1:$queryPort/health" 2>$null
            if ($body -match '"status"\s*:\s*"ok"') {
                $initialized = $true
                break
            }
            Start-Sleep -Seconds 1
        }
        if (-not $initialized) {
            throw "数据库初始化未在预期时间内完成。请查看日志：$initError"
        }
        Stop-Process -Id $initProcess.Id
        $initProcess.WaitForExit(10000) | Out-Null
    }
    finally {
        # 无论初始化是否成功，都清除本进程中的临时凭据。
        Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
        Remove-Item Env:Database__Provider -ErrorAction SilentlyContinue
        Remove-Item Env:ConnectionStrings__RoomControl -ErrorAction SilentlyContinue
        Remove-Item Env:Authentication__AdminUser -ErrorAction SilentlyContinue
        Remove-Item Env:Authentication__AdminPassword -ErrorAction SilentlyContinue
        Remove-Item Env:DataProtection__KeysPath -ErrorAction SilentlyContinue
    }
}

# IIS 下没有启动脚本注入环境变量，生产配置写在安装目录的 Production 配置文件中。
# 该文件每次部署都会重新生成，不依赖发布包内容。
$productionSettings = [ordered]@{
    Logging            = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning" } }
    Database           = @{ Provider = "Sqlite" }
    ConnectionStrings  = @{ RoomControl = "Data Source=$databasePath" }
    Authentication     = @{ AdminUser = $AdminUsername }
    DataProtection     = @{ KeysPath = $keysPath }
}
$productionSettings | ConvertTo-Json -Depth 5 |
    Set-Content -Path (Join-Path $installPath "appsettings.Production.json") -Encoding UTF8

# 由脚本生成 web.config，保证升级覆盖文件后仍然使用进程内托管和固定日志路径。
$stdoutLogValue = if ($EnableStdoutLog) { "true" } else { "false" }
$stdoutLogFile = [System.Security.SecurityElement]::Escape((Join-Path $logPath "stdout"))
$webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath=".\RoomControl.Api.exe" stdoutLogEnabled="$stdoutLogValue" stdoutLogFile="$stdoutLogFile" hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
"@
Set-Content -Path (Join-Path $installPath "web.config") -Value $webConfig -Encoding UTF8

# 应用池已经是「无托管代码」并保持常驻，避免定时控制和 SignalR 被空闲回收中断。

$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -eq $site) {
    $siteParameters = @{
        Name           = $SiteName
        PhysicalPath   = $installPath
        ApplicationPool = $AppPoolName
        Port           = $Port
    }
    if (-not [string]::IsNullOrWhiteSpace($HostHeader)) {
        $siteParameters.HostHeader = $HostHeader
    }
    New-Website @siteParameters | Out-Null
}
else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $installPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}
# 预加载需要 Application Initialization 功能；缺失时不影响站点运行，只影响首次请求速度。
try {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name preloadEnabled -Value $true
}
catch {
    Write-Warning "未能启用站点预加载，请确认已安装 IIS 的「应用程序初始化」功能。"
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $httpsBinding = Get-WebBinding -Name $SiteName -Protocol "https" -Port $HttpsPort -ErrorAction SilentlyContinue
    if ($null -eq $httpsBinding) {
        $bindingParameters = @{ Name = $SiteName; Protocol = "https"; Port = $HttpsPort }
        if (-not [string]::IsNullOrWhiteSpace($HostHeader)) {
            $bindingParameters.HostHeader = $HostHeader
        }
        New-WebBinding @bindingParameters | Out-Null
        $httpsBinding = Get-WebBinding -Name $SiteName -Protocol "https" -Port $HttpsPort
    }
    $httpsBinding.AddSslCertificate($CertificateThumbprint, "my")
}

if ((Get-Website -Name $SiteName).State -ne "Started") {
    Start-Website -Name $SiteName
}
Restart-WebAppPool -Name $AppPoolName

# 选一个可用绑定做健康检查；带主机名的绑定需要显式发送 Host 头。
$healthBinding = Get-WebBinding -Name $SiteName -Protocol "http" -ErrorAction SilentlyContinue |
    Select-Object -First 1
$healthScheme = "http"
if ($null -eq $healthBinding) {
    $healthBinding = Get-WebBinding -Name $SiteName -Protocol "https" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $healthScheme = "https"
}
$healthUrl = ""
$requestHost = ""
if ($null -ne $healthBinding) {
    $bindingParts = $healthBinding.bindingInformation -split ':'
    $healthPort = $bindingParts[1]
    $requestHost = $bindingParts[2]
    $healthUrl = "${healthScheme}://127.0.0.1:$healthPort/health"
}

$ready = $false
if (-not [string]::IsNullOrWhiteSpace($healthUrl)) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $curlArguments = @("--silent", "--insecure", "--max-time", "3")
        if (-not [string]::IsNullOrWhiteSpace($requestHost)) {
            $curlArguments += @("--header", "Host: $requestHost")
        }
        $curlArguments += $healthUrl
        $body = & curl.exe @curlArguments 2>$null
        if ($body -match '"status"\s*:\s*"ok"') {
            $ready = $true
            break
        }
        Start-Sleep -Seconds 1
    }
}

if (-not $ready) {
    throw "站点已创建，但健康检查未通过。请查看「事件查看器 → Windows 日志 → 应用程序」以及 $logPath 下的日志（可用 -EnableStdoutLog 重新部署获取详细输出）。"
}

$displayHost = if ([string]::IsNullOrWhiteSpace($HostHeader)) { "localhost" } else { $HostHeader }
$browserScheme = if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) { "https" } else { "http" }
$browserPort = if ($browserScheme -eq "https") { $HttpsPort } else { $Port }
$browserUrl = if ($browserPort -eq 80 -and $browserScheme -eq "http") {
    "http://$displayHost"
}
elseif ($browserPort -eq 443 -and $browserScheme -eq "https") {
    "https://$displayHost"
}
else {
    "${browserScheme}://${displayHost}:$browserPort"
}

if ($websocketRestartRequired) {
    Write-Warning "已启用 IIS WebSocket 功能，请执行 iisreset（或重启服务器）后再验证实时控制页面。"
}
Write-Host ""
Write-Host "RoomControl 已部署到 IIS：$browserUrl"
Write-Host "站点：$SiteName（应用池：$AppPoolName，进程内托管）"
Write-Host "应用目录：$installPath"
Write-Host "SQLite 数据库：$databasePath"
Write-Host "数据保护密钥：$keysPath"
Write-Host "如首次访问页面空白或报 500，可加 -EnableStdoutLog 重新运行本脚本，日志位于 $logPath。"
