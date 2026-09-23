$ErrorActionPreference = 'Stop'
$path = 'D:\VSCodeProject\MinMaxProject\NEWERP\src\ERP.Api\wwwroot\index.html'
$content = Get-Content -Path $path -Raw -Encoding UTF8

# script src 加 ?v=20260918（仅当还没 ? 时）
$scriptRe = [regex]'(<script\s+src=")(/js/[^"?]+)(">)'
$content = $scriptRe.Replace($content, {
  param($m)
  $url = $m.Groups[2].Value
  if ($url -notmatch '\?v=') { $url + '?v=20260918' } else { $url }
}, {
    param($g)
    $g.Groups[1].Value + $g.Groups[2].Value + $g.Groups[3].Value
})

# link href 加 ?v=20260918（仅当还没 ? 时）
$cssRe = [regex]'(<link\s+rel="stylesheet"\s+href=")(/css/[^"?]+)("\s*/?>)'
$content = $cssRe.Replace($content, {
  param($m)
  $url = $m.Groups[2].Value
  if ($url -notmatch '\?v=') { $url + '?v=20260918' } else { $url }
}, {
    param($g)
    $g.Groups[1].Value + $g.Groups[2].Value + $g.Groups[3].Value
})

Set-Content -Path $path -Value $content -Encoding UTF8 -NoNewline
Write-Host 'index.html cache-busted'

# 校验
$hits = ([regex]::Matches($content, '\?v=20260918')).Count
Write-Host ('Total ?v= occurrences: ' + $hits)