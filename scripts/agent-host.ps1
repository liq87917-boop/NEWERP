param(
    [int]$RefreshSeconds = 2,
    [int]$SyncSeconds = 30,
    [switch]$Once,
    [switch]$NoExecute
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$statePath = Join-Path $root '.ai\PROJECT_STATE.json'
$configPath = Join-Path $root '.ai\config.json'
$tasksDir = Join-Path $root '.ai\tasks'
$logsDir = Join-Path $root '.ai\logs'
$pipelineScript = Join-Path $scriptDir 'run-pipeline.ps1'
$orchestratorScript = Join-Path $scriptDir 'ai_orchestrator.py'
$outLog = Join-Path $logsDir 'agent-pipeline.out.log'
$errLog = Join-Path $logsDir 'agent-pipeline.err.log'
$managedBranches = @('main', 'master', 'develop')

if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
} catch {}

$createdNew = $false
$mutex = [System.Threading.Mutex]::new($true, 'NEWERP_AI_AGENT', [ref]$createdNew)
if (-not $createdNew) {
    Write-Host 'NEWERP AI Agent is already running in another window.' -ForegroundColor Yellow
    Read-Host 'Press Enter to close'
    exit 2
}

$pipelineProcess = $null
$lastPipelineExit = $null
$lastPipelineEndedAt = $null
$lastSyncAt = Get-Date '2000-01-01'
$lastSyncMessage = 'not synced yet'
$lastCiText = 'GitHub CLI not checked'
$lastCiAt = Get-Date '2000-01-01'
$lastRecoveryAttemptKey = $null
$selfRestartRequested = $false
$screenInitialized = $false
$lastScreen = @()
$lastScreenWidth = 0
$lastScreenHeight = 0
$screenFallbackSignature = $null
$originalForeground = $null
try { $originalForeground = [Console]::ForegroundColor } catch {}

function Invoke-Git {
    param([string[]]$Arguments)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $result = & git @Arguments 2>$null
        return @($LASTEXITCODE, ($result -join [Environment]::NewLine))
    } finally {
        $ErrorActionPreference = $old
    }
}

