<#
.SYNOPSIS
  Removes what install.ps1 installed: service, auto-start entries and program files.
.PARAMETER RemoveData
  Also delete the usage database in %ProgramData%\PcMonitor and the per-user settings/icon cache.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$serviceName = 'PcMonitorCollector'
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}
Get-Process -Name 'PcMonitor.Widget' -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($key in 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run') {
    Remove-ItemProperty -Path $key -Name 'PcMonitor' -ErrorAction SilentlyContinue
}

Start-Sleep -Seconds 1
Remove-Item (Join-Path $env:ProgramFiles 'PcMonitor') -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData 'PcMonitor') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA 'PcMonitor') -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PC Monitor removed.' -ForegroundColor Green
