# Registers Gergur with Windows as a browser, so it can be chosen as the default for
# http/https links - including the sign-in pages desktop apps open in "the browser".
#
# Windows does not let an app make itself the default. This writes the registration
# Windows needs to list Gergur as a choice, then opens Settings so you can pick it.
# Everything lands under HKCU, so no admin rights, and nothing changes for other users.
#
#   .\scripts\register-browser.ps1 -Install    copy the build somewhere stable, register that
#   .\scripts\register-browser.ps1             register the build where it already is
#   .\scripts\register-browser.ps1 -Remove     undo
[CmdletBinding()]
param(
  [string]$ExePath,
  [switch]$Install,
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$appName   = 'Gergur'
$progId    = 'GergurHTML'
$clientKey = "HKCU:\Software\Clients\StartMenuInternet\$appName"
$capKey    = "$clientKey\Capabilities"
$classKey  = "HKCU:\Software\Classes\$progId"
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Gergur'
$buildDir   = Join-Path $PSScriptRoot '..\src\Gergur\bin\Release\net10.0-windows'

if ($Remove) {
  Remove-Item $clientKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $classKey  -Recurse -Force -ErrorAction SilentlyContinue
  Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name $appName -ErrorAction SilentlyContinue
  Write-Host "Gergur unregistered. Choose another default browser in Settings."
  Start-Process 'ms-settings:defaultapps'
  return
}

# A registered path has to keep working. Pointing Windows at a bin\Release folder under
# Downloads means the default browser breaks the moment that folder moves, so -Install
# copies the build to a stable per-user location and registers that instead.
if ($Install) {
  if (Get-Process Gergur -ErrorAction SilentlyContinue) {
    throw "Close Gergur first: its running exe locks the files being copied."
  }
  if (-not (Test-Path (Join-Path $buildDir 'Gergur.exe'))) {
    throw "No Release build found. Run: dotnet publish src\Gergur -c Release"
  }
  New-Item -ItemType Directory -Force $installDir | Out-Null
  Copy-Item (Join-Path $buildDir '*') $installDir -Recurse -Force
  $ExePath = Join-Path $installDir 'Gergur.exe'
  Write-Host "Installed to $installDir"
}

if (-not $ExePath) {
  $candidates = @((Join-Path $installDir 'Gergur.exe'), (Join-Path $buildDir 'Gergur.exe'))
  $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
  throw "Gergur.exe not found. Build it, or pass -ExePath."
}
$ExePath = (Resolve-Path $ExePath).Path
if ($ExePath -like "*\Downloads\*") {
  Write-Warning "Registering a path under Downloads. If that folder ever moves, links stop opening. Consider -Install."
}

function Set-Default($path, $value) {
  New-Item -Path $path -Force | Out-Null
  Set-ItemProperty -Path $path -Name '(default)' -Value $value
}

# The StartMenuInternet client entry is what puts Gergur in the browser list.
Set-Default $clientKey $appName
Set-Default "$clientKey\DefaultIcon" "$ExePath,0"
Set-Default "$clientKey\shell\open\command" "`"$ExePath`""

New-Item -Path $capKey -Force | Out-Null
Set-ItemProperty $capKey -Name 'ApplicationName'        -Value 'Gergur'
Set-ItemProperty $capKey -Name 'ApplicationDescription' -Value 'A personal, memory-frugal browser'
Set-ItemProperty $capKey -Name 'ApplicationIcon'        -Value "$ExePath,0"

# http and https are what a "sign in with..." page arrives as. http matters as much as
# https: OAuth sends you back to a loopback address like http://127.0.0.1:5000/callback.
New-Item -Path "$capKey\URLAssociations" -Force | Out-Null
Set-ItemProperty "$capKey\URLAssociations" -Name 'http'  -Value $progId
Set-ItemProperty "$capKey\URLAssociations" -Name 'https' -Value $progId

New-Item -Path "$capKey\FileAssociations" -Force | Out-Null
Set-ItemProperty "$capKey\FileAssociations" -Name '.htm'  -Value $progId
Set-ItemProperty "$capKey\FileAssociations" -Name '.html' -Value $progId

New-Item -Path "$capKey\StartMenu" -Force | Out-Null
Set-ItemProperty "$capKey\StartMenu" -Name 'StartMenuInternet' -Value $appName

# The ProgID: what actually runs when Windows opens a link with Gergur. "%1" is the url.
Set-Default $classKey 'Gergur HTML Document'
Set-Default "$classKey\DefaultIcon" "$ExePath,0"
Set-Default "$classKey\shell\open\command" "`"$ExePath`" `"%1`""

New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $appName `
  -Value "Software\Clients\StartMenuInternet\$appName\Capabilities"

Write-Host ""
Write-Host "Registered Gergur at $ExePath"
Write-Host "Settings is opening: choose Web browser, then pick Gergur."
Write-Host "Windows only lets you make that choice yourself, so this is the last step."
Start-Process 'ms-settings:defaultapps'
