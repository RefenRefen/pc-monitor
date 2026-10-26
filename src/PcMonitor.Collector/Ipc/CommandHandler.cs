using System.ComponentModel;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcMonitor.Collector.Native;
using PcMonitor.Core;

namespace PcMonitor.Collector.Ipc;

/// <summary>
/// Executes the few state-changing actions the widget offers. The service runs as LocalSystem, so every action is
/// carried out while impersonating the pipe client: Windows then enforces the user's own rights, which means the
/// widget can never be used to end SYSTEM processes or to edit machine-wide settings without admin rights.
/// </summary>
internal sealed partial class CommandHandler
{
    private readonly CollectorContext _ctx;

    public CommandHandler(CollectorContext ctx) => _ctx = ctx;

    [GeneratedRegex(@"^(HKLM|HKU)\\(?:S-1-5-21-[\d-]+\\)?SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\(Run|Run32|StartupFolder)$")]
    private static partial Regex ApprovedKeyPattern();

    public CommandResponse Execute(CommandRequest req, NamedPipeServerStream pipe)
    {
        try
        {
            return req.Command switch
            {
                CommandKind.EndProcess => EndProcesses(new[] { req.Pid }, pipe),
                CommandKind.EndApp => EndApp(req.AppId, pipe),
                CommandKind.DisableStartup => SetStartup(req, enabled: false, pipe),
                CommandKind.EnableStartup => SetStartup(req, enabled: true, pipe),
                _ => Fail("Unknown command"),
            };
        }
        catch (Exception ex)
        {
            _ctx.Log.LogDebugSafe(ex, "Command {Command} failed", req.Command);
            return Fail(ex.Message);
        }
    }

    private static CommandResponse Fail(string message) => new() { Success = false, Message = message };
    private static CommandResponse Ok(string message) => new() { Success = true, Message = message };

    // ------------------------------------------------------------------ end process / app

    private CommandResponse EndApp(long appId, NamedPipeServerStream pipe)
    {
        List<int> pids;
        lock (_ctx.Table.SyncRoot)
            pids = _ctx.Table.Entries.Where(p => p.AppId == appId).Select(p => p.Pid).ToList();
        return pids.Count == 0 ? Fail("The application is no longer running.") : EndProcesses(pids, pipe);
    }

    private CommandResponse EndProcesses(IReadOnlyList<int> pids, NamedPipeServerStream pipe)
    {
        var targets = new List<int>();
        lock (_ctx.Table.SyncRoot)
            foreach (int pid in pids)
                if (pid > 4 && pid != Environment.ProcessId && _ctx.Table.TryGet(pid, out _)) targets.Add(pid);
        if (targets.Count == 0) return Fail("Nothing to end (process is gone or protected).");

        int ended = 0;
        string? lastError = null;
        pipe.RunAsClient(() =>
        {
            foreach (int pid in targets)
            {
                IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
                if (h == IntPtr.Zero) { lastError = new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message; continue; }
                try
                {
                    if (NativeMethods.TerminateProcess(h, 1)) ended++;
                    else lastError = new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
                }
                finally { NativeMethods.CloseHandle(h); }
            }
        });

        if (ended == targets.Count) return Ok(ended == 1 ? "Process ended." : $"Ended {ended} processes.");
        if (ended > 0) return Ok($"Ended {ended} of {targets.Count} processes ({lastError}).");
        return Fail($"Could not end the process: {lastError}");
    }

    // ------------------------------------------------------------------ startup entries

    private CommandResponse SetStartup(CommandRequest req, bool enabled, NamedPipeServerStream pipe)
    {
        if (req.Startup == null) return Fail("No startup entry specified.");
        var identity = _ctx.Identity.GetIdentity(req.AppId);
        if (identity == null) return Fail("Unknown application.");

        // Never trust the client's copy: look the entry up again from our own index.
        _ctx.Identity.RefreshStartup(identity);
        var entry = identity.Startup.FirstOrDefault(e =>
            e.Name == req.Startup.Name && e.Location == req.Startup.Location && e.Kind == req.Startup.Kind);
        if (entry == null) return Fail("Startup entry not found (it may have been removed).");
        if (!entry.CanDisable || entry.ApprovedKey == null)
            return Fail("This kind of startup entry cannot be switched here. Use Task Scheduler or Services instead.");

        var m = ApprovedKeyPattern().Match(entry.ApprovedKey);
        if (!m.Success) return Fail("Refusing to modify an unexpected registry location.");

        string hive = m.Groups[1].Value;
        string subKey = entry.ApprovedKey.Substring(hive.Length + 1);
        byte[] value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);

        string? error = null;
        pipe.RunAsClient(() =>
        {
            try
            {
                using var key = (hive == "HKLM" ? Registry.LocalMachine : Registry.Users).CreateSubKey(subKey, writable: true);
                key.SetValue(entry.Name, value, RegistryValueKind.Binary);
            }
            catch (UnauthorizedAccessException) { error = "Access denied. Changing machine-wide startup entries needs administrator rights."; }
            catch (System.Security.SecurityException) { error = "Access denied. Changing machine-wide startup entries needs administrator rights."; }
            catch (Exception ex) { error = ex.Message; }
        });
        if (error != null) return Fail(error);

        _ctx.Identity.InvalidateStartupIndex();
        return Ok(enabled ? "Startup entry enabled." : "Startup entry disabled. It will not launch at the next sign-in.");
    }
}

internal static class LoggerExtensions
{
    public static void LogDebugSafe(this Microsoft.Extensions.Logging.ILogger log, Exception ex, string message, params object?[] args)
    {
        try { log.LogDebug(ex, message, args); } catch { /* logging must never fail an action */ }
    }
}
