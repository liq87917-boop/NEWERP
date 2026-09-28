$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root '.ai\config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$executor = $config.pipeline.executor_worktree
if ($executor -and $executor.enabled -ne $false) {
    $candidate = [string]$executor.path
    if (-not [System.IO.Path]::IsPathRooted($candidate)) { $candidate = Join-Path (Split-Path -Parent $root) $candidate }
    if (Test-Path (Join-Path $candidate '.git')) { $root = $candidate }
}
Push-Location $root
try { & py -3 (Join-Path $root 'scripts\ai_orchestrator.py') status; exit $LASTEXITCODE }
finally { Pop-Location }
