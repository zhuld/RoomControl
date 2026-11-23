# 源码构建与打包共用的工具检查：缺少依赖时立即提示，避免后续命令才失败。
function Assert-BuildRequirements {
    foreach ($name in @("dotnet", "npm", "node")) {
        if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
            throw "未找到命令 $name。请先安装对应的开发工具后重试。"
        }
    }

    # 从已安装 SDK 中取最高主版本，确保项目可用 .NET 10 编译。
    $dotnetVersion = (& dotnet --list-sdks | ForEach-Object {
        if ($_ -match '^(\d+)\.') { [int]$Matches[1] }
    } | Measure-Object -Maximum).Maximum
    if ($null -eq $dotnetVersion -or $dotnetVersion -lt 10) {
        throw "需要 .NET 10 SDK。请安装后重新运行此脚本。"
    }

    # node --version 的主、次版本用于匹配项目所需的最低 Node.js 20.19。
    $nodeVersionOutput = & node --version
    if ($LASTEXITCODE -ne 0 -or $nodeVersionOutput -notmatch '^v(\d+)\.(\d+)') {
        throw "无法读取 Node.js 版本。"
    }
    $nodeMajor = [int]$Matches[1]
    $nodeMinor = [int]$Matches[2]
    if ($nodeMajor -lt 20 -or ($nodeMajor -eq 20 -and $nodeMinor -lt 19)) {
        throw "需要 Node.js 20.19 或更高版本。"
    }
}
