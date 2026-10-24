using System.Buffers;

namespace SomeEngine.Serialization;

/// <summary>Encodes and decodes values through their static binary serialization methods.</summary>
public static class BinarySerializer
{
    /// <summary>Decodes into caller-selected storage. A failure can leave the target partially changed.</summary>
    public static void DeserializeInto<T>(ReadOnlySpan<byte> source, T target, BinaryReadLimits? limits = null)
        where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        ArgumentNullException.ThrowIfNull(target);
        var reader = new BinaryDataReader(source, limits);
        T.ReadInto(ref reader, target);
        reader.EnsureFullyConsumed(typeof(T).FullName);
    }

    /// <summary>Decodes segments into caller-selected storage without concatenating input.</summary>
    public static void DeserializeInto<T>(in ReadOnlySequence<byte> source, T target, BinaryReadLimits? limits = null)
        where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        ArgumentNullException.ThrowIfNull(target);
        var reader = new BinaryDataReader(source, limits);
        T.ReadInto(ref reader, target);
        reader.EnsureFullyConsumed(typeof(T).FullName);
    }

    /// <summary>Copies encoded fields without retaining mutable references from the source.</summary>
    public static T Copy<T>(T value, BinaryReadLimits? limits = null)
        where T : IBinarySerializable<T>
    {
        var bytes = new ArrayBufferWriter<byte>();
        Write(bytes, value);
        return Deserialize<T>(bytes.WrittenSpan, limits);
    }

    /// <summary>
    /// Encodes once into caller-owned final storage. A too-small destination is left partially
    /// written and is never retried, because retrying would invoke the codec twice.
    /// </summary>
    public static bool TryWrite<T>(Span<byte> destination, T value, out int written)
        where T : IBinarySerializable<T>
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

    /// <summary>Encodes once without measuring or buffering the whole value. The caller controls output storage and flushing.</summary>
    public static int Write<T>(IBufferWriter<byte> destination, T value)
        where T : IBinarySerializable<T>
    {
        var writer = new BinaryDataWriter(destination);
        T.Write(ref writer, value);
        return writer.WrittenCount;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> source, BinaryReadLimits? limits = null)
        where T : IBinarySerializable<T>
    {
        var reader = new BinaryDataReader(source, limits);
        T value = T.Read(ref reader);
        return CompleteRead(ref reader, value);
    }

    /// <summary>Decodes directly from segments. Owned fields allocate their final storage; the input is not concatenated.</summary>
    public static T Deserialize<T>(in ReadOnlySequence<byte> source, BinaryReadLimits? limits = null)
        where T : IBinarySerializable<T>
    {
        var reader = new BinaryDataReader(source, limits);
        T value = T.Read(ref reader);
        return CompleteRead(ref reader, value);
    }
    private static T CompleteRead<T>(ref BinaryDataReader reader, T value)
    {
        try { reader.EnsureFullyConsumed(typeof(T).FullName); return value; }
        catch (Exception failure)
        {
            try { if (value is IDisposable disposable) disposable.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Decoding and result cleanup failed.", failure, cleanup); }
            throw;
        }
    }
}
