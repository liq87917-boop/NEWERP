# ============================================================
# 外贸 ERP 系统 · 发布产物本地验证脚本（模拟生产环境）
# 用法：在项目根目录执行  powershell -ExecutionPolicy Bypass -File .\start-deploy.ps1
# 作用：以 Production 环境直接运行 .\deploy\ERP.Api.dll，验证 IIS 部署前的产物是否可用
# 说明：生产敏感配置（连接串、JWT 密钥）通过 ERP_ 前缀环境变量注入，参考 .env.example
# ============================================================

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

Set-Location -Path $PSScriptRoot

$dllPath   = 'deploy\ERP.Api.dll'
$listenUrl = 'http://localhost:5001'

Write-Host '============== 外贸 ERP 系统 发布产物本地验证 ==============' -ForegroundColor Cyan

# 1. 校验产物是否存在
if (-not (Test-Path $dllPath)) {
    Write-Host "[错误] 未找到发布产物：$dllPath" -ForegroundColor Red
    Write-Host '[提示] 请先执行发布命令：dotnet publish src\ERP.Api\ERP.Api.csproj -c Release -o .\deploy' -ForegroundColor Yellow
    exit 1
}

# 2. 校验生产环境必需的环境变量（未注入则给出明确提示，避免 500.30 无从排查）
$missing = @()
if ([string]::IsNullOrWhiteSpace($env:ERP_ConnectionStrings__Default)) { $missing += 'ERP_ConnectionStrings__Default' }
if ([string]::IsNullOrWhiteSpace($env:ERP_Jwt__Key))                   { $missing += 'ERP_Jwt__Key' }
if ($missing.Count -gt 0) {
    Write-Host "[警告] 以下生产环境变量未注入：$($missing -join '、')" -ForegroundColor Yellow
    Write-Host '[提示] 可在当前会话临时注入后重试，例如：' -ForegroundColor Yellow
    Write-Host '        $env:ERP_ConnectionStrings__Default = "Server=...;Database=WMERP_Data;User Id=...;Password=...;TrustServerCertificate=True"' -ForegroundColor Yellow
    Write-Host '        $env:ERP_Jwt__Key = "请填入32位以上随机密钥"' -ForegroundColor Yellow
    exit 1
}

# 3. 确保日志目录存在（IIS 下需给应用池身份写入权限）
$logDir = 'deploy\logs'
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
    Write-Host "[信息] 已创建日志目录：$logDir" -ForegroundColor Green
}

$env:ASPNETCORE_ENVIRONMENT = 'Production'
Write-Host '[信息] 运行环境：Production' -ForegroundColor Green
Write-Host "[信息] 监听地址：$listenUrl    接口文档：$listenUrl/swagger" -ForegroundColor Cyan
Write-Host '[信息] 运行日志：deploy\logs\erp-yyyyMMdd.log ；按 Ctrl+C 停止服务。' -ForegroundColor Cyan

Push-Location 'deploy'
try {
    dotnet .\ERP.Api.dll --urls $listenUrl
}
finally {
    Pop-Location
}
exit $LASTEXITCODE
