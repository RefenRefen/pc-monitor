using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace PcMonitor.Collector.Sampling;

/// <summary>Tracks which addresses belong to this machine so traffic direction/remote peer can be derived.</summary>
internal sealed class LocalAddressSet
{
    private volatile HashSet<IPAddress> _addresses = new();
    private long _refreshedAt;

    public void Refresh()
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    set.Add(ua.Address);
        }
        catch (NetworkInformationException) { /* keep previous */ return; }
        _addresses = set;
        _refreshedAt = Environment.TickCount64;
    }

    public void RefreshIfStale()
    {
        if (Environment.TickCount64 - _refreshedAt > 30_000) Refresh();
    }

    public bool Contains(IPAddress a) => IPAddress.IsLoopback(a) || _addresses.Contains(a);

    /// <summary>
    /// Pick the remote peer from the two endpoints of a TCP/UDP event.
    /// Returns false for loopback/local-to-local traffic, which is excluded from network usage.
    /// </summary>
    public bool TryGetRemote(IPAddress daddr, int dport, IPAddress saddr, int sport,
        out IPAddress remote, out int remotePort)
    {
        bool dLocal = Contains(daddr), sLocal = Contains(saddr);
        if (dLocal && sLocal) { remote = daddr; remotePort = dport; return false; }
        if (sLocal || !dLocal) { remote = daddr; remotePort = dport; return true; }
        // Only the destination is local: the event reports the packet's view (typical for receives).
        remote = saddr; remotePort = sport;
        return true;
    }
}

/// <summary>
/// A private "system logger" ETW session (Windows 10+; not the legacy NT Kernel Logger, so it does not
/// collide with other tools) delivering per-process TCP/UDP bytes (with peers) and physical disk IO bytes.
/// </summary>
internal sealed class EtwMonitor : IDisposable
{
    public const string SessionName = "PcMonitorKernel";

    private readonly EtwCounters _counters;
    private readonly ILogger _log;
    private readonly LocalAddressSet _local = new();
    private readonly ConcurrentDictionary<int, (int Pid, long Tick)> _threadToPid = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _lastThreadCachePrune;

    public bool IsRunning { get; private set; }
    public string? FailureReason { get; private set; }

    public EtwMonitor(EtwCounters counters, ILogger log)
    {
        _counters = counters;
        _log = log;
    }

    public bool TryStart()
    {
        try
        {
            _local.Refresh();
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            // Small buffers keep the non-paged pool footprint low; events are drained in real time.
            _session.BufferSizeMB = 8;
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.DiskIO);

            var k = _session.Source.Kernel;
            k.TcpIpSend += d => OnNet(PayloadPid(d), true, d.size, d.daddr, d.dport, d.saddr, d.sport, false);
            k.TcpIpRecv += d => OnNet(PayloadPid(d), false, d.size, d.daddr, d.dport, d.saddr, d.sport, false);
            k.TcpIpSendIPV6 += d => OnNet(PayloadPid(d), true, d.size, d.daddr, d.dport, d.saddr, d.sport, false);
            k.TcpIpRecvIPV6 += d => OnNet(PayloadPid(d), false, d.size, d.daddr, d.dport, d.saddr, d.sport, false);
            k.UdpIpSend += d => OnNet(PayloadPid(d), true, d.size, d.daddr, d.dport, d.saddr, d.sport, true);
            k.UdpIpRecv += d => OnNet(PayloadPid(d), false, d.size, d.daddr, d.dport, d.saddr, d.sport, true);
            k.UdpIpSendIPV6 += d => OnNet(PayloadPid(d), true, d.size, d.daddr, d.dport, d.saddr, d.sport, true);
            k.UdpIpRecvIPV6 += d => OnNet(PayloadPid(d), false, d.size, d.daddr, d.dport, d.saddr, d.sport, true);
            k.DiskIORead += d => OnDisk(d, false);
            k.DiskIOWrite += d => OnDisk(d, true);

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { _log.LogWarning(ex, "ETW processing thread ended"); }
                IsRunning = false;
            })
            { IsBackground = true, Name = "etw-kernel", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            _log.LogWarning(ex, "ETW kernel session unavailable (needs administrator/LocalSystem); network and per-app disk will be limited");
            Dispose();
            return false;
        }
    }

    public void RefreshLocalAddresses() => _local.RefreshIfStale();

    private void OnNet(int pid, bool send, int size, IPAddress daddr, int dport, IPAddress saddr, int sport, bool udp)
    {
        if (!_local.TryGetRemote(daddr, dport, saddr, sport, out var remote, out int port)) return;
        _counters.AddNet(pid, send, size, IpKey.From(remote), port, udp);
    }

    private void OnDisk(TraceEvent d, bool write)
    {
        int size = d is Microsoft.Diagnostics.Tracing.Parsers.Kernel.DiskIOTraceData io ? io.TransferSize : 0;
        if (size <= 0) return;

        // The event is logged at completion in an arbitrary context; the payload names the issuing thread.
        int tid = ReadIssuingThread(d);
        int pid = tid > 0 ? ResolveThreadOwner(tid) : 0;
        if (pid <= 0) { _counters.AddDiskUnattributed(write, size); return; }
        _counters.AddDisk(pid, write, size);
    }

    /// <summary>TcpIp/UdpIp payloads all start with the owning PID; the header PID is unreliable on receives.</summary>
    private static int PayloadPid(TraceEvent d) => d.EventDataLength >= 8 ? Marshal.ReadInt32(d.DataStart) : d.ProcessID;

    private static int ReadIssuingThread(TraceEvent d)
    {
        // DiskIo_TypeGroup1: ... Irp @32, HighResResponseTime @40, IssuingThreadId @48.
        if (d.EventDataLength < 52) return 0;
        return Marshal.ReadInt32(d.DataStart, 48);
    }

    private int ResolveThreadOwner(int tid)
    {
        long now = Environment.TickCount64;
        if (_threadToPid.TryGetValue(tid, out var hit) && now - hit.Tick < 60_000) return hit.Pid;

        int pid = Native.ThreadInterop.GetOwnerPid(tid);
        _threadToPid[tid] = (pid, now);
        if (now - _lastThreadCachePrune > 120_000)
        {
            _lastThreadCachePrune = now;
            foreach (var kv in _threadToPid)
                if (now - kv.Value.Tick > 60_000) _threadToPid.TryRemove(kv.Key, out _);
        }
        return pid;
    }

    public void Dispose()
    {
        IsRunning = false;
        try { _session?.Dispose(); } catch { /* best effort */ }
        _session = null;
        if (_thread is { IsAlive: true }) _thread.Join(2000);
    }
}
