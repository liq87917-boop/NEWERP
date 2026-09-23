$css = (Invoke-WebRequest -Uri 'http://localhost:5059/css/app.css' -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue).Content
if (-not $css) { Write-Host 'CSS not loaded'; exit 1 }

Write-Host ('CSS size: ' + $css.Length + ' bytes')

$idx = $css.IndexOf('.btn-block')
if ($idx -gt 0) {
    $ctx = $css.Substring($idx, [Math]::Min(80, $css.Length - $idx))
    Write-Host ('-- .btn-block at ' + $idx + ':')
    Write-Host $ctx
}

Write-Host ''
$idx2 = $css.IndexOf('.input-wrap {')
if ($idx2 -gt 0) {
    $ctx = $css.Substring($idx2, [Math]::Min(280, $css.Length - $idx2))
    Write-Host ('-- .input-wrap at ' + $idx2 + ':')
    Write-Host $ctx
}

Write-Host ''
$idx3 = $css.IndexOf('.login-card {')
$idx4 = $css.IndexOf('.login-card {', $idx3 + 1)
Write-Host ('-- main .login-card at ' + $idx3 + ':')
Write-Host $css.Substring($idx3, [Math]::Min(300, $css.Length - $idx3))