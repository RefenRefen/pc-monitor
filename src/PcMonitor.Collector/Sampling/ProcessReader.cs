using System.Runtime.InteropServices;
using PcMonitor.Collector.Native;

namespace PcMonitor.Collector.Sampling;

/// <summary>One SYSTEM_PROCESS_INFORMATION record, copied out of the native buffer without allocation.</summary>
internal unsafe struct RawProcess
{
    public int Pid;
    public int ParentPid;
    public int Threads;
    public int Handles;
    public int SessionId;
    /// <summary>Private working set (WorkingSetPrivateSize) - what Task Manager's Memory column shows.</summary>
    public long PrivateWorkingSet;
    public long WorkingSet;
    /// <summary>PagefileUsage = commit charge / private bytes.</summary>
    public long Commit;
    /// <summary>FILETIME (100 ns since 1601).</summary>
    public long CreateTime;
    /// <summary>User + kernel time in 100 ns units.</summary>
    public long CpuTime;
    public long ReadBytes;
    public long WriteBytes;
    public long OtherBytes;
    public char* ImageName;
    public int ImageNameChars;

    public readonly string NameString() => ImageNameChars == 0 ? "" : new string(ImageName, 0, ImageNameChars);
}

/// <summary>
/// Wraps a single NtQuerySystemInformation(SystemProcessInformation) call, which returns every
/// process (CPU times, memory, IO counters) in one pass. That is far cheaper than WMI/PDH per process.
/// </summary>
internal sealed unsafe class ProcessReader : IDisposable
{
    // x64 field offsets inside SYSTEM_PROCESS_INFORMATION.
    private const int OffNextEntry = 0;
    private const int OffThreads = 4;
    private const int OffWorkingSetPrivate = 8;
    private const int OffCreateTime = 32;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLength = 56;   // USHORT
    private const int OffImageNameBuffer = 64;   // PWSTR
    private const int OffPid = 80;
    private const int OffParentPid = 88;
    private const int OffHandles = 96;
    private const int OffSession = 100;
    private const int OffWorkingSet = 144;
    private const int OffPagefileUsage = 184;
    private const int OffReadTransfer = 232;
    private const int OffWriteTransfer = 240;
    private const int OffOtherTransfer = 248;
    private const int MinRecordSize = 256;

    private byte* _buffer;
    private uint _capacity;

    public ProcessReader(uint initialCapacity = 1 << 20)
    {
        _capacity = initialCapacity;
        _buffer = (byte*)NativeMemory.Alloc(_capacity);
    }

    /// <summary>Fills the internal buffer with a fresh snapshot. Returns the first record or null.</summary>
    public bool Snapshot()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemProcessInformation, _buffer, _capacity, out uint needed);
            if (status == 0) return true;
            if (status == NativeMethods.StatusInfoLengthMismatch || status == NativeMethods.StatusBufferTooSmall)
            {
                // The process list can grow between calls, so over-allocate.
                uint next = Math.Max(needed + (needed >> 2) + 65536, _capacity * 2);
                _buffer = (byte*)NativeMemory.Realloc(_buffer, next);
                _capacity = next;
                continue;
            }
            return false;
        }
        return false;
    }

    /// <summary>Enumerates the records of the last snapshot. The name pointer is valid until the next Snapshot().</summary>
    public RecordEnumerator Records() => new(_buffer);

    public unsafe ref struct RecordEnumerator
    {
        private byte* _next;
        private RawProcess _current;

        internal RecordEnumerator(byte* start) { _next = start; _current = default; }

        public RawProcess Current => _current;

        public RecordEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next == null) return false;
            byte* p = _next;
            _current = Parse(p);
            uint nextOffset = *(uint*)(p + OffNextEntry);
            _next = nextOffset == 0 ? null : p + nextOffset;
            return true;
        }
    }

    internal static RawProcess Parse(byte* p)
    {
        RawProcess r;
        r.Threads = *(int*)(p + OffThreads);
        r.PrivateWorkingSet = *(long*)(p + OffWorkingSetPrivate);
        r.CreateTime = *(long*)(p + OffCreateTime);
        r.CpuTime = *(long*)(p + OffUserTime) + *(long*)(p + OffKernelTime);
        ushort nameBytes = *(ushort*)(p + OffImageNameLength);
        r.ImageNameChars = nameBytes / 2;
        r.ImageName = *(char**)(p + OffImageNameBuffer);
        r.Pid = (int)*(long*)(p + OffPid);
        r.ParentPid = (int)*(long*)(p + OffParentPid);
        r.Handles = *(int*)(p + OffHandles);
        r.SessionId = *(int*)(p + OffSession);
        r.WorkingSet = (long)*(ulong*)(p + OffWorkingSet);
        r.Commit = (long)*(ulong*)(p + OffPagefileUsage);
        r.ReadBytes = *(long*)(p + OffReadTransfer);
        r.WriteBytes = *(long*)(p + OffWriteTransfer);
        r.OtherBytes = *(long*)(p + OffOtherTransfer);
        return r;
    }

    public void Dispose()
    {
        if (_buffer != null) { NativeMemory.Free(_buffer); _buffer = null; }
    }
}
