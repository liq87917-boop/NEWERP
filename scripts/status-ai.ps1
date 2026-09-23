$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try { & py -3 (Join-Path $PSScriptRoot 'ai_orchestrator.py') status; exit $LASTEXITCODE }
finally { Pop-Location }
