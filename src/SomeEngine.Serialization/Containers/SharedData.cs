using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Serialization.Containers;

/// <summary>One counted reference to data. Generated code performs concrete copying and final release.</summary>
public sealed class SharedData<T>
{
    private Data? _data;
    public SharedData(T value) => _data = new(value);
    private SharedData(Data data) { _data = data; Interlocked.Increment(ref data.References); }
    public T Read => GetData().Value;
    public bool IsShared => Volatile.Read(ref GetData().References) > 1;
    public SharedData<T> Retain() => new(GetData());

    /// <summary>Releases this reference and returns the data only when its last reference has ended.</summary>
    public bool Release([MaybeNullWhen(false)] out T value)
    {
        Data? data = Interlocked.Exchange(ref _data, null);
        if (data is not null && Interlocked.Decrement(ref data.References) == 0)
        {
            value = data.Value;
            return true;
        }
        value = default;
        return false;
    }

    private Data GetData() => Volatile.Read(ref _data)
        ?? throw new ObjectDisposedException(nameof(SharedData<T>));

    private sealed class Data(T value)
    {
        internal readonly T Value = value;
        internal int References = 1;
    }
}
