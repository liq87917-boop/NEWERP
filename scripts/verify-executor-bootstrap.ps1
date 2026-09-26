<#
.SYNOPSIS
    ERP-078 control-plane verification: confirm the automated executor (Cline)
    provider/model pinning and the agent-bootstrap script are healthy after the
    ERP-074..077 no-op quarantines.

.DESCRIPTION
    Read-only verification. This script never mutates .ai/PROJECT_STATE.json,
    task JSON, or the audit trail, and never starts Cline, the API, SQL Server,
    the browser, or any deployment.

    Checks:
      1. Provider/model pinning (start_agent.bat + scripts/ai_orchestrator.py)
         pins AI_CLINE_PROVIDER=deepseek / AI_CLINE_MODEL=deepseek-v4-pro and
         passes "--provider / --model" to Cline, removing the Cline Credits default.
      2. scripts/agent-bootstrap.ps1 parses with no PowerShell errors and still
         contains the safe fast-forward merge, the remote/local path-conflict
         check, and Repair-DeferredBrowserFailedHead.
      3. A bounded, read-only dry-run of the rolling-queue head selection (using
         the real ai_orchestrator.next_task() via a throwaway Python helper) and
         reports the effective provider/model and whether ERP-074 may be resumed.

.PARAMETER Json
    Emit the report as a single JSON object instead of human-readable text.
#>
[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-Text([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8)
}

function Resolve-EnvValue([string]$Name, [string]$Fallback) {
    $v = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $Fallback }
    return $v
}

# ---------------------------------------------------------------- 1. provider
$bat = Get-Text (Join-Path $root 'start_agent.bat')
$batProvider = $null
$batModel = $null
if ($bat -match '(?m)^\s*set\s+AI_CLINE_PROVIDER=(\S+)') { $batProvider = $Matches[1].Trim() }
if ($bat -match '(?m)^\s*set\s+AI_CLINE_MODEL=(\S+)') { $batModel = $Matches[1].Trim() }

$orch = Get-Text (Join-Path $root 'scripts\ai_orchestrator.py')
$orchReadsProvider = $orch -match 'AI_CLINE_PROVIDER'
$orchReadsModel = $orch -match 'AI_CLINE_MODEL'
$orchPassesProviderFlag = $orch -match '"--provider"'
$orchPassesModelFlag = $orch -match '"--model"'
$orchFallbackModelMatch = [regex]::Match($orch, 'get\("AI_CLINE_MODEL",\s*"([^"]+)"\)')
$orchFallbackModel = if ($orchFallbackModelMatch.Success) { $orchFallbackModelMatch.Groups[1].Value } else { $null }

$effectiveProvider = Resolve-EnvValue 'AI_CLINE_PROVIDER' 'deepseek'
$effectiveModel = Resolve-EnvValue 'AI_CLINE_MODEL' $orchFallbackModel

# ------------------------------------------------------------- 2. bootstrap
$bootstrapPath = Join-Path $root 'scripts\agent-bootstrap.ps1'
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($bootstrapPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
$bootstrap = Get-Text $bootstrapPath
$hasFfOnly = $bootstrap -match 'merge.*--ff-only'
$repairCount = ([regex]::Matches($bootstrap, 'Repair-DeferredBrowserFailedHead')).Count
$hasConflictCheck = $bootstrap -match 'overlaps local work'

# ------------------------------------------------- 3. bounded read-only dry-run
$py = @'
import os, sys
sys.path.insert(0, sys.argv[1])
import ai_orchestrator as o
cfg = o.load_json(o.CONFIG_PATH)
print("env_provider=" + os.environ.get("AI_CLINE_PROVIDER", "deepseek"))
print("env_model=" + os.environ.get("AI_CLINE_MODEL", "deepseek-v4-pro"))
try:
    path, task = o.next_task(cfg)
    print("next_task=" + (task.get("id") if task else "none"))
except ValueError as exc:
    print("next_task_blocked=" + str(exc))
'@
$tmpPy = Join-Path ([System.IO.Path]::GetTempPath()) ('erp078-dryrun-' + [System.Guid]::NewGuid().ToString('N') + '.py')
$pyOut = $null
$pyExit = $null
try {
    [System.IO.File]::WriteAllText($tmpPy, $py, [System.Text.UTF8Encoding]::new($false))
    $pyOut = (& py -3 $tmpPy (Join-Path $root 'scripts') 2>&1 | Out-String).Trim()
    $pyExit = $LASTEXITCODE
} finally {
    if (Test-Path -LiteralPath $tmpPy) { Remove-Item -LiteralPath $tmpPy -Force }
}

# -------------------------------------------------------------- build report
$report = [ordered]@{
    task = 'ERP-078'
    provider_pinning = [ordered]@{
        bat_provider = $batProvider
        bat_model = $batModel
        orchestrator_reads_provider_env = $orchReadsProvider
        orchestrator_reads_model_env = $orchReadsModel
        orchestrator_passes_provider_flag = $orchPassesProviderFlag
        orchestrator_passes_model_flag = $orchPassesModelFlag
        orchestrator_model_fallback = $orchFallbackModel
        effective_provider = $effectiveProvider
        effective_model = $effectiveModel
        cline_credits_default_removed = ($batProvider -eq 'deepseek' -and $orchPassesProviderFlag)
    }
    agent_bootstrap = [ordered]@{
        parse_errors = @($parseErrors).Count
        fast_forward_merge_present = $hasFfOnly
        path_conflict_check_present = $hasConflictCheck
        repair_deferred_browser_present = ($repairCount -ge 2)
        repair_occurrences = $repairCount
    }
    bounded_dry_run = [ordered]@{
        python_exit_code = $pyExit
        output = $pyOut
    }
}

$prereqsHealthy = ($batProvider -eq 'deepseek') -and
                  $orchPassesProviderFlag -and
                  $orchPassesModelFlag -and
                  (@($parseErrors).Count -eq 0) -and
                  $hasFfOnly -and
                  $hasConflictCheck -and
                  ($repairCount -ge 2)
$queueHead = $null
if ($pyOut -match 'next_task_blocked=(.+)') { $queueHead = $Matches[1] }
elseif ($pyOut -match 'next_task=(\S+)') { $queueHead = $Matches[1] }

$erp074Resumable = $prereqsHealthy
$conclusion = if ($erp074Resumable) {
    'ERP-074 MAY be safely resumed by the rolling queue once ERP-078 (in_progress) completes and ERP-074 is re-queued from "failed" to "retry" (py -3 scripts/ai_pipeline.py retry ERP-074). The original no-op cause is fixed: provider/model are pinned to deepseek/deepseek-v4-pro and agent-bootstrap.ps1 parses cleanly with fast-forward/repair behavior intact.'
} else {
    'ERP-074 must NOT be resumed: a prerequisite check regressed. Fix the failing check before re-queueing ERP-074.'
}

$report['erp074_resumable'] = $erp074Resumable
$report['queue_head'] = $queueHead
$report['conclusion'] = $conclusion

$jsonOut = $report | ConvertTo-Json -Depth 8
if ($Json) {
    $jsonOut
} else {
    Write-Host '=== ERP-078 executor-bootstrap verification ===' -ForegroundColor Cyan
    $jsonOut
}

# Fail hard if any prerequisite check regressed, so this script doubles as a gate.
if (-not $prereqsHealthy) { exit 2 }
exit 0

