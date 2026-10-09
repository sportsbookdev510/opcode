# Refresh the local opcode.so mirror from the public site.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::DefaultConnectionLimit = 32
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$origin = "https://opcode.so"

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $true
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(90)
$client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")
$client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")

$seen = @{}
$queue = New-Object System.Collections.Generic.Queue[string]
$saved = 0
$failed = New-Object System.Collections.Generic.List[string]

function Add-SitePath([string]$raw) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return }
  $path = $raw.Trim()
  if ($path.StartsWith("https://opcode.so")) { $path = $path.Substring(16) }
  elseif ($path.StartsWith("http://opcode.so")) { $path = $path.Substring(15) }
  elseif ($path.StartsWith("//opcode.so")) { $path = $path.Substring(11) }
  if ($path.StartsWith("static/")) { $path = "/_next/" + $path }
  if (-not $path.StartsWith("/")) { return }
  $hash = $path.IndexOf("#")
  if ($hash -ge 0) { $path = $path.Substring(0, $hash) }
  $q = $path.IndexOf("?")
  if ($q -ge 0) { $path = $path.Substring(0, $q) }
  try { $path = [Uri]::UnescapeDataString($path) } catch { return }
  if ($path.Length -gt 1) { $path = $path.TrimEnd("/") }
  if ($path.Contains("..") -or $path.Contains("\") -or $path.Contains(" ")) { return }
  if ($path.Length -gt 240) { return }
  if ($path -match "^/(api|ops|paper-source)(/|$)") {
    if ($path -notmatch "^/api/launch/images/[A-Za-z0-9]+$") { return }
  }
  if ($seen.ContainsKey($path)) { return }
  $seen[$path] = $true
  $queue.Enqueue($path)
}

function Get-LinkedPaths([string]$text) {
  $found = New-Object System.Collections.Generic.List[string]
  foreach ($m in [regex]::Matches($text, '(?i)(?:href|src|poster)\s*=\s*"([^"]+)"')) { $found.Add($m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($text, "(?i)(?:href|src|poster)\s*=\s*'([^']+)'")) { $found.Add($m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($text, '/_next/static/[A-Za-z0-9_./-]+')) { $found.Add($m.Value) }
  foreach ($m in [regex]::Matches($text, '(?<![A-Za-z0-9_])static/(?:chunks|media)/[A-Za-z0-9_./-]+')) { $found.Add($m.Value) }
  foreach ($m in [regex]::Matches($text, '/images/[A-Za-z0-9_./-]+')) { $found.Add($m.Value) }
  foreach ($m in [regex]::Matches($text, '/api/launch/images/[A-Za-z0-9]+')) { $found.Add($m.Value) }
  foreach ($m in [regex]::Matches($text, '\\"href\\":\\"(/[^\\"]+)')) { $found.Add($m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($text, '(?i)url\(\s*[''"]?([^''")\s]+)')) { $found.Add($m.Groups[1].Value) }
  return $found
}

function Get-SavePath([string]$urlPath, [string]$mediaType) {
  $rel = $urlPath.TrimStart("/")
  $isAsset = $rel -match '\.(js|css|png|jpe?g|gif|webp|svg|ico|woff2?|ttf|pdf|map|txt|xml|webmanifest)$' -or
    $rel.StartsWith("_next/") -or $rel.StartsWith("images/") -or $rel.StartsWith("api/launch/images/") -or
    $mediaType -match '^(image/|font/|application/pdf|application/javascript|text/css|text/plain|application/xml)'
  if (-not $isAsset -and ($mediaType -eq "text/html" -or [string]::IsNullOrEmpty($mediaType))) {
    if ([string]::IsNullOrEmpty($rel)) { return (Join-Path $root "index.html") }
    return (Join-Path $root (Join-Path ($rel -replace "/", "\") "index.html"))
  }
  if ([string]::IsNullOrEmpty($rel)) { return $null }
  return (Join-Path $root ($rel -replace "/", "\"))
}

$pages = @(
  "/", "/litepaper", "/whitepaper", "/docs", "/trade", "/launch", "/launch/new",
  "/status", "/pricing", "/fees", "/wallet", "/opcode-whitepaper.pdf",
  "/icon.png", "/apple-icon.png", "/favicon.ico", "/opengraph-image", "/robots.txt",
  "/docs/quick-start", "/docs/wallet", "/docs/eligibility", "/docs/terminal",
  "/docs/swap", "/docs/receipts", "/docs/troubleshooting", "/docs/pricing",
  "/docs/sessions", "/docs/reference", "/docs/fees", "/docs/assets",
  "/docs/corporate-actions", "/docs/markets", "/docs/launch", "/docs/launch/rewards",
  "/docs/launch/terms", "/docs/settlement", "/docs/pools", "/docs/risk-controls",
  "/docs/verify", "/docs/api", "/docs/risks", "/docs/faq", "/docs/glossary",
  "/trade/pools"
)
foreach ($p in $pages) { Add-SitePath $p }

$marketFile = Join-Path $root "api\market-data.html"
if (Test-Path $marketFile) {
  $markets = Get-Content -Raw $marketFile | ConvertFrom-Json
  foreach ($m in $markets.markets) {
    if ($m.id) {
      Add-SitePath ("/trade/" + $m.id)
      Add-SitePath ("/images/assets/" + $m.id + ".png")
    }
  }
}
foreach ($icon in @("usdc", "sol", "nvda", "spy", "qqq", "gld", "tsla", "aapl", "msft", "meta", "coin", "crcl")) {
  Add-SitePath ("/images/assets/" + $icon + ".png")
}
Add-SitePath "/images/launch/sol.png"
Add-SitePath "/images/opcode-mark.png"
foreach ($id in @("nvda", "spy", "qqq", "gld", "tsla", "aapl", "msft", "meta", "coin", "crcl")) {
  Add-SitePath ("/trade/pools/" + $id)
}

$launchFile = Join-Path $root "api\launch.html"
if (Test-Path $launchFile) {
  $launch = Get-Content -Raw $launchFile | ConvertFrom-Json
  foreach ($item in $launch.launches) {
    if ($item.mint) { Add-SitePath ("/launch/" + $item.mint) }
    if ($item.image) { Add-SitePath $item.image }
  }
}

Write-Output ("queued " + $queue.Count)

while ($queue.Count -gt 0) {
  $batch = @()
  while ($batch.Count -lt 8 -and $queue.Count -gt 0) { $batch += $queue.Dequeue() }
  $pending = foreach ($path in $batch) {
    [pscustomobject]@{
      Path = $path
      Task = $client.GetAsync($origin + $path)
    }
  }
  try { [Threading.Tasks.Task]::WaitAll(@($pending.Task)) } catch {}

  foreach ($item in $pending) {
    $task = $item.Task
    if ($task.IsFaulted -or $task.IsCanceled) {
      $failed.Add($item.Path) | Out-Null
      continue
    }
    $resp = $task.Result
    try {
      $code = [int]$resp.StatusCode
      if ($code -ne 200) {
        if ($code -ne 404) { $failed.Add("$code $($item.Path)") | Out-Null }
        continue
      }
      $hostName = $resp.RequestMessage.RequestUri.Host
      if ($hostName -ne "opcode.so" -and $hostName -ne "www.opcode.so") { continue }
      $media = ""
      if ($resp.Content.Headers.ContentType) { $media = $resp.Content.Headers.ContentType.MediaType }
      $bytes = $resp.Content.ReadAsByteArrayAsync().Result
      if ($null -eq $bytes -or $bytes.Length -eq 0) { continue }
      $dest = Get-SavePath $item.Path $media
      if (-not $dest) { continue }
      $parent = Split-Path $dest
      if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
      [IO.File]::WriteAllBytes($dest, $bytes)
      $saved++
      $isText = $media -match '^(text/|application/javascript|application/json|image/svg)' -or $dest -match '\.(js|css|html|svg)$'
      if ($isText) {
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        foreach ($link in (Get-LinkedPaths $text)) { Add-SitePath $link }
      }
    } catch {
      $failed.Add($item.Path) | Out-Null
    } finally {
      $resp.Dispose()
    }
  }
  Write-Output ("saved=$saved queued=$($queue.Count) failed=$($failed.Count)")
}

Get-ChildItem -Path $root -Recurse -File | Where-Object { $_.Name -match ' \(\d+\)' } | Remove-Item -Force

Get-ChildItem -Path $root -Recurse -Filter *.html -File | Where-Object {
  $_.Name -ne "index.html" -and $_.FullName -notmatch '\\api\\'
} | ForEach-Object {
  $index = Join-Path (Join-Path $_.DirectoryName $_.BaseName) "index.html"
  if (Test-Path $index) { Remove-Item $_.FullName -Force }
}

$client.Dispose()
Write-Output ("DONE saved=$saved failed=$($failed.Count)")
if ($failed.Count -gt 0) { $failed | Select-Object -First 40 | ForEach-Object { Write-Output ("FAIL " + $_) } }
