# ============================================================
# 生成第四阶段增量脚本 deploy/init4.sql
# 作用：从 init2.sql / init3.sql 提取全部 sp_Biz_* 存储过程定义，
#       为其增加「销审（UnAudit）」动作分支，并输出为可重复执行的增量脚本
# 用法：powershell -ExecutionPolicy Bypass -File .\tools\gen-init4.ps1
# 说明：采用「逐行 + BEGIN/END 嵌套计数」定位审核分支的结束位置，
#       可正确处理审核分支内部含 IF/ELSE 嵌套的存储过程
# ============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sources = @("$root\deploy\init2.sql", "$root\deploy\init3.sql")

# 1. 提取全部 sp_Biz_* 存储过程定义（CREATE PROCEDURE ... GO）
$blocks = @()
foreach ($file in $sources) {
    $text = Get-Content -Raw -Encoding UTF8 $file
    $found = [regex]::Matches($text, "(?ms)^CREATE PROCEDURE db_owner\.(sp_Biz_\w+).*?^GO\s*$")
    foreach ($m in $found) {
        $blocks += [pscustomobject]@{ Name = $m.Groups[1].Value; Sql = $m.Value.TrimEnd() }
    }
}
Write-Host "[信息] 共提取存储过程 $($blocks.Count) 个"

# 2. 为单个存储过程注入销审分支
function Add-UnAuditBranch {
    param([string]$Sql, [string]$ProcName)

    $lines = $Sql -split "\r?\n"
    $result = New-Object System.Collections.Generic.List[string]
    $injected = $false
    $i = 0

    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $result.Add($line)

        # 定位审核分支
        if (-not $injected -and $line -match "^\s*ELSE IF @Action = 'Audit'") {
            $indent = ([regex]::Match($line, '^\s*')).Value
            $next = $i + 1
            $body = @()
            $endIndex = $next

            if ($next -lt $lines.Count -and $lines[$next].Trim() -eq 'BEGIN') {
                # 多行写法：按 BEGIN/END 嵌套计数找到审核分支的结束行
                $depth = 0
                $j = $next
                while ($j -lt $lines.Count) {
                    $t = $lines[$j].Trim()
                    if ($t -eq 'BEGIN') { $depth++ }
                    elseif ($t -eq 'END') { $depth--; if ($depth -eq 0) { break } }
                    $j++
                }
                $endIndex = $j
                for ($k = $next; $k -le $endIndex; $k++) { $body += $lines[$k] }
            }
            else {
                # 单行写法：下一行为 UPDATE 语句，将其 Status=2 反转为 Status=1
                $endIndex = $next
                $body += $lines[$next]
            }

            if (($body -join "`n") -match "UnAudit") {
                # 已存在销审分支，避免重复注入
                for ($k = $next; $k -le $endIndex; $k++) { $result.Add($lines[$k]) }
                $i = $endIndex
                $injected = $true
                $i++
                continue
            }

            $table = [regex]::Match(($body -join "`n"), 'UPDATE db_owner\.(\w+)').Groups[1].Value
            if ([string]::IsNullOrWhiteSpace($table)) { $table = [regex]::Match(($body -join "`n"), 'db_owner\.(\w+)').Groups[1].Value }
            if ([string]::IsNullOrWhiteSpace($table)) {
                Write-Host "[警告] $ProcName 未能识别单据表名，跳过注入" -ForegroundColor Yellow
                for ($k = $next; $k -le $endIndex; $k++) { $result.Add($lines[$k]) }
                $i = $endIndex + 1
                continue
            }

            # 复制原审核分支
            for ($k = $next; $k -le $endIndex; $k++) { $result.Add($lines[$k]) }

            if ($lines[$next].Trim() -eq 'BEGIN') {
                # 多行写法：追加结构相同的销审分支（含提示信息）
                $result.Add("$indent" + "ELSE IF @Action = 'UnAudit'")
                $result.Add("$indent" + "BEGIN")
                $result.Add("$indent" + "    IF EXISTS (SELECT 1 FROM db_owner.$table WHERE Oid = @Oid AND Status = 2)")
                $result.Add("$indent" + "        UPDATE db_owner.$table SET Status = 1, UpdatedAt = GETDATE(), UpdatedBy = @UserId WHERE Oid = @Oid;")
                $result.Add("$indent" + "    ELSE")
                $result.Add("$indent" + "    BEGIN")
                $result.Add("$indent" + "        SET @Result = -1; SET @Msg = N'仅已审核状态的单据可销审';")
                $result.Add("$indent" + "    END")
                $result.Add("$indent" + "END")
            }
            else {
                # 单行写法：把审核分支的 UPDATE 复制为销审（状态 2 -> 1）
                $updateLine = $lines[$next] -replace 'SET Status\s*=\s*2', 'SET Status=1'
                $result.Add("$indent" + "ELSE IF @Action = 'UnAudit' AND EXISTS (SELECT 1 FROM db_owner.$table WHERE Oid=@Oid AND Status=2)")
                $result.Add($updateLine)
            }

            $injected = $true
            $i = $endIndex + 1
            continue
        }
        $i++
    }

    $outputSql = ($result -join "`r`n")
    if (-not $injected) {
        Write-Host "[警告] $ProcName 未找到审核分支" -ForegroundColor Yellow
    } else {
        Write-Host "[信息] $ProcName -> 销审分支 已注入"
    }
    return $outputSql
}



