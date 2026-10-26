using System.Runtime.InteropServices;

namespace PcMonitor.Collector.Native;

/// <summary>Which Windows services each running process hosts (essential to understand svchost.exe instances).</summary>
internal sealed class ServiceMap
{
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;
    private const int SC_ENUM_PROCESS_INFO = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode,
            dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    private readonly object _gate = new();
    private Dictionary<int, string> _byPid = new();
    private long _builtAt;

    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    /// <summary>Comma separated display names of services hosted by <paramref name="pid"/>, or null.</summary>
    public string? Describe(int pid)
    {
        Refresh();
        lock (_gate) return _byPid.TryGetValue(pid, out var s) ? s : null;
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_builtAt != 0 && Environment.TickCount64 - _builtAt < MaxAge.TotalMilliseconds) return;
            _byPid = Build();
            _builtAt = Environment.TickCount64;
        }
    }

    private static Dictionary<int, string> Build()
    {
        var map = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return new();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint size = 128 * 1024, resume = 0;
            buffer = Marshal.AllocHGlobal((int)size);
            int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            while (true)
            {
                bool ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                    buffer, size, out uint needed, out uint count, ref resume, null);
                int err = Marshal.GetLastWin32Error();
                if (!ok && err != 234 /* ERROR_MORE_DATA */) break;

                for (uint i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + (int)i * stride);
                    if (e.dwProcessId == 0) continue;
                    string name = Marshal.PtrToStringUni(e.lpDisplayName) ?? Marshal.PtrToStringUni(e.lpServiceName) ?? "";
                    if (!map.TryGetValue((int)e.dwProcessId, out var l)) map[(int)e.dwProcessId] = l = new();
                    l.Add(name);
                }
                if (ok) break;
                if (needed > size) { Marshal.FreeHGlobal(buffer); size = needed + 4096; buffer = Marshal.AllocHGlobal((int)size); resume = 0; map.Clear(); }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
        return map.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
    }
}
