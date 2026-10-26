using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcMonitor.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PipeMessage))]
public partial class PipeJsonContext : JsonSerializerContext { }

/// <summary>4-byte little-endian length prefix followed by UTF-8 JSON.</summary>
public static class PipeFraming
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    public static async ValueTask WriteAsync(Stream stream, PipeMessage message, CancellationToken ct = default)
    {
        var writer = new ArrayBufferWriter<byte>(4096);
        writer.GetSpan(4).Slice(0, 4).Clear();
        writer.Advance(4);
        using (var json = new Utf8JsonWriter(writer))
            JsonSerializer.Serialize(json, message, PipeJsonContext.Default.PipeMessage);

        var mem = MemoryMarshalAsArray(writer);
        BinaryPrimitives.WriteInt32LittleEndian(mem.Span, writer.WrittenCount - 4);
        await stream.WriteAsync(mem, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static Memory<byte> MemoryMarshalAsArray(ArrayBufferWriter<byte> w)
    {
        // WrittenMemory is read-only; the backing array is ours so a writable view is safe.
        return System.Runtime.InteropServices.MemoryMarshal.AsMemory(w.WrittenMemory);
    }

    /// <summary>Returns null on clean end of stream.</summary>
    public static async ValueTask<PipeMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len <= 0 || len > MaxMessageBytes) throw new InvalidDataException($"Bad frame length {len}");

        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            if (!await ReadExactAsync(stream, rented.AsMemory(0, len), ct).ConfigureAwait(false))
                throw new EndOfStreamException();
            return JsonSerializer.Deserialize(rented.AsSpan(0, len), PipeJsonContext.Default.PipeMessage);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async ValueTask<bool> ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (n == 0) return total == 0 ? false : throw new EndOfStreamException();
            total += n;
        }
        return true;
    }
}
