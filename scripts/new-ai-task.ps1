param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Description,
    [Parameter(Mandatory=$true)][string[]]$AcceptanceCriteria,
    [Parameter(Mandatory=$true)][string[]]$AllowedPaths,
    [ValidateSet('safe','build_only','integration','ui')][string]$ValidationProfile = 'safe',
    [ValidateSet('low','medium','high')][string]$Risk = 'low',
    [string[]]$DependsOn = @(),
    [ValidateSet('L1','L2','L3','L4')][string]$HumanGate = 'L1',
    [ValidateSet('browser','control_plane')][string]$CompletionMode = 'browser',
    [string[]]$BrowserScenarios = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$arguments = @((Join-Path $PSScriptRoot 'ai_pipeline.py'), 'create', '--title', $Title, '--description', $Description, '--profile', $ValidationProfile, '--risk', $Risk, '--gate', $HumanGate, '--completion-mode', $CompletionMode)
foreach ($criterion in $AcceptanceCriteria) { $arguments += @('--accept', $criterion) }
foreach ($path in $AllowedPaths) { $arguments += @('--allow', $path) }
foreach ($dependency in $DependsOn) { $arguments += @('--depends-on', $dependency) }
foreach ($scenario in $BrowserScenarios) { $arguments += @('--browser-scenario', $scenario) }
Push-Location $root
try { & py -3 @arguments; exit $LASTEXITCODE }
finally { Pop-Location }
