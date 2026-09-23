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

$fetch = Invoke-Git @("fetch", "--quiet", "origin", ("+refs/heads/{0}:refs/remotes/origin/{0}" -f $Branch))
if ($fetch.Code -ne 0) {
    Write-Host "Bootstrap   : git fetch failed; starting with local code" -ForegroundColor Yellow
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
    Write-Host "Bootstrap   : local/remote diverged (ahead $ahead, behind $behind); no merge" -ForegroundColor Red
    exit 0
}
if ($ahead -gt 0) {
    Write-Host "Bootstrap   : local ahead by $ahead commit(s); no pull needed" -ForegroundColor Yellow
    exit 0
}
if ($behind -eq 0) {
    Write-Host "Bootstrap   : up to date" -ForegroundColor Green
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
exit 0
