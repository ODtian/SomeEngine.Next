using System.Runtime.InteropServices;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Containers;

/// <summary>Byte storage with scalar value reads and writes over the common shared page index.</summary>
public sealed class PageBuffer : PageBuffer<byte>
{
    public PageBuffer(PageArena arena, long length = 0, PageAccess? access = null) : base(arena, length, access) { }
    internal PageBuffer(PageArena arena, long length, int pageLength, ResidencyReservation reservation)
        : base(arena, length, pageLength, reservation) { }
    private PageBuffer(PageBuffer source, PageAccess? access) : base(source, access) { }
    private PageBuffer(PageBuffer source, long offset, long length) : base(source, offset, length) { }

    public override PageBuffer Duplicate(PageAccess? access = null) => new(this, access);
    public override PageBuffer Slice(long offset, long length) => new(this, offset, length);

    /// <summary>Reads a source range directly into new pages. The caller keeps the source open until completion.</summary>
    public static async ValueTask<PageBuffer> ReadAsync(IRangeSource source, long offset, long length,
        PageArena arena, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        RangeValidation.Validate(offset, length, source.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new PageBuffer(arena, length);
        try
        {
            await result.FillAsync((position, memory, token) => source.ReadExactlyAsync(checked(offset + position), memory, token),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public new void Read(long offset, Span<byte> destination) => base.Read(offset, destination);
    public new void Write(long offset, ReadOnlySpan<byte> values) => base.Write(offset, values);
    public new void Write(long offset, long length, PageWrite<byte> writer) => base.Write(offset, length, writer);

    public T Read<T>(long offset) where T : unmanaged
    {
        T value = default;
        Read(offset, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
        return value;
    }

    public void Write<T>(long offset, T value) where T : unmanaged
        => base.Write(offset, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
}
