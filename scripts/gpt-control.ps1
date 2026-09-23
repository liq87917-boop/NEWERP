param(
    [Parameter(Mandatory=$true)][ValidateSet('status','record','pause','resume')][string]$Command,
    [string]$Intent,
    [string]$Summary,
    [string]$By = 'GPT',
    [string]$ConversationId,
    [string]$MessageId
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$arguments = @((Join-Path $PSScriptRoot 'gpt_project_control.py'), $Command)
if ($Command -ne 'status') {
    if (-not $Summary) { throw '-Summary is required.' }
    $arguments += @('--summary', $Summary, '--by', $By)
    if ($ConversationId) { $arguments += @('--conversation-id', $ConversationId) }
    if ($MessageId) { $arguments += @('--message-id', $MessageId) }
    if ($Command -eq 'record') {
        if (-not $Intent) { throw '-Intent is required for record.' }
        $arguments += @('--intent', $Intent)
    }
}
Push-Location $root
try { & py -3 @arguments; exit $LASTEXITCODE }
finally { Pop-Location }
