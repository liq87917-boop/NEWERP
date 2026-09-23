# ============================================================
# 外贸 ERP 系统 · 开发环境一键启动脚本
# 用法：在项目根目录执行  powershell -ExecutionPolicy Bypass -File .\start-dev.ps1
# 作用：校验 SDK → 还原依赖 → 以 Development 环境启动 WebAPI（http://localhost:5000）
param([switch]$ValidateEnvironmentOnly)

# ============================================================

$ErrorActionPreference = 'Stop'

# 统一使用 UTF-8 输出，避免中文日志乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 切换到脚本所在目录（即解决方案根目录）
Set-Location -Path $PSScriptRoot

$projectPath = 'src\ERP.Api\ERP.Api.csproj'
$listenUrl   = 'http://localhost:5000'

# 从仅供本机使用的 .env.local 加载密钥。只输出变量名，不输出变量值。
$envFile = Join-Path $PSScriptRoot '.env.local'
if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
    Write-Host '[错误] 未找到 .env.local。请复制 .env.example 为 .env.local 并填写轮换后的本地开发密钥。' -ForegroundColor Red
    exit 1
}

$loadedEnvNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$lineNumber = 0
foreach ($rawLine in Get-Content -LiteralPath $envFile -Encoding UTF8) {
    $lineNumber++
    $line = $rawLine.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -le 0) {
        Write-Host "[错误] .env.local 第 $lineNumber 行格式无效，应为 NAME=value。" -ForegroundColor Red
        exit 1
    }
    $name = $line.Substring(0, $separator).Trim()
    $value = $line.Substring($separator + 1)
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Write-Host "[错误] .env.local 第 $lineNumber 行变量名无效：$name" -ForegroundColor Red
        exit 1
    }
    if (-not $loadedEnvNames.Add($name)) {
        Write-Host "[错误] .env.local 存在重复变量：$name" -ForegroundColor Red
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '<[^>]+>') {
        Write-Host "[错误] .env.local 变量尚未填写：$name" -ForegroundColor Red
        exit 1
    }
    Set-Item -Path "Env:$name" -Value $value
}

$requiredEnvNames = @(
    'ERP_ConnectionStrings__Default',
    'ERP_Jwt__Key',
    'ERP_Oss__AccessKeyId',
    'ERP_Oss__AccessKeySecret',
    'ERP_Oss__Bucket',
    'ERP_Oss__Endpoint'
)
foreach ($name in $requiredEnvNames) {
    if (-not $loadedEnvNames.Contains($name)) {
        Write-Host "[错误] .env.local 缺少必填变量：$name" -ForegroundColor Red
        exit 1
    }
}
if ($env:ERP_Jwt__Key.Length -lt 32) {
    Write-Host '[错误] ERP_Jwt__Key 必须至少为 32 个字符。' -ForegroundColor Red
    exit 1
}
Write-Host "[信息] 已从 .env.local 安全加载 $($loadedEnvNames.Count) 个环境变量（值不显示）。" -ForegroundColor Green
if ($ValidateEnvironmentOnly) {
    Write-Host '[信息] .env.local 校验通过；未启动 API。' -ForegroundColor Green
    exit 0
}

Write-Host '================ 外贸 ERP 系统 开发环境启动 ================' -ForegroundColor Cyan

# 1. 校验 .NET SDK
try {
    $sdkVersion = (dotnet --version).Trim()
    Write-Host "[信息] 已检测到 .NET SDK 版本：$sdkVersion" -ForegroundColor Green
}
catch {
    Write-Host '[错误] 未检测到 .NET SDK，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
    exit 1
}

# 2. 校验项目文件存在
if (-not (Test-Path $projectPath)) {
    Write-Host "[错误] 未找到项目文件：$projectPath" -ForegroundColor Red
    exit 1
}

# 3. 检查 5000 端口是否被占用（占用时给出 PID，避免启动失败无从排查）
$occupied = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if ($occupied) {
    foreach ($conn in $occupied) {
        $procName = (Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Write-Host "[警告] 端口 5000 已被占用：PID=$($conn.OwningProcess) 进程=$procName" -ForegroundColor Yellow
    }
    Write-Host '[警告] 请先结束占用进程（Stop-Process -Id <PID> -Force），或修改本脚本中的 $listenUrl 端口。' -ForegroundColor Yellow
    exit 1
}
Write-Host '[信息] 端口 5000 空闲，可正常启动。' -ForegroundColor Green

# 4. 指定运行环境：读取 appsettings.Development.json（含开发库连接串与 JWT 配置）
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Write-Host '[信息] 运行环境：Development' -ForegroundColor Green

# 5. 还原 NuGet 依赖
Write-Host '[信息] 正在还原 NuGet 依赖……' -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host '[错误] 依赖还原失败，请检查网络或 NuGet 源配置。' -ForegroundColor Red
    exit $LASTEXITCODE
}

# 6. 启动 WebAPI（启动时自动建表并写入种子数据）
Write-Host "[信息] 正在启动服务，监听地址：$listenUrl" -ForegroundColor Cyan
Write-Host "[信息] 前端页面：$listenUrl/    接口文档：$listenUrl/swagger" -ForegroundColor Cyan
Write-Host '[信息] 默认管理员账号：admin / Admin@123（首次登录后请立即修改）' -ForegroundColor Cyan
Write-Host '[信息] 运行日志：src\ERP.Api\logs\erp-yyyyMMdd.log ；按 Ctrl+C 停止服务。' -ForegroundColor Cyan

dotnet run --project $projectPath --urls $listenUrl
exit $LASTEXITCODE
