# ============================================================
# 执行增量升级脚本 deploy\init4.sql（第四阶段）
# 作用：读取 appsettings.Development.json 中的连接字符串，按 GO 分批执行脚本
# 用法：powershell -ExecutionPolicy Bypass -File .\tools\apply-init4.ps1
# 说明：脚本幂等（CREATE OR ALTER / IF NOT EXISTS），不会删除表与数据
# ============================================================
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'deploy\init4.sql'
$configPath = Join-Path $root 'src\ERP.Api\appsettings.Development.json'

$config = Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json
$connectionString = $config.ConnectionStrings.Default
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    Write-Host '[错误] 未在 appsettings.Development.json 中找到 ConnectionStrings.Default' -ForegroundColor Red
    exit 1
}

$sql = Get-Content -Raw -Encoding UTF8 $scriptPath
# 按 GO 批次拆分（GO 必须独占一行）
$batches = [regex]::Split($sql, '(?m)^\s*GO\s*$') | Where-Object { $_.Trim().Length -gt 0 }

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
$connection.Open()
Write-Host "[信息] 已连接数据库，共 $($batches.Count) 个批次待执行" -ForegroundColor Cyan

$index = 0
foreach ($batch in $batches) {
    $index++
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = $batch
    try {
        $null = $command.ExecuteNonQuery()
    } catch {
        Write-Host "[错误] 第 $index 批次执行失败：$($_.Exception.Message)" -ForegroundColor Red
        $connection.Close()
        exit 1
    } finally {
        $command.Dispose()
    }
}
$connection.Close()
Write-Host '[完成] init4.sql 执行成功（存储过程销审分支 + 打印模板表 + 日志单据号列）' -ForegroundColor Green


