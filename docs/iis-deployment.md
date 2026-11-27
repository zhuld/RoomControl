# 在 IIS 中部署 RoomControl

RoomControl 是单进程 ASP.NET Core 应用：前端构建产物放在 `wwwroot`，REST API（`/api`）、SignalR（`/hubs/control`）与页面由同一个进程提供。IIS 下只需要**一个站点 + 一个应用池**，通过 ASP.NET Core 模块（ANCM）**进程内托管**，不需要配置 URL 重写或额外的反向代理。

本文与 [Windows 一键部署](../README.md#windows-一键部署sqlite)、[打包发布](../README.md#打包发布离线部署包) 二选一：同一份 SQLite 数据库同一时间只能由一个进程写入，不要让 IIS 站点和 `Start-RoomControl.ps1` 启动的控制台进程同时运行。

## 1. 前置条件

- Windows 10 1803+ 或 Windows Server 2016+（IIS 10）。
- 已启用 IIS，并包含以下功能：
  - Web 服务器 → 常见 HTTP 功能
  - Web 服务器 → 应用程序开发 → **WebSocket 协议**（SignalR 实时推送必需）
  - Web 服务器 → 应用程序开发 → 应用程序初始化（可选，用于站点常驻/预热）
  - Web 管理工具 → IIS 管理控制台
- **.NET 10 Hosting Bundle**（不是 ASP.NET Core Runtime）。只有运行时不会安装 ASP.NET Core 模块，站点会直接返回 500.31。下载地址：<https://dotnet.microsoft.com/download/dotnet/10.0>，选择 *Hosting Bundle*。
- 若使用 SQLite（本文默认），无需安装数据库服务器。

Windows 10/11 可用（管理员 PowerShell）：

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -All -NoRestart
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -All -NoRestart
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -All -NoRestart
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementConsole -All -NoRestart
```

Windows Server 可用：

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-AppInit, Web-Mgmt-Console
```

安装 Hosting Bundle 或启用新功能后重启 IIS：

```powershell
iisreset
```

检查 ASP.NET Core 模块是否就绪：

```powershell
Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
& "$env:windir\system32\inetsrv\appcmd.exe" list modules | Select-String AspNetCoreModuleV2
```

## 2. 生成发布文件

在项目根目录（需要 .NET 10 SDK 与 Node.js 20.19+）：

```powershell
.\publish.ps1
```

输出目录 `artifacts\RoomControl-<版本>-win-x64\` 即 IIS 的站点物理路径来源，包含：

- `RoomControl.Api.dll`、`RoomControl.Api.exe`（进程内托管的 apphost）
- `web.config`（`hostingModel="inprocess"`）
- `wwwroot\index.html` 等前端静态文件

> 不要手工把 `web` 目录或数据库文件拷进站点目录，前端必须以构建产物进入 `wwwroot`。

## 3. 一键部署（推荐）

以管理员身份打开 PowerShell，在项目根目录运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Deploy-RoomControl-IIS.ps1 -Port 80 -HostHeader room.example.com
```

首次运行会提示设置管理员用户名和至少 12 位密码。脚本会：

1. 校验 Hosting Bundle/ANCM 与 WebSocket 功能；
2. 创建应用池与站点目录；
3. 授予应用池对安装目录的读权限、对 `C:\ProgramData\RoomControl` 的读写权限；
4. 用临时环境变量启动一次程序完成数据库/管理员初始化（密码不写入任何配置文件）；
5. 生成 `appsettings.Production.json` 与 `web.config`；
6. 应用池设为无托管代码、常驻、无空闲回收；
7. 启动站点并轮询 `/health`，失败时给出日志位置。

常用参数：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `-SourceDirectory` | `artifacts` 下最新的 `RoomControl-*-win-x64` | 发布目录 |
| `-InstallDir` | `C:\inetpub\RoomControl` | 站点物理路径 |
| `-DataDir` | `C:\ProgramData\RoomControl` | SQLite、日志、数据保护密钥 |
| `-SiteName` / `-AppPoolName` | `RoomControl` | 站点与应用池名称 |
| `-Port` / `-HostHeader` | `80` / 空 | HTTP 绑定；空主机名为“所有未匹配主机” |
| `-CertificateThumbprint` / `-HttpsPort` | 空 / `443` | 额外创建 HTTPS 绑定，证书需已导入“本地计算机 → 个人” |
| `-Credential` | 空 | 首次部署的管理员凭据，便于无人值守安装 |
| `-EnableStdoutLog` | 关闭 | 临时打开 ANCM 标准输出日志，用于排查启动失败 |

带 HTTPS 的示例（证书指纹可在 `certlm.msc` 中查看）：

```powershell
.\scripts\Deploy-RoomControl-IIS.ps1 -HostHeader room.example.com -CertificateThumbprint 1A2B3C4D5E6F...
```

升级：重新执行 `.\publish.ps1`，再运行同一部署脚本即可；脚本会停止应用池、覆盖程序文件、保留数据库并重新做健康检查。若站点名已存在，脚本只更新物理路径与应用池，不会改动已有绑定。

## 4. 手工配置

不使用脚本时按下面步骤配置，效果与脚本一致。

### 4.1 复制发布文件

```powershell
$install = 'C:\inetpub\RoomControl'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\artifacts\RoomControl-*-win-x64\* $install -Recurse -Force
```

### 4.2 目录权限

应用池使用 `ApplicationPoolIdentity` 时，运行身份是虚拟账号 `IIS AppPool\<应用池名>`。**这是 IIS 部署最容易出错的一步**：[Deploy-RoomControl.ps1](../scripts/Deploy-RoomControl.ps1) 为控制台部署只授权了当前用户、`SYSTEM` 和本机管理员，直接复用会导致 `SQLite Error 14: unable to open database file` 或数据保护密钥无法写入。

```powershell
$install = 'C:\inetpub\RoomControl'
$data = 'C:\ProgramData\RoomControl'
New-Item -ItemType Directory -Force "$data\logs", "$data\keys" | Out-Null

# 程序目录只读
icacls $install /grant "IIS AppPool\RoomControl:(OI)(CI)RX"

# 数据目录：当前用户/管理员完全控制，应用池可修改（SQLite + 密钥环 + 日志）
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls $data /inheritance:r /grant:r `
    "$($currentUser):(OI)(CI)F" `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    "IIS AppPool\RoomControl:(OI)(CI)M"
```

### 4.3 创建应用池

```powershell
Import-Module WebAdministration
New-WebAppPool -Name RoomControl

Set-ItemProperty IIS:\AppPools\RoomControl -Name managedRuntimeVersion -Value ''
Set-ItemProperty IIS:\AppPools\RoomControl -Name managedPipelineMode -Value Integrated
Set-ItemProperty IIS:\AppPools\RoomControl -Name processModel.identityType -Value ApplicationPoolIdentity
Set-ItemProperty IIS:\AppPools\RoomControl -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\RoomControl -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\AppPools\RoomControl -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
```

必须使用“无托管代码”应用池。`AlwaysRunning` 与关闭空闲/定时回收是业务要求：Quartz 定时控制任务和 SignalR 长连接都依赖进程常驻，默认 20 分钟空闲回收会断开实时连接并导致定时任务漏执行。若启用站点预加载还需安装“应用程序初始化”功能：

```powershell
Set-ItemProperty IIS:\Sites\RoomControl -Name preloadEnabled -Value $true
```

### 4.4 应用配置

IIS 下没有启动脚本注入环境变量，配置放在站点的 `appsettings.Production.json` 中（`ASPNETCORE_ENVIRONMENT` 未设置时默认为 `Production`）：

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "Database": { "Provider": "Sqlite" },
  "ConnectionStrings": { "RoomControl": "Data Source=C:\\ProgramData\\RoomControl\\roomcontrol.db" },
  "Authentication": { "AdminUser": "admin" },
  "DataProtection": { "KeysPath": "C:\\ProgramData\\RoomControl\\keys" }
}
```

| 配置项 | 说明 |
| --- | --- |
| `Database:Provider` | `Sqlite` 或 `PostgreSQL` |
| `ConnectionStrings:RoomControl` | 必填；SQLite 使用绝对路径 |
| `Authentication:AdminUser` | 初始管理员用户名 |
| `Authentication:AdminPassword` | **仅首次启动需要**；数据库已有账号后请删除或留空，程序不会重置已有密码 |
| `DataProtection:KeysPath` | 密钥环目录；不配置时使用应用池用户的 `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`，应用池身份变化或回收后可能导致所有登录 Cookie 失效 |

也可以把配置写进 `web.config`（等价，且升级覆盖文件后需要重新添加）：

```xml
<aspNetCore processPath=".\RoomControl.Api.exe" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="Database__Provider" value="Sqlite" />
    <environmentVariable name="ConnectionStrings__RoomControl" value="Data Source=C:\ProgramData\RoomControl\roomcontrol.db" />
    <environmentVariable name="Authentication__AdminUser" value="admin" />
    <environmentVariable name="DataProtection__KeysPath" value="C:\ProgramData\RoomControl\keys" />
  </environmentVariables>
