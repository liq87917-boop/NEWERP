# ============================================================
# 外贸 ERP 系统 冒烟测试脚本（第四阶段新增功能）
# 用法：powershell -ExecutionPolicy Bypass -File .\tools\smoke-test.ps1
# 日志：logs\smoke-20260912.log
# 说明：Windows PowerShell 5.1 下 -Body 传字符串按 ASCII 编码，统一改用 UTF-8 字节提交
# ============================================================
param(
    [string]$BaseUrl = 'http://localhost:5055',
    [int]$WaitSeconds = 90,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot
$logFile = Join-Path $root 'logs\smoke-20260912.log'
$outLog = Join-Path $root 'logs\smoke-run-out.log'
$errLog = Join-Path $root 'logs\smoke-run-err.log'
$script:passed = 0
$script:failed = 0

function Write-Log {
    param([string]$Message, [string]$Level = '信息')
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    $color = switch ($Level) { '通过' { 'Green' } '失败' { 'Red' } '标题' { 'Cyan' } default { 'Gray' } }
    Write-Host $line -ForegroundColor $color
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

function Assert-True {
    param([bool]$Condition, [string]$Name, [string]$Detail = '')
    if ($Condition) {
        $script:passed++
        Write-Log "$Name —— 通过 $Detail" '通过'
    } else {
        $script:failed++
        Write-Log "$Name —— 失败 $Detail" '失败'
    }
}

# 以 UTF-8 编码提交 JSON（避免中文在 PowerShell 5.1 下被转成问号）
function Invoke-Json {
    param([string]$Uri, [string]$Method, $Body, [hashtable]$Headers = @{})
    $json = $Body | ConvertTo-Json -Depth 6
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers `
        -ContentType 'application/json; charset=utf-8' -Body $bytes
}

# 以 multipart/form-data 上传文件
function Invoke-Upload {
    param([string]$Uri, [string]$FilePath, [string]$Token)
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.DefaultRequestHeaders.Authorization =
        New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Token)
    $client.Timeout = [TimeSpan]::FromSeconds(180)
    $form = New-Object System.Net.Http.MultipartFormDataContent
    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $content = [System.Net.Http.ByteArrayContent]::new($bytes)
    $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse(
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')
    $form.Add($content, 'file', [System.IO.Path]::GetFileName($FilePath))
    $response = $client.PostAsync($Uri, $form).Result
    $text = $response.Content.ReadAsStringAsync().Result
    $client.Dispose()
    return ($text | ConvertFrom-Json)
}

# ============ 启动服务 ============
Set-Content -Path $logFile -Value "=== 外贸 ERP 冒烟测试 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -Encoding UTF8
Write-Log '启动 WebAPI（Development，端口 5055）……' '标题'

$existing = Get-NetTCPConnection -LocalPort 5055 -State Listen -ErrorAction SilentlyContinue
$startedByScript = $false
if (-not $existing) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Start-Process -FilePath 'dotnet' `
        -ArgumentList "run --project src\ERP.Api\ERP.Api.csproj --urls $BaseUrl" `
        -WorkingDirectory $root -RedirectStandardOutput $outLog -RedirectStandardError $errLog | Out-Null
    $startedByScript = $true
} else {
    Write-Log '端口 5055 已有服务在运行，直接复用。'
}

$ready = $false
for ($i = 1; $i -le $WaitSeconds; $i++) {
    Start-Sleep -Seconds 1
    try {
        $null = Invoke-WebRequest -Uri "$BaseUrl/swagger/v1/swagger.json" -UseBasicParsing -TimeoutSec 3
        $ready = $true
        break
    } catch { }
}
if (-not $ready) {
    Write-Log "服务在 $WaitSeconds 秒内未就绪，请查看 $outLog / $errLog" '失败'
    exit 1
}
Write-Log "服务已就绪：$BaseUrl（等待 $i 秒）"

try {
    # ============ 1. 登录认证 ============
    Write-Log '1. 登录认证' '标题'
    $login = Invoke-Json -Uri "$BaseUrl/api/auth/login" -Method Post -Body @{ userName = 'admin'; password = 'Admin@123' }
    Assert-True ($login.code -eq 0) '管理员登录'
    $headers = @{ Authorization = "Bearer $($login.data.token)" }
    $token = $login.data.token

    # ============ 0. 清理历史冒烟测试产生的打印模板（保证脚本可重复运行）============
    Write-Log '0. 清理历史冒烟测试模板' '标题'
    $existingTpls = Invoke-RestMethod -Uri "$BaseUrl/api/sys/print-templates?billType=sales-order" -Headers $headers
    $cleanedCount = 0
    foreach ($t in @($existingTpls.data)) {
        if ($t.templateName -like '冒烟测试*' -or $t.templateName -like '探针*' -or $t.templateName -like '冷启动探针*') {
            try {
                $null = Invoke-RestMethod -Uri "$BaseUrl/api/sys/print-templates/$($t.id)" -Method Delete -Headers $headers
                $cleanedCount++
            } catch {}
        }
    }
    Write-Log "已清理历史模板 $cleanedCount 条"

    # ============ 2. 打印设计 / 打印模板 ============
    Write-Log '2. 打印设计 / 打印模板' '标题'
    $tpl = Invoke-RestMethod -Uri "$BaseUrl/api/sys/print-templates/sales-order" -Headers $headers
    Assert-True ($tpl.code -eq 0 -and $tpl.data.BillType -eq 'sales-order') '读取默认打印模板' "标题=$($tpl.data.Title) 纸张=$($tpl.data.PaperSize)"

    $savedTpl = Invoke-Json -Uri "$BaseUrl/api/sys/print-templates" -Method Post -Headers $headers -Body @{
        Id = 0; BillType = 'sales-order'; TemplateName = "冒烟测试_$((Get-Date).ToString('HHmmss'))"; Title = '销售订单（冒烟测试）'
        CompanyName = '冒烟测试公司'; CompanyAddress = '测试地址'; CompanyPhone = '0000-0000000'
        PaperSize = 'A4'; FontSize = 11; FieldKeys = '["BillNo","OrderDate","CustId","TotalAmount","Status"]'
        FooterText = '冒烟测试页脚'; ShowCompanyHeader = $true; ShowDetailTable = $true
        ShowRemark = $true; IsDefault = $true
    }
    Assert-True ($savedTpl.code -eq 0 -and $savedTpl.data.Id -gt 0) '保存打印模板' "Id=$($savedTpl.data.Id)"
    $tplId = $savedTpl.data.Id

    $tpl2 = Invoke-RestMethod -Uri "$BaseUrl/api/sys/print-templates/sales-order" -Headers $headers
    Assert-True ($tpl2.data.Title -eq '销售订单（冒烟测试）') '默认模板已切换为新建模板' "标题=$($tpl2.data.Title)"
    Assert-True ($tpl2.data.CompanyName -eq '冒烟测试公司') '中文内容按 UTF-8 正确保存'
    Assert-True ($tpl2.data.FieldKeys -like '*BillNo*') '打印字段配置已保存'

    # ============ 3. 单据新增 / 查询 / 明细 ============
    Write-Log '3. 单据新增与查询' '标题'
    $today = (Get-Date).ToString('yyyy-MM-dd') + 'T00:00:00'
    $saved = Invoke-Json -Uri "$BaseUrl/api/v2/bills/sales-order/save" -Method Post -Headers $headers -Body @{
        oid = 0
        fields = @{ OrderDate = $today; CustId = 1; EmpId = 1; Currency = 2; ExchangeRate = 7.1; TotalAmount = 1234.56; Remark = '冒烟测试单据' }
        details = @(@{ ProductId = 1; ProductName = '冒烟测试商品'; Spec = '规格A'; Quantity = 10; Unit = 'PCS'; UnitPrice = 123.456; Amount = 1234.56 })
    }
    Assert-True ($saved.code -eq 0 -and $saved.data.oid -gt 0) '新增销售订单' "单据号=$($saved.data.billNo)"
    $oid = $saved.data.oid
    $billNo = $saved.data.billNo

    $detail = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$oid" -Headers $headers
    Assert-True ($detail.code -eq 0 -and $detail.data.main.BillNo -eq $billNo) '查询单据主表'
    Assert-True ($detail.data.details.Count -ge 1) '查询单据明细' "明细行数=$($detail.data.details.Count)"
    Assert-True ($detail.data.main.Remark -eq '冒烟测试单据') '单据中文备注正确保存'

    $paged = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order?page=1&pageSize=10&keyword=$billNo" -Headers $headers
    Assert-True ($paged.data.total -ge 1) '按单据号搜索'

    # ============ 4. 单据状态流转（审核 / 销审） ============
    Write-Log '4. 单据状态流转' '标题'
    $audit = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$oid/audit" -Method Post -Headers $headers
    Assert-True ($audit.code -eq 0) '审核单据' "提示=$($audit.message)"

    $unaudit = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$oid/unaudit" -Method Post -Headers $headers
    if ($unaudit.code -eq 0) {
        Write-Log '销审（反审核）—— 通过（数据库已包含销审分支）' '通过'
        $script:passed++
    } else {
        Write-Log "销审返回：$($unaudit.message)" '信息'
        Assert-True ($unaudit.message -like '*init4.sql*') '销审未升级时给出明确提示'
    }

    # ============ 5. 单据操作日志（按单据追溯） ============
    Write-Log '5. 单据操作日志' '标题'
    $logs = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$oid/logs?page=1&pageSize=20" -Headers $headers
    Assert-True ($logs.code -eq 0 -and $logs.data.total -ge 2) '查询单据操作日志' "条数=$($logs.data.total)"
    Assert-True (($logs.data.items | Where-Object { $_.billNo -eq $billNo }).Count -ge 1) '日志已记录单据号' "单据号=$billNo"

    $logSearch = Invoke-RestMethod -Uri "$BaseUrl/api/sys/logs?page=1&pageSize=20&keyword=$billNo" -Headers $headers
    Assert-True ($logSearch.code -eq 0 -and $logSearch.data.total -ge 1) '系统日志按单据号搜索'

    # ============ 6. 单据导出 ============
    Write-Log '6. 单据导出' '标题'
    $exportPath = Join-Path $env:TEMP 'erp-export-test.xlsx'
    Invoke-WebRequest -Uri "$BaseUrl/api/v2/bills/sales-order/export?keyword=$billNo" -Headers $headers -OutFile $exportPath -UseBasicParsing
    $exportSize = (Get-Item $exportPath).Length
    Assert-True ($exportSize -gt 1000) '导出销售订单 Excel' "文件大小=$exportSize 字节"

    # ============ 7. 单据导入（下载示例文件后回传，验证解析与存储过程写入） ============
    Write-Log '7. 单据导入' '标题'
    $importPath = Join-Path $env:TEMP 'erp-import-test.xlsx'
    Invoke-WebRequest -Uri "$BaseUrl/api/v2/bills/sales-order/import-template?withSample=true" -Headers $headers -OutFile $importPath -UseBasicParsing
    Assert-True ((Get-Item $importPath).Length -gt 1000) '下载导入示例文件'

    $importJson = Invoke-Upload -Uri "$BaseUrl/api/v2/bills/sales-order/import" -FilePath $importPath -Token $token
    Assert-True ($importJson.code -eq 0 -and $importJson.data.success -ge 1) '导入销售订单 Excel' "成功=$($importJson.data.success) 失败=$($importJson.data.failed)"

    # ============ 8. 基础资料导出 / 导入模板 / 导入（重复数据校验） ============
    Write-Log '8. 基础资料导入导出' '标题'
    $baseExportPath = Join-Path $env:TEMP 'erp-base-export-test.xlsx'
    Invoke-WebRequest -Uri "$BaseUrl/api/base/io/customers/export" -Headers $headers -OutFile $baseExportPath -UseBasicParsing
    $baseExportSize = (Get-Item $baseExportPath).Length
    Assert-True ($baseExportSize -gt 1000) '导出客户资料 Excel' "文件大小=$baseExportSize 字节"

    $baseTplPath = Join-Path $env:TEMP 'erp-base-tpl-test.xlsx'
    Invoke-WebRequest -Uri "$BaseUrl/api/base/io/products/import-template" -Headers $headers -OutFile $baseTplPath -UseBasicParsing
    Assert-True ((Get-Item $baseTplPath).Length -gt 1000) '下载商品导入模板'

    $jsonBase = Invoke-Upload -Uri "$BaseUrl/api/base/io/customers/import" -FilePath $baseExportPath -Token $token
    Assert-True ($jsonBase.code -eq 0) '基础资料导入接口可正常执行' "总计=$($jsonBase.data.total) 成功=$($jsonBase.data.success) 失败=$($jsonBase.data.failed)"

    # ============ 9. 清理冒烟测试产生的数据 ============
    Write-Log '9. 清理冒烟测试数据' '标题'
    $delete = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$oid/delete" -Method Post -Headers $headers
    Assert-True ($delete.code -eq 0) '删除测试单据' "提示=$($delete.message)"

    $delTpl = Invoke-RestMethod -Uri "$BaseUrl/api/sys/print-templates/$tplId" -Method Delete -Headers $headers
    Assert-True ($delTpl.code -eq 0) '删除测试打印模板'

    # 清理历史冒烟测试残留单据（备注为「冒烟测试单据」或导入生成的空金额单据）
    $drafts = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order?page=1&pageSize=50&status=1" -Headers $headers
    foreach ($row in @($drafts.data.items)) {
        $isSmoke = ($row.Remark -eq '冒烟测试单据') -or ([decimal]$row.TotalAmount -eq 0 -and [decimal]$row.DepositAmount -eq 0)
        if ($isSmoke) {
            $null = Invoke-RestMethod -Uri "$BaseUrl/api/v2/bills/sales-order/$($row.Oid)/delete" -Method Post -Headers $headers
            Write-Log "已清理测试单据：$($row.BillNo)"
        }
    }
}
catch {
    Write-Log "执行异常：$($_.Exception.Message)" '失败'
    $script:failed++
}
finally {
    if ($startedByScript -and -not $KeepRunning) {
        Get-NetTCPConnection -LocalPort 5055 -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
        Write-Log '已停止 WebAPI 服务。'
    }
    Write-Log "冒烟测试结束：通过 $($script:passed) 项，失败 $($script:failed) 项"
}

