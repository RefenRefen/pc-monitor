<#
.SYNOPSIS
  Captures a PC Monitor window to a PNG (used for the README screenshots).
  Assumes the collector and the widget are already running.

.EXAMPLE
  .\tools\capture.ps1 -Window dashboard -Out docs\images\dashboard.png -SelectApp "Google Chrome"
  .\tools\capture.ps1 -Window compact   -Out docs\images\widget.png
#>
param(
    [Parameter(Mandatory)] [ValidateSet('dashboard', 'compact')] [string]$Window,
    [Parameter(Mandatory)] [string]$Out,
    [string]$SelectApp,
    [string]$Search,
    [switch]$NeedsAttention,
    [int]$Padding = 0
)

Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class DpiAware { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[DpiAware]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public struct RECT { public int L, T, R, B; } public class Z { [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags); }'
$UIA = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

$widget = Get-Process PcMonitor.Widget -ErrorAction Stop | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $widget.Id)
$windows = $UIA::RootElement.FindAll($Tree::Children, $cond)
$target = $windows | Sort-Object { $_.Current.BoundingRectangle.Width } -Descending | Select-Object -First 1
if ($Window -eq 'compact') { $target = $windows | Sort-Object { $_.Current.BoundingRectangle.Width } | Select-Object -First 1 }
if (-not $target) { throw 'Widget window not found.' }

# Bring the window above everything else (HWND_TOPMOST, SWP_NOMOVE|NOSIZE|SHOWWINDOW) so nothing overlaps the capture.
[Z]::SetWindowPos([IntPtr]$target.Current.NativeWindowHandle, [IntPtr]::new(-1), 0, 0, 0, 0, 0x43) | Out-Null
Start-Sleep -Milliseconds 500

function Find-ByName($root, $name, $type) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $type)))
    $root.FindFirst($Tree::Descendants, $c)
}

if ($Window -eq 'dashboard') {
    $box = $target.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    if ($Search -and $box) { $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Search); Start-Sleep 1 }
    if ($NeedsAttention) {
        $t = $target.FindFirst($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'Needs attention')))
        while ($t -and -not $t.GetSupportedPatterns().Count) { $t = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($t) }
        if ($t) { $t.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 2; if ($box) { $box.SetFocus() } }
    }
    if ($SelectApp) {
        $item = Find-ByName $target $SelectApp ([System.Windows.Automation.ControlType]::ListItem)
        if (-not $item) { throw "App '$SelectApp' not found in the list." }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep 5
    }
}

Start-Sleep 1
$r = $target.Current.BoundingRectangle
$x = [int]$r.X - $Padding; $y = [int]$r.Y - $Padding; $w = [int]$r.Width + 2 * $Padding; $h = [int]$r.Height + 2 * $Padding
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
New-Item -ItemType Directory -Force (Split-Path -Parent (Join-Path (Get-Location) $Out)) | Out-Null
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
"Saved $Out ($w x $h)"




