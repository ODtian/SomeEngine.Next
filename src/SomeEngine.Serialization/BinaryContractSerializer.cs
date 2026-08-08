namespace SomeEngine.Serialization;

public static class BinaryContractSerializer
{
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
