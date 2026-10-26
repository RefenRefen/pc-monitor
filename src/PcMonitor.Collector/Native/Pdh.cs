using System.Runtime.InteropServices;

namespace PcMonitor.Collector.Native;

/// <summary>Thin wrapper over the Performance Data Helper API (GPU counters live there).</summary>
internal static unsafe class Pdh
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_CALC_NEGATIVE_DENOMINATOR = 0x800007D6;
    public const uint PDH_CSTATUS_NO_OBJECT = 0xC0000BB8;
    public const uint PDH_CSTATUS_NO_COUNTER = 0xC0000BB9;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, void* buffer);

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W on x64: pointer + { DWORD status; pad; 8-byte union }.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CounterItem
    {
        public char* Name;
        public uint Status;
        public uint Pad;
        public long Value;          // reinterpret as double for PDH_FMT_DOUBLE
    }

    public static double AsDouble(long raw) => BitConverter.Int64BitsToDouble(raw);

    public static bool IsGood(uint status) => status == 0 || status == 1; // PDH_CSTATUS_VALID_DATA / NEW_DATA
}

/// <summary>A PDH query holding one or more wildcard counters.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly List<IntPtr> _counters = new();
    private byte* _buffer;
    private uint _capacity;

    public int CounterCount => _counters.Count;

    public static PdhQuery? TryCreate(params string[] counterPaths)
    {
        var q = new PdhQuery();
        if (Pdh.PdhOpenQueryW(null, UIntPtr.Zero, out q._query) != 0) { q.Dispose(); return null; }
        foreach (string path in counterPaths)
        {
            if (Pdh.PdhAddEnglishCounterW(q._query, path, UIntPtr.Zero, out IntPtr c) != 0) { q.Dispose(); return null; }
            q._counters.Add(c);
        }
        q._capacity = 64 * 1024;
        q._buffer = (byte*)NativeMemory.Alloc(q._capacity);
        return q;
    }

    public bool Collect() => Pdh.PdhCollectQueryData(_query) == 0;

    /// <summary>Reads every instance of counter <paramref name="index"/>. Names are valid only inside the callback.</summary>
    public bool ReadArray(int index, uint format, Action<string, long> onItem)
    {
        uint size = _capacity, count = 0;
        uint rc = Pdh.PdhGetFormattedCounterArrayW(_counters[index], format, ref size, ref count, _buffer);
        if (rc == Pdh.PDH_MORE_DATA)
        {
            _buffer = (byte*)NativeMemory.Realloc(_buffer, size);
            _capacity = size;
            rc = Pdh.PdhGetFormattedCounterArrayW(_counters[index], format, ref size, ref count, _buffer);
        }
        if (rc != 0) return false;

        var items = (Pdh.CounterItem*)_buffer;
        for (uint i = 0; i < count; i++)
        {
            ref var it = ref items[i];
            if (!Pdh.IsGood(it.Status)) continue;
            onItem(new string(it.Name), it.Value);
        }
        return true;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buffer != null) { NativeMemory.Free(_buffer); _buffer = null; }
        _counters.Clear();
    }
}
