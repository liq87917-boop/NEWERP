$ErrorActionPreference = 'Continue'

try {
    $css = (Invoke-WebRequest -Uri 'http://localhost:5059/css/app.css' -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue).Content
} catch {
    Write-Host 'API not responding'
    exit 1
}
if (-not $css) { Write-Host 'CSS empty'; exit 1 }

Write-Host ('---- CSS size: ' + $css.Length + ' bytes ----')

Write-Host ''
Write-Host '---- .login-page appearances ----'
Select-String -InputObject $css -Pattern 'login-page' -AllMatches | Select-Object -First 12 Matches | ForEach-Object {
    $s = $_.Matches[0]
    $start = $s.Index
    $len = [Math]::Min(180, $css.Length - $start)
    $ctx = ($css.Substring($start, $len) -replace "`n", ' ' -replace "`r", '')
    Write-Host ('AT ' + $start + ': ' + ($ctx.Substring(0, [Math]::Min(180, $ctx.Length))))
}

Write-Host ''
Write-Host '---- body global styles ----'
Select-String -InputObject $css -Pattern '^body[\.\s\{]' | Select-Object -First 10 | ForEach-Object {
    Write-Host ('L' + $_.LineNumber + ': ' + $_.Line.Trim())
}

Write-Host ''
Write-Host '---- .app-page / .app-main / .page-content styles ----'
Select-String -InputObject $css -Pattern '^\.(app-page|app-main|page-content)\b' | Select-Object -First 15 | ForEach-Object {
    Write-Host ('L' + $_.LineNumber + ': ' + $_.Line.Trim())
}

Write-Host ''
Write-Host '---- login-page display/flex/grid related ----'
Select-String -InputObject $css -Pattern 'login-page.*(flex|grid)' | Select-Object -First 5 | ForEach-Object {
    Write-Host ('L' + $_.LineNumber + ': ' + $_.Line.Trim())
}

Write-Host ''
Write-Host '---- top of HTML ----'
try {
    $h = (Invoke-WebRequest -Uri 'http://localhost:5059/' -UseBasicParsing -TimeoutSec 3).Content
} catch { Write-Host 'HTML failed'; exit 1 }
$idx = $h.IndexOf('<body')
if ($idx -gt 0) {
    Write-Host 'Body opening:'
    Write-Host ($h.Substring($idx, [Math]::Min(280, $h.Length - $idx)))
}

Write-Host ''
Write-Host '---- All <div class=...> first level ----'
Select-String -InputObject $h -Pattern '<div class="(login-page|app-page|app|login-banner|login-card)' | Select-Object -First 5 | ForEach-Object {
    Write-Host ('L' + $_.LineNumber + ': ' + $_.Line.Trim())
}