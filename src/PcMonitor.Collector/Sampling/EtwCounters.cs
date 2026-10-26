using System.Collections.Concurrent;
using System.Net;

namespace PcMonitor.Collector.Sampling;

/// <summary>16-byte IP address (IPv4 stored v4-mapped) usable as a dictionary key without allocation.</summary>
internal readonly record struct IpKey(ulong Hi, ulong Lo)
{
    public static IpKey From(IPAddress address)
    {
        Span<byte> b = stackalloc byte[16];
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(b.Slice(12), out _);
            b[10] = 0xFF; b[11] = 0xFF;
        }
        else
        {
            address.TryWriteBytes(b, out _);
        }
        return new IpKey(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.Slice(8)));
    }

    public IPAddress ToAddress()
    {
        Span<byte> b = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, Hi);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b.Slice(8), Lo);
        var addr = new IPAddress(b);
        return addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
    }

    public override string ToString() => ToAddress().ToString();
}

internal readonly record struct ConnKey(int Pid, IpKey Remote, int Port, bool Udp);

internal readonly record struct PidDelta(int Pid, long Rx, long Tx, long DiskRead, long DiskWrite);

internal readonly record struct ConnDelta(ConnKey Key, long Bytes);

/// <summary>
/// Lock-free accumulation of ETW events. The ETW thread only does <see cref="Interlocked"/> adds;
/// the tick thread drains and resets once per second.
/// </summary>
internal sealed class EtwCounters
{
    private sealed class PidCounters
    {
        public long Rx, Tx, DiskRead, DiskWrite;
        public int IdleDrains;
    }

    private sealed class ConnCounter { public long Bytes; }

    public const int MaxTrackedConnections = 50_000;
    private const int IdleDrainsBeforePrune = 120;

    private readonly ConcurrentDictionary<int, PidCounters> _pids = new();
    private readonly ConcurrentDictionary<ConnKey, ConnCounter> _conns = new();
    private long _sysRx, _sysTx, _sysDiskRead, _sysDiskWrite;

    public void AddNet(int pid, bool send, long bytes, in IpKey remote, int port, bool udp)
    {
        if (bytes <= 0) return;
        var c = _pids.GetOrAdd(pid, static _ => new PidCounters());
        if (send) { Interlocked.Add(ref c.Tx, bytes); Interlocked.Add(ref _sysTx, bytes); }
        else { Interlocked.Add(ref c.Rx, bytes); Interlocked.Add(ref _sysRx, bytes); }

        var key = new ConnKey(pid, remote, port, udp);
        if (_conns.TryGetValue(key, out var cc)) Interlocked.Add(ref cc.Bytes, bytes);
        else if (_conns.Count < MaxTrackedConnections) Interlocked.Add(ref _conns.GetOrAdd(key, static _ => new ConnCounter()).Bytes, bytes);
    }

    public void AddDisk(int pid, bool write, long bytes)
    {
        if (bytes <= 0) return;
        if (pid <= 0) { AddDiskUnattributed(write, bytes); return; }
        var c = _pids.GetOrAdd(pid, static _ => new PidCounters());
        if (write) { Interlocked.Add(ref c.DiskWrite, bytes); Interlocked.Add(ref _sysDiskWrite, bytes); }
        else { Interlocked.Add(ref c.DiskRead, bytes); Interlocked.Add(ref _sysDiskRead, bytes); }
    }

    public void AddDiskUnattributed(bool write, long bytes)
    {
        if (write) Interlocked.Add(ref _sysDiskWrite, bytes);
        else Interlocked.Add(ref _sysDiskRead, bytes);
    }

    /// <summary>Moves everything accumulated since the previous drain into the output lists.</summary>
    public void Drain(List<PidDelta> pids, List<ConnDelta> conns,
        out long sysRx, out long sysTx, out long sysDiskRead, out long sysDiskWrite)
    {
        pids.Clear();
        conns.Clear();

        foreach (var kv in _pids)
        {
            var c = kv.Value;
            long rx = Interlocked.Exchange(ref c.Rx, 0);
            long tx = Interlocked.Exchange(ref c.Tx, 0);
            long dr = Interlocked.Exchange(ref c.DiskRead, 0);
            long dw = Interlocked.Exchange(ref c.DiskWrite, 0);
            if ((rx | tx | dr | dw) != 0)
            {
                c.IdleDrains = 0;
                pids.Add(new PidDelta(kv.Key, rx, tx, dr, dw));
            }
            else if (++c.IdleDrains > IdleDrainsBeforePrune)
            {
                _pids.TryRemove(kv.Key, out _);
            }
        }

        foreach (var kv in _conns)
        {
            long b = Interlocked.Exchange(ref kv.Value.Bytes, 0);
            if (b != 0) conns.Add(new ConnDelta(kv.Key, b));
        }
        // Drop idle connection keys periodically so the dictionary does not grow without bound.
        if (_conns.Count > MaxTrackedConnections / 2)
            foreach (var kv in _conns)
                if (Volatile.Read(ref kv.Value.Bytes) == 0) _conns.TryRemove(kv.Key, out _);

        sysRx = Interlocked.Exchange(ref _sysRx, 0);
        sysTx = Interlocked.Exchange(ref _sysTx, 0);
        sysDiskRead = Interlocked.Exchange(ref _sysDiskRead, 0);
        sysDiskWrite = Interlocked.Exchange(ref _sysDiskWrite, 0);
    }
}
