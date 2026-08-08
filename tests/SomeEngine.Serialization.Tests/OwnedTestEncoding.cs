using SomeEngine.Serialization;

namespace SomeEngine.Serialization.Tests;

/// <summary>
/// Test-only fixed-capacity owner. The contract codec is invoked exactly once and the encoded
/// value never has a second backing store; <see cref="Span"/> and <see cref="Memory"/> are views.
/// </summary>
internal sealed class OwnedTestEncoding
{
    private readonly byte[] _buffer;

    private OwnedTestEncoding(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    internal int Length { get; private set; }
    internal int Capacity => _buffer.Length;
    internal Span<byte> Span => _buffer.AsSpan(0, Length);
    internal Memory<byte> Memory => _buffer.AsMemory(0, Length);

    internal byte this[int index]
    {
        get => _buffer[index];
        set => _buffer[index] = value;
    }

    internal void SetLength(int length)
    {
        if ((uint)length > (uint)_buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
    }

    internal static OwnedTestEncoding Encode<T>(T value, int capacity = 4 * 1024 * 1024)
        where T : IBinaryContract<T>
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(capacity);
        if (!BinaryContractSerializer.TryWrite(buffer, value, out int written))
        {
            throw new InvalidOperationException(
                $"The fixed test destination ({capacity} bytes) is too small for {typeof(T).FullName}.");
        }

        return new OwnedTestEncoding(buffer, written);
    }

    internal static OwnedTestEncoding Allocate(int capacity)
        => new(GC.AllocateUninitializedArray<byte>(capacity), capacity);
}