</aspNetCore>
```

不要把 `ASPNETCORE_URLS` 写进 IIS 站点的配置：进程内托管时监听地址由 IIS 决定，该变量无效。

首次启动前可先在控制台完成数据库与管理员初始化（密码不落盘）：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__RoomControl = 'Data Source=C:\ProgramData\RoomControl\roomcontrol.db'
$env:Authentication__AdminUser = 'admin'
$env:Authentication__AdminPassword = '至少12位的初始密码'
$env:DataProtection__KeysPath = 'C:\ProgramData\RoomControl\keys'
dotnet .\RoomControl.Api.dll --urls http://127.0.0.1:5123
# 看到 /health 正常后按 Ctrl+C 停止；之后启动 IIS 站点不会再重置密码
```

### 4.5 创建站点与绑定

```powershell
New-Website -Name RoomControl -PhysicalPath C:\inetpub\RoomControl `
    -ApplicationPool RoomControl -Port 80 -HostHeader room.example.com
```

HTTPS（证书需已导入“本地计算机 → 个人”）：

```powershell
New-WebBinding -Name RoomControl -Protocol https -Port 443 -HostHeader room.example.com
(Get-WebBinding -Name RoomControl -Protocol https -Port 443).AddSslCertificate('<证书指纹>', 'my')
```

- 80 端口已被 `Default Web Site` 占用时，要么使用 `-HostHeader` 区分，要么先停止默认站点。
- 建议只保留 HTTPS 绑定或额外配置 HTTP→HTTPS 跳转；应用使用 `SameAsRequest` 的 Cookie 安全策略，HTTPS 访问时登录 Cookie 会自动带 `Secure`。
- 进程内托管时客户端 IP 由 IIS 直接传给应用，不需要在应用侧配置 `ForwardedHeaders` 的信任代理。

### 4.6 启动并完成首次登录

```powershell
Start-Website -Name RoomControl
Start-Process http://room.example.com
```

用初始化时设置的管理员登录，然后删除 `appsettings.Production.json` 中的 `Authentication:AdminPassword`（如果写过）。

## 5. 验证

```powershell
curl.exe http://127.0.0.1/health
# {"status":"ok","database":"ok"}
```

- 浏览器打开站点，登录页可正常显示品牌信息；
- 浏览器开发者工具 → Network → WS，`/hubs/control` 应返回 `101 Switching Protocols`；
- 页面里的教室状态能实时刷新；
- 在 IIS 管理器中回收应用池后，已登录会话仍有效（密钥环已持久化），定时任务在回收后也能继续执行。

## 6. 运维要点

- **备份**：定期备份 `C:\ProgramData\RoomControl\roomcontrol.db`（以及 `-wal`/`-shm` 文件）与 `keys` 目录；`keys` 丢失会导致所有登录失效。
- **日志**：业务审计日志保存在数据库；启动/异常日志默认进入 Windows 事件查看器。需要详细输出时用 `-EnableStdoutLog` 重新部署，ANCM 日志写入 `C:\ProgramData\RoomControl\logs\stdout*.log`，排查完请关闭。
- **升级**：停止应用池 → 覆盖发布文件（或重跑部署脚本）→ 启动应用池。数据库结构由程序启动时自动补齐，无需手工迁移。
- **防火墙**：如需要允许其他电脑访问：

  ```powershell
  New-NetFirewallRule -DisplayName "RoomControl HTTP" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
  New-NetFirewallRule -DisplayName "RoomControl HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
  ```

- 仅建议在可信内网或 HTTPS 后运行；不要将纯 HTTP 站点直接暴露到公网。

## 7. 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| HTTP 500.31 | 未安装 ASP.NET Core 模块。安装 .NET 10 **Hosting Bundle** 后执行 `iisreset`。 |
| HTTP 500.30 | 进程内启动失败。打开 `-EnableStdoutLog` 查看日志，多半是连接串、目录权限或端口配置错误。 |
| HTTP 500.0 / 502.5 | `processPath` 指向的 `RoomControl.Api.exe` 不存在，或发布目录不完整。用 `publish.ps1` 重新打包。 |
| HTTP 500.19 | `web.config` 配置节无法识别，通常是 Hosting Bundle/ANCM 未安装或配置文件损坏。 |
| `SQLite Error 14: unable to open database file` | 应用池身份对数据目录没有写权限，按 4.2 授权 `IIS AppPool\<应用池名>:(OI)(CI)M`。 |
| `database is locked` | 同一个数据库被两个进程同时写入（IIS 站点与 `Start-RoomControl.ps1` 控制台进程），停止其中一个。 |
| 回收应用池后需要重新登录 | 未配置 `DataProtection:KeysPath`，或密钥目录对应用池不可写。按 4.4 配置并授权。 |
| `/hubs/control` 404，或 WS 一直失败 | 未安装 IIS WebSocket 协议功能；安装后 `iisreset`。若前面还有 Nginx/ARR，也需允许 WebSocket 升级。 |
| 定时任务不执行 | 应用池被空闲回收或定时回收。设置 `startMode=AlwaysRunning`、`idleTimeout=00:00:00`、`periodicRestart.time=00:00:00`，并启用站点预加载。 |
| 页面能打开但接口 401 | 使用 HTTPS 访问，或检查浏览器是否拦截了 Cookie；反向代理场景下需转发 `X-Forwarded-Proto`。 |
| 站点无法启动，提示端口/绑定冲突 | 80/443 已被其他站点占用。改用主机名绑定或停止冲突站点。 |

## 8. 与一键脚本的关系

- `install.ps1` / `Install-RoomControl.ps1` 以控制台进程监听 `http://0.0.0.0:5000` 并生成 `Start-RoomControl.ps1`，适合不使用 IIS 的机器；迁移到 IIS 前先运行 `Stop-RoomControl.ps1`，避免两个进程写同一个 SQLite 文件。
- 迁移时可以继续使用 `C:\ProgramData\RoomControl\roomcontrol.db`，只需按 4.2–4.4 授予应用池权限并配置路径。
