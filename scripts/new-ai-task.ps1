param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Description,
    [Parameter(Mandatory=$true)][string[]]$AcceptanceCriteria,
    [Parameter(Mandatory=$true)][string[]]$AllowedPaths,
    [ValidateSet('safe','build_only','integration','ui')][string]$ValidationProfile = 'safe',
    [ValidateSet('low','medium','high')][string]$Risk = 'low'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$arguments = @((Join-Path $PSScriptRoot 'ai_pipeline.py'), 'create', '--title', $Title, '--description', $Description, '--profile', $ValidationProfile, '--risk', $Risk)
foreach ($criterion in $AcceptanceCriteria) { $arguments += @('--accept', $criterion) }
foreach ($path in $AllowedPaths) { $arguments += @('--allow', $path) }
Push-Location $root
try { & py -3 @arguments; exit $LASTEXITCODE }
finally { Pop-Location }
