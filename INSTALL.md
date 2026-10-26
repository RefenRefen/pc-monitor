# Installing PC Monitor

PC Monitor has two parts:

- **Collector** – a Windows service that runs as LocalSystem. It needs that level of access to read exact per-app network and disk usage (kernel ETW). It is the only part that runs elevated.
- **Widget** – the glass pill and the dashboard. It runs as your normal user and talks to the collector through a protected named pipe.

Requirements: Windows 11 (x64). Nothing else needs to be installed; both parts bring their own .NET runtime.

## 1. Build

Open PowerShell in the repository folder (normal, not elevated):

```powershell
.\installer\build.ps1
```

This publishes both parts to `artifacts\publish` and builds `artifacts\PcMonitor.msi` (takes about 3 minutes the first time).
Use `.\installer\build.ps1 -NoMsi` to publish only, which is all the script install needs.

If PowerShell says running scripts is disabled, run any script in this guide like this instead:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build.ps1
```

## 2. Install (choose one)

### Option A – MSI

Double-click `artifacts\PcMonitor.msi` and accept the UAC prompt.

It will:

- copy the files to `C:\Program Files\PcMonitor` (`collector\` and `widget\`),
- register the `PcMonitorCollector` service (LocalSystem, automatic delayed start, restarts itself after a crash) and start it,
- make the widget start at sign-in for every user (`HKLM\...\Run`).

### Option B – script

Open PowerShell **as Administrator**:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1
```

Same result as the MSI. Add `-PerUserWidgetStartup` to start the widget only for your account (`HKCU\...\Run`).
Running it again updates an existing installation.

## 3. Start the widget

It starts automatically at your next sign-in. To start it now, run it as your normal user (not elevated):

```powershell
& "C:\Program Files\PcMonitor\widget\PcMonitor.Widget.exe"
```

The pill appears near the bottom-right corner and a tray icon is added. Open the dashboard from the button on the pill, by double-clicking it, or from the tray menu.

## 4. Check that it works

- The status dot on the pill is green and the NET row shows numbers instead of "n/a". If it says the collector is not elevated, the service is not running as LocalSystem or ETW could not start.
- Service state:

  ```powershell
  Get-Service PcMonitorCollector
  ```

- Errors are written to the Windows Application event log (source `PcMonitorCollector`). Widget errors go to `%LocalAppData%\PcMonitor\widget-errors.log`.
- The dashboard needs a few minutes of recording before there is history to show.

## Where things are stored

| What | Where |
|---|---|
| Programs | `C:\Program Files\PcMonitor` |
| Usage history (SQLite) | `C:\ProgramData\PcMonitor\usage.db` – readable only by SYSTEM and Administrators |
| Widget settings, icon cache | `%LocalAppData%\PcMonitor` |

History is kept as 1-minute rows for 7 days, then as 15-minute rows for 90 days.

## Update

Run the build again, then install again (MSI upgrades in place; the script replaces the files and restarts the service).

## Uninstall

- MSI: Settings, Apps, PC Monitor.
- Script (elevated PowerShell):

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\uninstall.ps1
  ```

  Add `-RemoveData` to also delete the usage database and the widget's settings.

## Trying it without installing

Useful for development. Run the collector in a console, then the widget:

```powershell
.\artifacts\publish\collector\PcMonitor.Collector.exe --data "$env:TEMP\pcmon-dev"
.\artifacts\publish\widget\PcMonitor.Widget.exe
```

Without an elevated console the collector works, but network and per-app disk detail are unavailable. Start the console as Administrator to get them.

Widget command-line options: `--hidden` (start in the tray only) and `--dashboard` (open the dashboard at start).