# 3. 生成脚本头部（DDL 部分：打印模板表 / 唯一索引 / 日志单据号列）
$header = @'
-- ============================================================
-- 外贸 ERP 系统 增量升级脚本（第四阶段）
-- 适用库：WMERP_Data（SQL Server 2022）
-- 前提  ：已完成 init2.sql / init3.sql 的初始化
-- 内容  ：
--   1. 业务单据新增「销审（反审核）」动作：重建全部 16 个 sp_Biz_* 存储过程
--      （使用 CREATE OR ALTER，不删除任何业务表与数据）
--   2. 打印设计模板表 db_owner.SysPrintTemplates（IF NOT EXISTS 建表 + 唯一索引）
--   3. 系统操作日志表 db_owner.SysOperationLogs 增加 BillNo 列（单据号追溯）
-- 说明  ：本脚本幂等，可重复执行；执行完成后请重启 IIS 站点
-- ============================================================
USE [WMERP_Data];
GO

/* ============ 1. 打印设计模板表 ============ */
IF OBJECT_ID('db_owner.SysPrintTemplates') IS NULL
BEGIN
    CREATE TABLE db_owner.SysPrintTemplates (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,    -- 主键
        BillType NVARCHAR(50) NOT NULL,                  -- 单据类型（菜单编码）
        TemplateName NVARCHAR(100) NOT NULL,             -- 模板名称
        Title NVARCHAR(200) NOT NULL DEFAULT '',         -- 打印标题
        CompanyName NVARCHAR(200) NOT NULL DEFAULT '',   -- 公司抬头
        CompanyAddress NVARCHAR(300) NOT NULL DEFAULT '', -- 公司地址
        CompanyPhone NVARCHAR(100) NOT NULL DEFAULT '',  -- 公司电话
        ShowCompanyHeader BIT NOT NULL DEFAULT 1,        -- 是否显示公司抬头
        ShowDetailTable BIT NOT NULL DEFAULT 1,          -- 是否打印明细
        ShowRemark BIT NOT NULL DEFAULT 1,               -- 是否打印备注
        PaperSize NVARCHAR(20) NOT NULL DEFAULT 'A4',    -- 纸张规格
        FontSize INT NOT NULL DEFAULT 12,                -- 正文字号
        FieldKeys NVARCHAR(2000) NOT NULL DEFAULT '',    -- 打印字段顺序（JSON 数组）
        FooterText NVARCHAR(500) NOT NULL DEFAULT '',    -- 页脚文本
        IsDefault BIT NOT NULL DEFAULT 0,                -- 是否默认模板
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END
GO

/* 兼容历史版本遗留的单数表名：如存在则重命名，避免残留空表 */
IF OBJECT_ID('db_owner.SysPrintTemplate') IS NOT NULL AND OBJECT_ID('db_owner.SysPrintTemplates') IS NULL
    EXEC sp_rename 'db_owner.SysPrintTemplate', 'SysPrintTemplates';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SysPrintTemplates_BillType_Name')
    CREATE UNIQUE INDEX UX_SysPrintTemplates_BillType_Name
        ON db_owner.SysPrintTemplates(BillType, TemplateName);
GO

/* ============ 2. 系统操作日志增加单据号列 ============ */
IF COL_LENGTH('db_owner.SysOperationLogs', 'BillNo') IS NULL
    ALTER TABLE db_owner.SysOperationLogs ADD BillNo NVARCHAR(100) NOT NULL DEFAULT '';
GO

/* ============ 3. 业务单据存储过程（新增销审动作） ============ */
'@

# 4. 逐个存储过程注入销审分支并改写为 CREATE OR ALTER
$procResults = @()
foreach ($item in $blocks) {
    $sql = $item.Sql -replace '^CREATE PROCEDURE', 'CREATE OR ALTER PROCEDURE'
    $procResults += (Add-UnAuditBranch -Sql $sql -ProcName $item.Name)
}

$output = $header + "`r`n" + ($procResults -join "`r`n`r`n") + "`r`n"
Set-Content -Path "$root\deploy\init4.sql" -Value $output -Encoding UTF8
Write-Host "[完成] 已生成 $root\deploy\init4.sql"


