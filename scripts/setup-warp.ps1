# Sets up the browser-only Cloudflare WARP tunnel for Gergur.
# Downloads two open-source helpers, registers a free WARP account, and
# verifies the tunnel end to end:
#   wgcf       https://github.com/ViRb3/wgcf         (WARP account + WireGuard profile)
#   wireproxy  https://github.com/pufferffish/wireproxy (WireGuard -> local SOCKS5 proxy)
$ErrorActionPreference = 'Stop'
$vpnDir = "$env:LOCALAPPDATA\Gergur\vpn"
$port = 24001
New-Item -ItemType Directory -Force $vpnDir | Out-Null

if (-not (Test-Path "$vpnDir\wgcf.exe")) {
  Write-Host "downloading wgcf..."
  $asset = (Invoke-RestMethod "https://api.github.com/repos/ViRb3/wgcf/releases/latest").assets |
    Where-Object name -match 'windows_amd64\.exe$' | Select-Object -First 1
  Invoke-WebRequest $asset.browser_download_url -OutFile "$vpnDir\wgcf.exe"
}

if (-not (Test-Path "$vpnDir\wireproxy.exe")) {
  Write-Host "downloading wireproxy..."
  $asset = (Invoke-RestMethod "https://api.github.com/repos/pufferffish/wireproxy/releases/latest").assets |
    Where-Object name -match 'windows_amd64' | Select-Object -First 1
  $pkg = "$vpnDir\$($asset.name)"
  Invoke-WebRequest $asset.browser_download_url -OutFile $pkg
  Push-Location $vpnDir
  # Windows-native tar with relative paths; Git Bash's GNU tar treats C: as a hostname
  if ($pkg -match '\.tar\.gz$') { & "$env:SystemRoot\System32\tar.exe" -xzf $asset.name }
  elseif ($pkg -match '\.zip$') { Expand-Archive $asset.name . -Force }
  Pop-Location
  Remove-Item $pkg -ErrorAction SilentlyContinue
  if (-not (Test-Path "$vpnDir\wireproxy.exe")) { throw "wireproxy.exe did not extract; asset was $($asset.name)" }
}

Push-Location $vpnDir
if (-not (Test-Path "$vpnDir\wgcf-account.toml")) {
  Write-Host "registering free WARP account..."
  & "$vpnDir\wgcf.exe" register --accept-tos
}
& "$vpnDir\wgcf.exe" generate
Pop-Location

@"
WGConfig = $vpnDir\wgcf-profile.conf

[Socks5]
BindAddress = 127.0.0.1:$port
"@ | Set-Content -Encoding ascii "$vpnDir\wireproxy.conf"

Write-Host "testing the tunnel..."
$proc = Start-Process "$vpnDir\wireproxy.exe" -ArgumentList "-c", "$vpnDir\wireproxy.conf" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 4
$trace = & curl.exe -s --max-time 20 --socks5-hostname "127.0.0.1:$port" https://www.cloudflare.com/cdn-cgi/trace
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
$warp = ($trace -split "`n" | Where-Object { $_ -match '^(warp|ip|loc)=' }) -join '  '
Write-Host "tunnel check: $warp"
if ($trace -match 'warp=on') { Write-Host "SUCCESS: WARP tunnel works. Toggle VPN from the Gergur menu." }
else { Write-Host "tunnel test did not confirm warp=on; tell Claude the output above." }
