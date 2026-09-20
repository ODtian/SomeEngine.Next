namespace SomeEngine.Serialization.Containers;

/// <summary>A retained read of bytes. Independent writers keep this content unchanged until disposal.</summary>
public sealed class BufferRead : IDisposable
{
    private SharedBytes? _data;

    public BufferRead(SharedBytes data) => _data = data.Retain();

    public BufferRead(ReadOnlyMemory<byte> bytes)
        => _data = new(bytes);

    public ReadOnlyMemory<byte> Memory => (Volatile.Read(ref _data)
        ?? throw new ObjectDisposedException(nameof(BufferRead))).Read;

    public void Dispose() => Interlocked.Exchange(ref _data, null)?.Dispose();
}