function Get-ProjectState {
    if (-not (Test-Path $statePath)) { return $null }
    try {
        return (Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-Tasks {
    $rows = @()
    if (-not (Test-Path $tasksDir)) { return $rows }
    Get-ChildItem $tasksDir -Filter 'ERP-*.json' -File | Sort-Object Name | ForEach-Object {
        try {
            $task = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $rows += $task
        } catch {}
    }
    return $rows
}

function Get-QueueHead {
    param([object[]]$Tasks)
    foreach ($task in $Tasks) {
        if ($task.status -notin @('completed', 'deferred', 'skipped')) {
            return $task
        }
    }
    return $null
}

function Test-TaskRunnable {
    param($Task)
    if ($null -eq $Task) { return $false }
    if ($Task.status -notin @('pending', 'retry')) { return $false }
    if ($Task.PSObject.Properties.Name -contains 'auto_start') {
        if ($Task.auto_start -eq $false) { return $false }
    }

    $gate = $Task.human_gate
    $gateLevel = 'L1'
    $gateRequired = $false
    $gateStatus = 'not_required'

    if ($null -ne $gate) {
        if ($gate.level) { $gateLevel = [string]$gate.level }
        if ($gate.required -eq $true) { $gateRequired = $true }
        if ($gate.status) { $gateStatus = [string]$gate.status }
    }

    $requiresHuman = $false
    if ($Task.PSObject.Properties.Name -contains 'requires_human_approval') {
        $requiresHuman = ($Task.requires_human_approval -eq $true)
    }

    if ($gateLevel.ToUpperInvariant() -in @('L3', 'L4') -or $requiresHuman) {
        return $gateStatus -eq 'approved'
    }

    if ($gateRequired) {
        return $gateStatus -in @('approved', 'not_required', 'ai_reviewed')
    }

    return $true
}

function Get-GitInfo {
    $branchResult = Invoke-Git @('branch', '--show-current')
    $shaResult = Invoke-Git @('rev-parse', '--short', 'HEAD')
    $statusResult = Invoke-Git @('status', '--porcelain')
    $branch = $branchResult[1].Trim()
    $sha = $shaResult[1].Trim()
    $dirty = -not [string]::IsNullOrWhiteSpace($statusResult[1])

    $ahead = $null
    $behind = $null
    if ($branch) {
        $countResult = Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...origin/$branch")
        if ($countResult[0] -eq 0 -and $countResult[1]) {
            $parts = ($countResult[1] -split '\s+') | Where-Object { $_ -ne '' }
            if ($parts.Count -ge 2) {
                $ahead = [int]$parts[0]
                $behind = [int]$parts[1]
            }
        }
    }

    return [pscustomobject]@{
        Branch = $branch
        Sha = $sha
        Dirty = $dirty
        Ahead = $ahead
        Behind = $behind
    }
}

function Get-LocalChangedPaths {
    $result = Invoke-Git @('-c', 'core.quotepath=false', 'status', '--porcelain', '--untracked-files=all')
    if ($result[0] -ne 0) { return $null }

    $paths = @()
    foreach ($line in ($result[1] -split "\r?\n")) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) { continue }
        $payload = $line.Substring(3).Trim()
        # Rename/copy porcelain records need two-path conflict analysis. Stay fail-closed.
        if ($payload -match ' -> ') { return $null }
        if ($payload) { $paths += $payload.Replace('\', '/') }
    }
    return @($paths | Sort-Object -Unique)
}

function Get-IncomingPaths {
    param([string]$Branch)

    $result = Invoke-Git @('-c', 'core.quotepath=false', 'diff', '--name-only', "HEAD..origin/$Branch")
    if ($result[0] -ne 0) { return $null }
    return @(($result[1] -split "\r?\n") |
        ForEach-Object { $_.Trim().Replace('\', '/') } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
}

function Sync-Repository {
    param($GitInfo)

    if ($pipelineProcess -and -not $pipelineProcess.HasExited) {
        return 'sync skipped: pipeline running'
    }
    if ($GitInfo.Branch -notin $managedBranches) {
        return "sync skipped: unmanaged branch $($GitInfo.Branch)"
    }

    $env:GIT_TERMINAL_PROMPT = '0'
    $fetch = Invoke-Git @('fetch', '--quiet', 'origin', ('+refs/heads/{0}:refs/remotes/origin/{0}' -f $GitInfo.Branch))
    if ($fetch[0] -ne 0) {
        # Prefer the already-authenticated GitHub CLI as a credential repair path
        # for unattended private-repository synchronization.
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if ($gh) {
            $oldPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & gh auth status --hostname github.com *> $null
                if ($LASTEXITCODE -eq 0) {
                    & gh auth setup-git --hostname github.com *> $null
                    if ($LASTEXITCODE -eq 0) {
                        $fetch = Invoke-Git @('fetch', '--quiet', 'origin', $GitInfo.Branch)
                    }
                }
            } finally {
                $ErrorActionPreference = $oldPreference
            }
        }
    }
    if ($fetch[0] -ne 0) {
        return 'git fetch failed after credential repair'
    }

    $counts = Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...origin/$($GitInfo.Branch)")
    if ($counts[0] -ne 0) {
        return 'unable to compare local/remote'
    }

    $parts = ($counts[1] -split '\s+') | Where-Object { $_ -ne '' }
    if ($parts.Count -lt 2) { return 'remote comparison unavailable' }
    $ahead = [int]$parts[0]
    $behind = [int]$parts[1]

    if ($ahead -gt 0 -and $behind -gt 0) {
        return "local/remote diverged: ahead $ahead, behind $behind"
    }
    if ($ahead -gt 0) {
        return "local ahead by $ahead commit(s); pipeline push will sync"
    }
    if ($behind -eq 0) {
        return 'up to date'
    }

    # A task can legitimately leave a dirty tree while waiting for guarded recovery.
    # Permit fast-forwarding remote control-plane fixes only when none of the incoming
    # paths overlap the local dirty paths. Git itself remains the final fail-closed check.
    if ($GitInfo.Dirty) {
        $localPaths = Get-LocalChangedPaths
        $incomingPaths = Get-IncomingPaths $GitInfo.Branch
        if ($null -eq $localPaths -or $null -eq $incomingPaths) {
            return 'sync skipped: dirty tree could not be compared safely'
        }

        $localSet = @{}
        foreach ($path in $localPaths) { $localSet[$path] = $true }
        $conflicts = @($incomingPaths | Where-Object { $localSet.ContainsKey($_) })
        if ($conflicts.Count -gt 0) {
            $sample = ($conflicts | Select-Object -First 3) -join ', '
            return "sync skipped: remote overlaps local changes ($sample)"
        }

        $merge = Invoke-Git @('merge', '--ff-only', '--quiet', "origin/$($GitInfo.Branch)")
        if ($merge[0] -eq 0) {
            if ($incomingPaths -contains 'scripts/agent-host.ps1' -or $incomingPaths -contains 'scripts/agent-bootstrap.ps1' -or $incomingPaths -contains 'start_agent.bat') {
                $script:selfRestartRequested = $true
            }
            return "updated $behind remote commit(s), preserved $($localPaths.Count) local change(s)"
        }
        return 'safe dirty-tree fast-forward failed; local work preserved'
    }

    $incomingPaths = Get-IncomingPaths $GitInfo.Branch
    $merge = Invoke-Git @('merge', '--ff-only', '--quiet', "origin/$($GitInfo.Branch)")
    if ($merge[0] -eq 0) {
        if ($null -ne $incomingPaths -and ($incomingPaths -contains 'scripts/agent-host.ps1' -or $incomingPaths -contains 'start_agent.bat')) {
            $script:selfRestartRequested = $true
        }
        return "updated from origin/$($GitInfo.Branch) ($behind commit(s))"
    }
    return 'git merge --ff-only failed'
}

function Start-Pipeline {
    param([switch]$RecoverPathGuard)

    if (Test-Path $outLog) { Remove-Item $outLog -Force -ErrorAction SilentlyContinue }
    if (Test-Path $errLog) { Remove-Item $errLog -Force -ErrorAction SilentlyContinue }

    if ($RecoverPathGuard) {
        $arguments = @(
            '-NoLogo',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-Command', ('"& py -3 ''{0}'' run-next; exit $LASTEXITCODE"' -f $orchestratorScript)
        ) -join ' '
    } else {
        $arguments = @(
            '-NoLogo',
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', ('"{0}"' -f $pipelineScript)
        ) -join ' '
    }

    $startArgs = @{
        FilePath = 'powershell.exe'
        ArgumentList = $arguments
        WorkingDirectory = $root
        RedirectStandardOutput = $outLog
        RedirectStandardError = $errLog
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    return Start-Process @startArgs
}

function Get-CiText {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return 'unavailable (optional: install/authenticate GitHub CLI)' }

    try {
        $raw = & gh run list --repo liq87917-boop/NEWERP --workflow 'Build & Test' --branch main --limit 1 --json databaseId,status,conclusion,displayTitle,headSha 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
            return 'GitHub CLI present but not authenticated/available'
        }
        $rows = $raw | ConvertFrom-Json
        if ($null -eq $rows -or $rows.Count -eq 0) { return 'no workflow run found' }
        $run = $rows[0]
        $shortSha = [string]$run.headSha
        if ($shortSha.Length -gt 7) { $shortSha = $shortSha.Substring(0, 7) }
        $result = [string]$run.status
        if ($run.conclusion) { $result += "/$($run.conclusion)" }
        return "#$($run.databaseId) $result $shortSha - $($run.displayTitle)"
    } catch {
        return 'unable to read GitHub Actions status'
    }
}

function Test-RecoverablePathGuard {
    param($State, $Head)

    if (-not $State -or -not $Head) { return $false }
    if ($State.phase -ne 'human_attention') { return $false }
    if ($State.finish_reason -ne 'attempts_exhausted') { return $false }
    if ($Head.status -ne 'failed') { return $false }
    $blockerText = [string]$State.blocker
    return $blockerText.StartsWith('Path guard failed:')
}

function Test-RecoverableInterruptedTask {
    param($State, $Head)

    if (-not $State -or -not $Head) { return $false }
    if ($State.phase -notin @('developing', 'browser_acceptance')) { return $false }
    return $Head.status -in @('in_progress', 'code_ready')
}

function Test-BrowserAcceptanceDeferred {
    if (-not (Test-Path $configPath)) { return $false }
    try {
        $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        return ($null -ne $config.completion_policy -and $config.completion_policy.defer_browser_during_development -eq $true)
    } catch {
        return $false
    }
}

function Test-RecoverableDeferredFailedHead {
    param($State, $Head)

    if (-not $Head) { return $false }
    if (-not (Test-BrowserAcceptanceDeferred)) { return $false }
    return $Head.status -eq 'failed'
}

function Test-RecoverableBrowserFailure {
    param($State, $Head)

    if (-not $State -or -not $Head) { return $false }
    if ($State.phase -ne 'human_attention') { return $false }
    if ($State.finish_reason -ne 'attempts_exhausted') { return $false }
    if ($Head.status -ne 'failed') { return $false }
    $blockerText = [string]$State.blocker
    if (-not $blockerText.StartsWith('Real-browser acceptance failed')) { return $false }
    $cycles = 0
    if (Test-BrowserAcceptanceDeferred) {
        # Browser/UI is non-blocking during feature development. Recover an
        # existing browser-only failure even if its old recovery budget ended.
        return $true
    }
    if ($Head.PSObject.Properties.Name -contains 'browser_recovery_cycles') {
        $cycles = [int]$Head.browser_recovery_cycles
    }
    return $cycles -lt 2
}

function Get-AgentMode {
    param($State, $Head, $GitInfo)

    if ($State -and $State.conversation_control -and $State.conversation_control.paused -eq $true) {
        return 'PAUSED'
    }
    if ($pipelineProcess -and -not $pipelineProcess.HasExited) {
        return 'RUNNING'
    }
    if ((Test-RecoverablePathGuard $State $Head) -or (Test-RecoverableInterruptedTask $State $Head) -or (Test-RecoverableBrowserFailure $State $Head) -or (Test-RecoverableDeferredFailedHead $State $Head)) {
        return 'READY'
    }
    if ($State -and $State.phase -eq 'replenishing') {
        return 'REPLENISH'
    }
    if ($State -and $State.phase -in @('blocked', 'human_attention', 'waiting_human_gate', 'push_pending')) {
        return 'ATTENTION'
    }
    if ($GitInfo.Dirty) {
        return 'ATTENTION'
    }
    if ($Head -and (Test-TaskRunnable $Head)) {
        return 'READY'
    }
    return 'IDLE'
}

function New-StatusLine {
    param(
        [string]$Text = '',
        [string]$Color = 'Gray'
    )
    if ($null -eq $Text) { $Text = '' }
    # Never allow embedded CR/LF/tab characters into one cursor-positioned row.
    # Multiline blocker text previously moved the console cursor and left duplicated
    # headers/queue rows on screen even though the underlying data was correct.
    $Text = ($Text -replace "\r\n|\n|\r", " | ") -replace "\t", " "
    return [pscustomobject]@{ Text = $Text; Color = $Color }
}

function Fit-ConsoleText {
    param(
        [string]$Text,
        [int]$Width
    )

    if ($null -eq $Text) { $Text = '' }
    # Never render wider than the actual console. The previous minimum width of
    # 20 columns caused automatic line wrapping when the user resized the window
    # narrower than that, which corrupted all cursor-positioned rows.
    $usable = [Math]::Max(1, $Width - 1)
    if ($Text.Length -gt $usable) {
        if ($usable -le 3) { return $Text.Substring(0, $usable) }
        return $Text.Substring(0, $usable - 3) + '...'
    }
    return $Text.PadRight($usable)
}

function Get-StatusLines {
    param($State, [object[]]$Tasks, $Head, $GitInfo)

    $lines = @()
    $mode = Get-AgentMode $State $Head $GitInfo
    $modeColor = 'Gray'
    if ($mode -eq 'RUNNING') { $modeColor = 'Green' }
    elseif ($mode -eq 'READY') { $modeColor = 'Cyan' }
    elseif ($mode -eq 'REPLENISH') { $modeColor = 'DarkCyan' }
    elseif ($mode -eq 'PAUSED') { $modeColor = 'Yellow' }
    elseif ($mode -eq 'ATTENTION') { $modeColor = 'Red' }

    $lines += New-StatusLine '============================================================' 'DarkCyan'
    $lines += New-StatusLine (" NEWERP AI AGENT   [{0}]" -f $mode) $modeColor
    $lines += New-StatusLine '============================================================' 'DarkCyan'
    $lines += New-StatusLine (" Local time : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

    $lines += New-StatusLine (" Repository : {0}" -f $root)
    $lines += New-StatusLine (" Git        : {0} @ {1}" -f $GitInfo.Branch, $GitInfo.Sha)

    $gitState = 'clean'
    if ($GitInfo.Dirty) { $gitState = 'DIRTY' }
    $syncText = ''
    if ($null -ne $GitInfo.Ahead -and $null -ne $GitInfo.Behind) {
        $syncText = " | ahead $($GitInfo.Ahead), behind $($GitInfo.Behind)"
    }
    $gitColor = 'Gray'
    if ($GitInfo.Dirty) { $gitColor = 'Red' }
    $lines += New-StatusLine (" Worktree   : {0}{1}" -f $gitState, $syncText) $gitColor
    $lines += New-StatusLine (" Auto sync  : {0}" -f $lastSyncMessage)
    $lines += New-StatusLine (" GitHub CI  : {0}" -f $lastCiText)

    $lines += New-StatusLine ''
    $lines += New-StatusLine ' Project state' 'Cyan'
    if ($State) {
        $lines += New-StatusLine (" Phase      : {0}" -f $State.phase)
        $currentTask = '-'
        if ($State.current_task) { $currentTask = $State.current_task }
        $lastCompleted = '-'
        if ($State.last_completed_task) { $lastCompleted = $State.last_completed_task }
        $lines += New-StatusLine (" Current    : {0}" -f $currentTask)
        $lines += New-StatusLine (" Completed  : {0}" -f $lastCompleted)
        if ($State.validation) {
            $lines += New-StatusLine (" Validation : {0} / {1}" -f $State.validation.profile, $State.validation.status)
        }
        if ($State.browser_acceptance) {
            $lines += New-StatusLine (" Browser    : {0}" -f $State.browser_acceptance.status)
        }
        if ($State.blocker) {
            $blockerText = [string]$State.blocker
            $blockerParts = @($blockerText -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($blockerParts.Count -gt 0) {
                $lines += New-StatusLine (" Blocker    : {0}" -f $blockerParts[0]) 'Red'
                foreach ($part in ($blockerParts | Select-Object -Skip 1 -First 2)) {
                    $lines += New-StatusLine ("              > {0}" -f $part.Trim()) 'DarkRed'
                }
                if ($blockerParts.Count -gt 3) {
                    $lines += New-StatusLine ("              > ... {0} more line(s)" -f ($blockerParts.Count - 3)) 'DarkRed'
                }
            }
        }
        if ($State.conversation_control -and $State.conversation_control.paused -eq $true) {
            $lines += New-StatusLine (" Pause      : {0}" -f $State.conversation_control.pause_reason) 'Yellow'
        }
    } else {
        $lines += New-StatusLine ' PROJECT_STATE.json is missing or invalid.' 'Red'
    }

    $lines += New-StatusLine ''
    $lines += New-StatusLine ' Queue' 'Cyan'
    if ($Tasks.Count -eq 0) {
        $lines += New-StatusLine ' (empty)'
    } else {
        # Keep the dashboard inside the visible console window. Completed history
        # is already summarized above; the queue should focus on active/future work.
        $visible = @($Tasks | Where-Object { $_.status -notin @('completed', 'deferred', 'skipped') } | Select-Object -First 5)
        if ($visible.Count -eq 0) {
            $visible = @($Tasks | Select-Object -Last 2)
        }
        foreach ($task in $visible) {
            $marker = ' '
            if ($Head -and $task.id -eq $Head.id) { $marker = '>' }
            $line = " $marker {0,-8} {1,-12} {2}" -f $task.id, $task.status, $task.title
            $color = 'Gray'
            if ($task.status -eq 'completed') { $color = 'DarkGreen' }
            elseif ($task.status -in @('pending', 'retry', 'code_ready')) { $color = 'Cyan' }
            elseif ($task.status -in @('failed', 'blocked')) { $color = 'Red' }
            elseif ($task.status -eq 'deferred') { $color = 'DarkYellow' }
            $lines += New-StatusLine $line $color
        }
    }

    $lines += New-StatusLine ''
    $lines += New-StatusLine ' Local runner' 'Cyan'
    if ($pipelineProcess -and -not $pipelineProcess.HasExited) {
        $lines += New-StatusLine (" Pipeline   : running (PID {0})" -f $pipelineProcess.Id) 'Green'
    } elseif ($State -and $State.phase -eq 'replenishing') {
        $lines += New-StatusLine ' Pipeline   : rolling queue replenishment watch (agent remains active)' 'DarkCyan'
    } elseif ($null -ne $lastPipelineExit) {
        $pipelineColor = 'Gray'
        if ($lastPipelineExit -ne 0) { $pipelineColor = 'Red' }
        $lines += New-StatusLine (" Pipeline   : stopped, last exit={0} at {1}" -f $lastPipelineExit, $lastPipelineEndedAt) $pipelineColor
    } else {
        $lines += New-StatusLine ' Pipeline   : waiting for runnable task'
    }

    if ((Test-RecoverablePathGuard $State $Head) -or (Test-RecoverableInterruptedTask $State $Head) -or (Test-RecoverableBrowserFailure $State $Head) -or (Test-RecoverableDeferredFailedHead $State $Head)) {
        $recoveryState = 'eligible'
        if ($null -ne $lastPipelineExit -and $lastPipelineExit -ne 0) {
            $recoveryState = "last recovery exited $lastPipelineExit"
        }
        $lines += New-StatusLine (" Recovery   : {0} (existing work preserved)" -f $recoveryState) 'Yellow'
    }
    $lines += New-StatusLine ' Secrets    : hidden (never printed by this console)'

    $lines += New-StatusLine ''
    $lines += New-StatusLine ' Recent runner output' 'Cyan'
    $tail = @()
    $browserLog = $null
    if ($State -and [string]$State.blocker -match 'see\s+([^;]+ERP-\d+-browser-acceptance\.log)') {
        $browserLog = Join-Path $root $Matches[1]
    }
    if ($browserLog -and (Test-Path $browserLog)) {
        $tail += Get-Content $browserLog -Tail 5 -Encoding UTF8 -ErrorAction SilentlyContinue
    } else {
        if (Test-Path $outLog) {
            $tail += Get-Content $outLog -Tail 3 -Encoding UTF8 -ErrorAction SilentlyContinue
        }
        if (Test-Path $errLog) {
            $errTail = Get-Content $errLog -Tail 2 -Encoding UTF8 -ErrorAction SilentlyContinue
            foreach ($line in $errTail) {
                if ($line) { $tail += "[stderr] $line" }
            }
        }
    }
    if ($tail.Count -eq 0) {
        $lines += New-StatusLine ' (no runner output yet)'
    } else {
        foreach ($line in $tail) {
            $lines += New-StatusLine (" " + $line)
        }
    }

    $lines += New-StatusLine ''
    $lines += New-StatusLine (" Incremental refresh: {0}s | Remote sync: {1}s | Ctrl+C closes agent and child pipeline" -f $RefreshSeconds, $SyncSeconds) 'DarkGray'
    return $lines
}

function Write-Status {
    param($State, [object[]]$Tasks, $Head, $GitInfo)

    $lines = @(Get-StatusLines $State $Tasks $Head $GitInfo)

    # CI/log redirection does not have a stable interactive cursor. In that case
    # print once normally; interactive DOS/PowerShell windows use incremental redraw.
    $redirected = $false
    try { $redirected = [Console]::IsOutputRedirected } catch {}
    if ($Once -or $redirected) {
        foreach ($item in $lines) {
            Write-Host $item.Text -ForegroundColor $item.Color
        }
        return
    }

    try {
        # Always use the real console dimensions. Never substitute an artificial
        # larger width: writing wider than WindowWidth makes cmd.exe wrap lines,
        # which destroys cursor-positioned dashboard rendering after a resize.
        $width = [Math]::Max(2, [Console]::WindowWidth)
        $windowHeight = [Math]::Max(3, [Console]::WindowHeight)

        $resized = $script:screenInitialized -and (
            $script:lastScreenWidth -ne $width -or
            $script:lastScreenHeight -ne $windowHeight
        )

        # Clear once at startup, and once per actual resize. Normal refreshes stay
        # incremental and do not flash. A full redraw on resize removes wrapped
        # fragments and stale rows left by the old console geometry.
        if (-not $script:screenInitialized -or $resized) {
            try { [Console]::Clear() } catch { Clear-Host }
            try { [Console]::CursorVisible = $false } catch {}
            $script:screenInitialized = $true
            $script:lastScreen = @()
            $script:lastScreenWidth = $width
            $script:lastScreenHeight = $windowHeight
        }

        # Keep drawing inside the visible window. If the user makes the window
        # very short, show a compact footer rather than scrolling the viewport and
        # making the dashboard appear scrambled.
        $maxRows = [Math]::Max(1, $windowHeight - 1)
        $visibleLines = @($lines | Select-Object -First $maxRows)
        if ($lines.Count -gt $maxRows -and $maxRows -ge 1) {
            $hidden = $lines.Count - $maxRows + 1
            $visibleLines[$maxRows - 1] = New-StatusLine (" ... {0} more row(s); enlarge window height to view ..." -f $hidden) 'DarkGray'
        }
        $height = [Math]::Max($visibleLines.Count, $script:lastScreen.Count)

        $current = @()
        for ($i = 0; $i -lt $height; $i++) {
            $text = ''
            $colorName = 'Gray'
            if ($i -lt $visibleLines.Count) {
                $text = [string]$visibleLines[$i].Text
                $colorName = [string]$visibleLines[$i].Color
            }

            $rendered = Fit-ConsoleText $text $width
            $signature = "$colorName|$rendered"
            $oldSignature = $null
            if ($i -lt $script:lastScreen.Count) {
                $oldSignature = $script:lastScreen[$i]
            }

            if ($signature -ne $oldSignature) {
                [Console]::SetCursorPosition(0, $i)
                $oldColor = [Console]::ForegroundColor
                try {
                    [Console]::ForegroundColor = [Enum]::Parse([ConsoleColor], $colorName, $true)
                } catch {}
                [Console]::Write($rendered)
                try { [Console]::ForegroundColor = $oldColor } catch {}
            }
            $current += $signature
        }

        $script:lastScreen = $current
        $cursorRow = [Math]::Min([Math]::Max(0, $visibleLines.Count), $windowHeight - 1)
        [Console]::SetCursorPosition(0, $cursorRow)
    } catch {
        # Last-resort mode: avoid repeated full-screen redraws. Emit a compact status
        # line only when meaningful state changes, so even unusual console hosts do not flash.
        $mode = Get-AgentMode $State $Head $GitInfo
        $headId = '-'
        $headStatus = '-'
        if ($Head) {
            $headId = $Head.id
            $headStatus = $Head.status
        }
        $signature = "$mode|$($State.phase)|$headId|$headStatus|$($GitInfo.Branch)|$($GitInfo.Sha)|$lastSyncMessage|$lastCiText"
        if ($signature -ne $script:screenFallbackSignature) {
            Write-Host ("[{0}] {1} | task {2}/{3} | git {4}@{5} | {6}" -f
                (Get-Date -Format 'HH:mm:ss'), $mode, $headId, $headStatus, $GitInfo.Branch, $GitInfo.Sha, $lastSyncMessage)
            $script:screenFallbackSignature = $signature
        }
    }
}

Set-Location $root

try {
    while ($true) {
        if ($pipelineProcess -and $pipelineProcess.HasExited) {
            try { $lastPipelineExit = $pipelineProcess.ExitCode } catch { $lastPipelineExit = -1 }
            $lastPipelineEndedAt = Get-Date -Format 'HH:mm:ss'
            $pipelineProcess.Dispose()
            $pipelineProcess = $null
            $lastSyncAt = Get-Date '2000-01-01'
        }

        $state = Get-ProjectState
        $tasks = @(Get-Tasks)
        $head = Get-QueueHead $tasks
        $gitInfo = Get-GitInfo

        # Paint local state before any network operation. Git fetch or GitHub CLI
        # can take time on Windows; without this the DOS window looked completely
        # blank even though the agent process was alive.
        if (-not $script:screenInitialized) {
            Write-Status $state $tasks $head $gitInfo
        }

        if (((Get-Date) - $lastSyncAt).TotalSeconds -ge $SyncSeconds) {
            $lastSyncMessage = Sync-Repository $gitInfo
            $lastSyncAt = Get-Date
            $state = Get-ProjectState
            $tasks = @(Get-Tasks)
            $head = Get-QueueHead $tasks
            $gitInfo = Get-GitInfo
        }

        if (((Get-Date) - $lastCiAt).TotalSeconds -ge 20) {
            $lastCiText = Get-CiText
            $lastCiAt = Get-Date
        }

        $paused = $false
        if ($state -and $state.conversation_control) {
            $paused = ($state.conversation_control.paused -eq $true)
        }

        $recoverPush = $false
        if ($state -and $state.phase -eq 'push_pending') { $recoverPush = $true }

        $recoverPathGuard = Test-RecoverablePathGuard $state $head
        $recoverInterrupted = Test-RecoverableInterruptedTask $state $head
        $recoverBrowser = Test-RecoverableBrowserFailure $state $head
        $recoverDeferredFailedHead = Test-RecoverableDeferredFailedHead $state $head
        $recoverExisting = $recoverPathGuard -or $recoverInterrupted -or $recoverBrowser -or $recoverDeferredFailedHead
        $recoveryKey = $null
        if ($recoverExisting) {
            $recoveryKey = "$($head.id)|$($gitInfo.Sha)|$($state.phase)|$([string]$state.blocker)"
        }

        $canStartWithDirty = $recoverExisting -and ($lastRecoveryAttemptKey -ne $recoveryKey)
        $worktreeAllowsStart = (-not $gitInfo.Dirty) -or $canStartWithDirty

        if (-not $NoExecute -and -not $pipelineProcess -and -not $paused -and $worktreeAllowsStart -and $gitInfo.Branch -in $managedBranches) {
            if ($recoverPush -or $recoverExisting -or (Test-TaskRunnable $head)) {
                try {
                    if ($recoverExisting) {
                        $lastRecoveryAttemptKey = $recoveryKey
                        $pipelineProcess = Start-Pipeline -RecoverPathGuard
                    } else {
                        $pipelineProcess = Start-Pipeline
                    }
                } catch {
                    $lastPipelineExit = -1
                    $lastPipelineEndedAt = Get-Date -Format 'HH:mm:ss'
                    Add-Content -Path $errLog -Value ("Unable to start pipeline: " + $_.Exception.Message)
                }
            }
        }

        Write-Status $state $tasks $head $gitInfo
        if ($Once) { break }
        if ($script:selfRestartRequested) {
            try { [Console]::CursorVisible = $true } catch {}
            exit 75
        }
        Start-Sleep -Seconds ([Math]::Max(1, $RefreshSeconds))
    }
}
finally {
    if ($pipelineProcess -and -not $pipelineProcess.HasExited) {
        try {
            Stop-Process -Id $pipelineProcess.Id -Force -ErrorAction SilentlyContinue
        } catch {}
    }
    try {
        if (-not $Once -and -not [Console]::IsOutputRedirected) {
            [Console]::CursorVisible = $true
            if ($null -ne $originalForeground) { [Console]::ForegroundColor = $originalForeground }
        }
    } catch {}
    try { $mutex.ReleaseMutex() } catch {}
    try { $mutex.Dispose() } catch {}
}

if ($Once) { exit 0 }
