using System.Runtime.InteropServices;

namespace SomeEngine.Serialization.Containers;

/// <summary>Shared byte storage with explicit independent writes.</summary>
public sealed class SharedBytes : IDisposable
{
    private SharedData<Bytes> _data;
    public SharedBytes(ReadOnlyMemory<byte> bytes, bool writable = false) => _data = new(new(bytes, writable));
    private SharedBytes(SharedData<Bytes> data) => _data = data;
    public ReadOnlyMemory<byte> Read => _data.Read.Memory;
    public SharedBytes Retain() => new(_data.Retain());
    public Memory<byte> Write()
    {
        Bytes bytes = _data.Read;
        if (_data.IsShared || !bytes.Writable)
        {
            var replacement = new SharedData<Bytes>(new(bytes.Memory.ToArray(), true));
            _data.Release(out _);
            _data = replacement;
        }
        return MemoryMarshal.AsMemory(_data.Read.Memory);
    }
    public void Dispose() => _data.Release(out _);

    private sealed class Bytes(ReadOnlyMemory<byte> memory, bool writable)
    {
        internal readonly ReadOnlyMemory<byte> Memory = memory;
        internal readonly bool Writable = writable;
    }
}
