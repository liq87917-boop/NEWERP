# ============================================================
# 外贸 ERP 系统 · 开发环境一键启动脚本
# 用法：在项目根目录执行  powershell -ExecutionPolicy Bypass -File .\start-dev.ps1
# 作用：校验 SDK → 还原依赖 → 以 Development 环境启动 WebAPI（http://localhost:5000）
# ============================================================

$ErrorActionPreference = 'Stop'

# 统一使用 UTF-8 输出，避免中文日志乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 切换到脚本所在目录（即解决方案根目录）
Set-Location -Path $PSScriptRoot

$projectPath = 'src\ERP.Api\ERP.Api.csproj'
$listenUrl   = 'http://localhost:5000'

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
