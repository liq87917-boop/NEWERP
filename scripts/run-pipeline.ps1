$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try { & py -3 (Join-Path $PSScriptRoot 'ai_pipeline.py') run; exit $LASTEXITCODE }
finally { Pop-Location }
