$ErrorActionPreference = 'Stop'

$css = (Invoke-WebRequest -Uri 'http://localhost:5059/css/app.css' -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue).Content
if (-not $css) { Write-Host 'CSS not loaded'; exit 1 }

# Strip comments first
$noComments = [regex]::Replace($css, '/\*[\s\S]*?\*/', '')

# Count braces
$openCount  = ([regex]::Matches($noComments, '{')).Count
$closeCount = ([regex]::Matches($noComments, '}')).Count

Write-Host ('Open  braces: ' + $openCount)
Write-Host ('Close braces: ' + $closeCount)
Write-Host ('Diff: ' + ($openCount - $closeCount))

if ($openCount -ne $closeCount) {
    Write-Host ''
    Write-Host '---- Detailed brace positions (first 30 mismatches) ----'

    # Find a position with stack tracking
    $stack = New-Object System.Collections.Generic.Stack[int]
    $lines = $css -split "`r?`n"

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        # Strip comment in this line
        $idx = $line.indexOf('/*')
        $endIdx = $line.indexOf('*/')
        if ($idx -ge 0 -and $endIdx -gt $idx) {
            $line = $line.Substring(0, $idx) + $line.Substring($endIdx + 2)
        }

        for ($j = 0; $j -lt $line.Length; $j++) {
            $c = $line[$j]
            if ($c -eq '{') {
                $stack.Push($i + 1)
            } elseif ($c -eq '}') {
                if ($stack.Count -gt 0) {
                    $stack.Pop() | Out-Null
                } else {
                    Write-Host ('UNMATCHED CLOSE at line ' + ($i + 1) + ' col ' + ($j + 1) + ': ' + $line.Trim())
                }
            }
        }
    }

    if ($stack.Count -gt 0) {
        Write-Host ''
        Write-Host '---- Unclosed opens (last 30) ----'
        $stackArr = $stack.ToArray()
        [array]::Reverse($stackArr)
        $stackArr | Select-Object -First 30 | ForEach-Object {
            Write-Host ('Unclosed brace opened at line ' + $_)
        }
    }
}