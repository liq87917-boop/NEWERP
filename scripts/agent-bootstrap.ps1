param(
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root
$env:GIT_TERMINAL_PROMPT = "0"

function Invoke-Git {
    param([string[]]$Args)
    $output = & git @Args 2>&1
    return [pscustomobject]@{
        Code = $LASTEXITCODE
        Text = ($output -join [Environment]::NewLine)
    }
}

function Repair-DeferredBrowserFailedHead {
    $configPath = Join-Path $root '.ai\config.json'
    $statePath = Join-Path $root '.ai\PROJECT_STATE.json'
    $tasksDir = Join-Path $root '.ai\tasks'

    if (-not (Test-Path $configPath) -or -not (Test-Path $statePath)) { return }
    try {
        $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $config.completion_policy -or $config.completion_policy.defer_browser_during_development -ne $true) { return }

        $state = Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($state.phase -ne 'blocked' -or $state.finish_reason -ne 'queue_head_blocked' -or -not $state.current_task) { return }

        $taskId = [string]$state.current_task
        $expectedBlocker = "$taskId status=failed stops queue"
        if ([string]$state.blocker -ne $expectedBlocker) { return }

        $taskPath = Join-Path $tasksDir ("{0}.json" -f $taskId)
        if (-not (Test-Path $taskPath)) { return }
        $task = Get-Content $taskPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($task.status -ne 'failed') { return }

        # Convert only this stale queue-blocking state into an interrupted-task
        # recovery state. Core validation still decides whether the task completes.
        $task.status = 'in_progress'
        $task.attempts = 0
        if ($task.PSObject.Properties.Name -contains 'browser_deferred_recovery_done') {
            $task.browser_deferred_recovery_done = $true
        } else {
            $task | Add-Member -NotePropertyName browser_deferred_recovery_done -NotePropertyValue $true
        }

        $state.phase = 'developing'
        $state.blocker = $null
        $state.finish_reason = 'deferred_browser_bootstrap_recovery'
        $state.browser_acceptance = [pscustomobject]@{ status = 'deferred' }
        $state.updated_at = [DateTime]::UtcNow.ToString('o')

        $taskJson = $task | ConvertTo-Json -Depth 30
        $stateJson = $state | ConvertTo-Json -Depth 30
        [System.IO.File]::WriteAllText($taskPath, $taskJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($statePath, $stateJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
        Write-Host "Bootstrap   : recovered $taskId from stale browser-blocked failed state" -ForegroundColor Cyan
    } catch {
        Write-Host ("Bootstrap   : deferred-browser state repair skipped: " + $_.Exception.Message) -ForegroundColor Yellow
    }
}

function Get-Paths {
    param([string[]]$Args)
    $r = Invoke-Git $Args
    if ($r.Code -ne 0) { return $null }
    return @(($r.Text -split "\r?\n") |
        ForEach-Object { $_.Trim().Replace("\", "/") } |
        Where-Object { $_ } |
        Sort-Object -Unique)
}

Write-Host "Bootstrap   : checking origin/$Branch ..." -ForegroundColor DarkCyan

$inside = Invoke-Git @("rev-parse", "--is-inside-work-tree")
if ($inside.Code -ne 0 -or $inside.Text.Trim() -ne "true") {
    Write-Host "Bootstrap   : not a Git repository" -ForegroundColor Red
    exit 10
}

$current = (Invoke-Git @("branch", "--show-current")).Text.Trim()
if ($current -ne $Branch) {
    Write-Host "Bootstrap   : skipped on branch '$current'" -ForegroundColor Yellow
    exit 0
}

$fetch = Invoke-Git @("fetch", "--quiet", "origin", $Branch)
if ($fetch.Code -ne 0) {
    # GitHub CLI may already be authenticated even when Git Credential Manager is
    # not configured for this private repository. Repair the credential helper once
    # and retry so the unattended agent can continue receiving GPT queue updates.
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & gh auth status --hostname github.com *> $null
            if ($LASTEXITCODE -eq 0) {
                & gh auth setup-git --hostname github.com *> $null
                if ($LASTEXITCODE -eq 0) {
                    $fetch = Invoke-Git @("fetch", "--quiet", "origin", $Branch)
                }
            }
        } finally {
            $ErrorActionPreference = $oldPreference
        }
    }
}
if ($fetch.Code -ne 0) {
    Write-Host "Bootstrap   : git fetch failed after credential repair; starting with local code" -ForegroundColor Yellow
    exit 0
}

$counts = Invoke-Git @("rev-list", "--left-right", "--count", "HEAD...origin/$Branch")
if ($counts.Code -ne 0) {
    Write-Host "Bootstrap   : cannot compare local and remote" -ForegroundColor Yellow
    exit 0
}

$parts = ($counts.Text.Trim() -split "\s+")
if ($parts.Count -lt 2) {
    Write-Host "Bootstrap   : comparison unavailable" -ForegroundColor Yellow
    exit 0
}

$ahead = [int]$parts[0]
$behind = [int]$parts[1]

if ($ahead -gt 0 -and $behind -gt 0) {
    # A drained old single-task queue may race with a newly published GPT rolling
    # batch and create one local control-only commit. That commit contains no
    # business work and must not strand the agent forever.
    $messages = (Invoke-Git @("log", "--format=%s", "origin/$Branch..HEAD")).Text -split "\r?\n" | Where-Object { $_ }
    $localOnlyPaths = Get-Paths @("-c", "core.quotepath=false", "diff", "--name-only", "origin/$Branch...HEAD")
    $safePaths = @('.ai/PROJECT_STATE.json', '.ai/audit.jsonl')
    $safeControlOnly = $null -ne $localOnlyPaths -and $localOnlyPaths.Count -gt 0
    foreach ($path in $localOnlyPaths) {
        if ($path -notin $safePaths) { $safeControlOnly = $false; break }
    }
    $safeMessages = $messages.Count -gt 0
    foreach ($message in $messages) {
        if ($message -notmatch '^chore: (automation queue drained|request rolling queue replenishment)if ($ahead -gt 0) {
    Write-Host "Bootstrap   : local ahead by $ahead commit(s); no pull needed" -ForegroundColor Yellow
    exit 0
}
if ($behind -eq 0) {
    Write-Host "Bootstrap   : up to date" -ForegroundColor Green
    Repair-DeferredBrowserFailedHead
    exit 0
}

$localStatus = Invoke-Git @("-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all")
if ($localStatus.Code -ne 0) {
    Write-Host "Bootstrap   : unable to inspect local changes" -ForegroundColor Red
    exit 0
}

$localPaths = @()
foreach ($line in ($localStatus.Text -split "\r?\n")) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) { continue }
    $payload = $line.Substring(3).Trim()
    if ($payload -match " -> ") {
        Write-Host "Bootstrap   : rename/copy detected; no automatic merge" -ForegroundColor Yellow
        exit 0
    }
    if ($payload) { $localPaths += $payload.Replace("\", "/") }
}
$localPaths = @($localPaths | Sort-Object -Unique)

$incomingPaths = Get-Paths @("-c", "core.quotepath=false", "diff", "--name-only", "HEAD..origin/$Branch")
if ($null -eq $incomingPaths) {
    Write-Host "Bootstrap   : unable to inspect incoming paths" -ForegroundColor Red
    exit 0
}

if ($localPaths.Count -gt 0) {
    $set = @{}
    foreach ($path in $localPaths) { $set[$path] = $true }
    $conflicts = @($incomingPaths | Where-Object { $set.ContainsKey($_) })
    if ($conflicts.Count -gt 0) {
        $sample = ($conflicts | Select-Object -First 4) -join ", "
        Write-Host "Bootstrap   : remote update overlaps local work; merge skipped" -ForegroundColor Yellow
        Write-Host "              $sample" -ForegroundColor DarkYellow
        exit 0
    }
}

$merge = Invoke-Git @("merge", "--ff-only", "--quiet", "origin/$Branch")
if ($merge.Code -ne 0) {
    Write-Host "Bootstrap   : safe fast-forward failed; local work preserved" -ForegroundColor Red
    exit 0
}

Write-Host "Bootstrap   : updated $behind commit(s); local task work preserved" -ForegroundColor Green
Repair-DeferredBrowserFailedHead
exit 0
) {
            $safeMessages = $false
            break
        }
    }
    $worktree = Invoke-Git @("status", "--porcelain")
    if ($safeControlOnly -and $safeMessages -and [string]::IsNullOrWhiteSpace($worktree.Text)) {
        $backupRef = "refs/backup/rolling-recovery-" + (Get-Date -Format "yyyyMMddHHmmss")
        $null = Invoke-Git @("update-ref", $backupRef, "HEAD")
        $reset = Invoke-Git @("reset", "--hard", "origin/$Branch")
        if ($reset.Code -eq 0) {
            Write-Host "Bootstrap   : discarded stale queue-drained control commit; rolling batch restored" -ForegroundColor Green
            Repair-DeferredBrowserFailedHead
            exit 0
        }
    }
    Write-Host "Bootstrap   : local/remote diverged (ahead $ahead, behind $behind); business-safe auto-recovery not applicable" -ForegroundColor Red
    exit 0
}
if ($ahead -gt 0) {
    Write-Host "Bootstrap   : local ahead by $ahead commit(s); no pull needed" -ForegroundColor Yellow
    exit 0
}
if ($behind -eq 0) {
    Write-Host "Bootstrap   : up to date" -ForegroundColor Green
    Repair-DeferredBrowserFailedHead
    exit 0
}

$localStatus = Invoke-Git @("-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all")
if ($localStatus.Code -ne 0) {
    Write-Host "Bootstrap   : unable to inspect local changes" -ForegroundColor Red
    exit 0
}

$localPaths = @()
foreach ($line in ($localStatus.Text -split "\r?\n")) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) { continue }
    $payload = $line.Substring(3).Trim()
    if ($payload -match " -> ") {
        Write-Host "Bootstrap   : rename/copy detected; no automatic merge" -ForegroundColor Yellow
        exit 0
    }
    if ($payload) { $localPaths += $payload.Replace("\", "/") }
}
$localPaths = @($localPaths | Sort-Object -Unique)

$incomingPaths = Get-Paths @("-c", "core.quotepath=false", "diff", "--name-only", "HEAD..origin/$Branch")
if ($null -eq $incomingPaths) {
    Write-Host "Bootstrap   : unable to inspect incoming paths" -ForegroundColor Red
    exit 0
}

if ($localPaths.Count -gt 0) {
    $set = @{}
    foreach ($path in $localPaths) { $set[$path] = $true }
    $conflicts = @($incomingPaths | Where-Object { $set.ContainsKey($_) })
    if ($conflicts.Count -gt 0) {
        $sample = ($conflicts | Select-Object -First 4) -join ", "
        Write-Host "Bootstrap   : remote update overlaps local work; merge skipped" -ForegroundColor Yellow
        Write-Host "              $sample" -ForegroundColor DarkYellow
        exit 0
    }
}

$merge = Invoke-Git @("merge", "--ff-only", "--quiet", "origin/$Branch")
if ($merge.Code -ne 0) {
    Write-Host "Bootstrap   : safe fast-forward failed; local work preserved" -ForegroundColor Red
    exit 0
}

Write-Host "Bootstrap   : updated $behind commit(s); local task work preserved" -ForegroundColor Green
Repair-DeferredBrowserFailedHead
exit 0
