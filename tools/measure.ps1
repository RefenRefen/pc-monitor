<#
.SYNOPSIS
  Measures steady-state CPU and memory of a running process.
.EXAMPLE
  .\tools\measure.ps1 -Name PcMonitor.Collector -Seconds 60
#>
param(
    [Parameter(Mandatory)] [string]$Name,
    [int]$Seconds = 60,
    [int]$WarmupSeconds = 0
)

$p = Get-Process -Name $Name -ErrorAction Stop | Select-Object -First 1
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$p.Refresh()
$cores = [Environment]::ProcessorCount
$cpu0 = $p.TotalProcessorTime
$t0 = Get-Date
$peakWs = 0
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $p.Refresh()
    if ($p.WorkingSet64 -gt $peakWs) { $peakWs = $p.WorkingSet64 }
}
$cpu1 = $p.TotalProcessorTime
$elapsed = ((Get-Date) - $t0).TotalSeconds
$cpuSec = ($cpu1 - $cpu0).TotalSeconds
[pscustomobject]@{
    Process       = $Name
    Seconds       = [math]::Round($elapsed, 1)
    CpuPctOfOneCore = [math]::Round($cpuSec / $elapsed * 100, 2)
    CpuPctOfMachine = [math]::Round($cpuSec / $elapsed / $cores * 100, 3)
    WorkingSetMB  = [math]::Round($p.WorkingSet64 / 1MB, 1)
    PrivateMB     = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    PeakWsMB      = [math]::Round($peakWs / 1MB, 1)
    Threads       = $p.Threads.Count
    Handles       = $p.HandleCount
}
