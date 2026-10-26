<#
.SYNOPSIS
  Publishes the collector and the widget (self-contained, win-x64) into artifacts\publish and, if the WiX CLI
  is available, builds artifacts\PcMonitor.msi.

.EXAMPLE
  .\installer\build.ps1            # publish + MSI (when WiX is restorable)
  .\installer\build.ps1 -NoMsi     # publish only (what install.ps1 consumes)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoMsi
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host '== Publishing collector' -ForegroundColor Cyan
dotnet publish "$root\src\PcMonitor.Collector\PcMonitor.Collector.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o "$out\collector" --nologo
if ($LASTEXITCODE -ne 0) { throw 'collector publish failed' }

Write-Host '== Publishing widget' -ForegroundColor Cyan
dotnet publish "$root\src\PcMonitor.Widget\PcMonitor.Widget.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o "$out\widget" --nologo
if ($LASTEXITCODE -ne 0) { throw 'widget publish failed' }

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published to {0} ({1:N0} MB)" -f $out, $size) -ForegroundColor Green

if ($NoMsi) { return }

Write-Host '== Building MSI (WiX)' -ForegroundColor Cyan
Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'dotnet tool restore failed - skipping the MSI.'; return }
    dotnet build "$root\installer\PcMonitor.Installer.wixproj" -c $Configuration --nologo -p:PublishDir=$out
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
    Write-Host "MSI: $root\artifacts\PcMonitor.msi" -ForegroundColor Green
}
finally { Pop-Location }
