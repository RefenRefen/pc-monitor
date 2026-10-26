<#
.SYNOPSIS
  Developer install: copies the published binaries to Program Files, registers the collector as an auto-start
  Windows service (LocalSystem, delayed start, restarts on failure) and makes the widget start at sign-in.

.DESCRIPTION
  Run from an elevated PowerShell after `.\installer\build.ps1 -NoMsi`.
  The collector runs elevated on purpose: that is what lets it open the kernel ETW session which gives exact
  per-application network and disk byte counts. The widget itself never runs elevated.

.PARAMETER PublishDir
  Folder that contains the `collector` and `widget` sub-folders (default: <repo>\artifacts\publish).

.PARAMETER PerUserWidgetStartup
  Register the widget under HKCU\...\Run (current user only) instead of HKLM\...\Run (every user).
#>
[CmdletBinding()]
param(
    [string]$PublishDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish'),
    [switch]$PerUserWidgetStartup
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$serviceName = 'PcMonitorCollector'
$installRoot = Join-Path $env:ProgramFiles 'PcMonitor'
$collectorDir = Join-Path $installRoot 'collector'
$widgetDir = Join-Path $installRoot 'widget'

foreach ($sub in 'collector', 'widget') {
    if (-not (Test-Path (Join-Path $PublishDir $sub))) { throw "Missing $PublishDir\$sub - run installer\build.ps1 -NoMsi first." }
}

# 1) stop whatever is running so files can be replaced
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Host 'Stopping service...'
    Stop-Service $serviceName -Force
    $svc.WaitForStatus('Stopped', '00:00:30')
}
Get-Process -Name 'PcMonitor.Widget' -ErrorAction SilentlyContinue | Stop-Process -Force

# 2) copy files
Write-Host "Copying files to $installRoot"
New-Item -ItemType Directory -Force $installRoot | Out-Null
robocopy (Join-Path $PublishDir 'collector') $collectorDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copying the collector failed' }
robocopy (Join-Path $PublishDir 'widget') $widgetDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copying the widget failed' }
$global:LASTEXITCODE = 0

# 3) service
$binPath = '"' + (Join-Path $collectorDir 'PcMonitor.Collector.exe') + '"'
if (-not $svc) {
    Write-Host 'Creating service...'
    sc.exe create $serviceName binPath= $binPath start= delayed-auto obj= LocalSystem DisplayName= 'PC Monitor Collector' | Out-Null
} else {
    sc.exe config $serviceName binPath= $binPath start= delayed-auto obj= LocalSystem | Out-Null
}
sc.exe description $serviceName 'Samples per-application CPU, memory, GPU, network and disk usage for the PC Monitor widget.' | Out-Null
# restart after 5 s, 30 s, 60 s; reset the failure counter after a day
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

# 4) widget auto-start
$runKey = if ($PerUserWidgetStartup) { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' } else { 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' }
Set-ItemProperty -Path $runKey -Name 'PcMonitor' -Value ('"' + (Join-Path $widgetDir 'PcMonitor.Widget.exe') + '"')

# 5) go
Start-Service $serviceName
Write-Host ''
Write-Host 'Installed. Service state:' (Get-Service $serviceName).Status -ForegroundColor Green
Write-Host "The widget starts at your next sign-in. Start it now (as your normal user, not elevated): `"$widgetDir\PcMonitor.Widget.exe`""
