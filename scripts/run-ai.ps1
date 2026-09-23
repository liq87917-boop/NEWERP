param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$argsList = @((Join-Path $PSScriptRoot 'ai_orchestrator.py'), 'run-next')
if ($DryRun) { $argsList += '--dry-run' }
Push-Location $root
try { & py -3 @argsList; exit $LASTEXITCODE }
finally { Pop-Location }
