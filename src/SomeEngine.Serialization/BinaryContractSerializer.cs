namespace SomeEngine.Serialization;

public static class BinaryContractSerializer
{
    /// <summary>Copies encoded fields without retaining mutable references from the source.</summary>
    public static T Copy<T>(T value, BinaryReadLimits? limits = null)
        where T : IBinaryContract<T>
    {
        var bytes = new FieldBytes();
        Write(bytes, value);
        return Deserialize<T>(bytes.WrittenSpan, limits);
    }

    private sealed class FieldBytes : IStreamingBinarySink
    {
        private readonly System.Buffers.ArrayBufferWriter<byte> _bytes = new();
        internal ReadOnlySpan<byte> WrittenSpan => _bytes.WrittenSpan;
        public void Advance(int count) => _bytes.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _bytes.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _bytes.GetSpan(sizeHint);
        public void WriteDirect(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_bytes.GetSpan(value.Length));
            _bytes.Advance(value.Length);
        }
        public void WriteZeroesDirect(int count)
        {
            _bytes.GetSpan(count)[..count].Clear();
            _bytes.Advance(count);
        }
    }

    /// <summary>
    /// Encodes once into caller-owned final storage. A too-small destination is left partially
    /// written and is never retried, because retrying would invoke the contract codec twice.
    /// </summary>
    public static bool TryWrite<T>(Span<byte> destination, T value, out int written)
        where T : IBinaryContract<T>
    {
        var writer = new BinaryDataWriter(destination);
        try
        {
            T.Write(ref writer, value);
            written = writer.WrittenCount;
            return true;
        }
        catch (BinaryDestinationTooSmallException)
        {
            written = 0;
            return false;
        }
    }

    internal static int Write<T>(IStreamingBinarySink destination, T value)
        where T : IBinaryContract<T>
    {
        var writer = new BinaryDataWriter(destination);
        T.Write(ref writer, value);
        return writer.WrittenCount;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> source, BinaryReadLimits? limits = null)
        where T : IBinaryContract<T>
    {
        var reader = new BinaryDataReader(source, limits);
        T value = T.Read(ref reader);
        reader.EnsureFullyConsumed(typeof(T).FullName);
        return value;
    }
}
