namespace SomeEngine.ECS.Serialization;

public interface IComponentCodec<T>
    where T : struct
{
    /// <summary>Whether copies of this codec may Write concurrently with independent writers while preserving each value's encoding.</summary>
    bool SupportsParallelWrite => false;

    void Write(ref DataWriter writer, in T value);
    void Read(ref DataReader reader, out T value);
}

/// <summary>
/// Marker for codecs whose output is a stable, fixed-endian durable representation.
/// Source-generated codecs implement this interface.
/// </summary>
public interface ICanonicalComponentCodec<T> : IComponentCodec<T>
    where T : struct
{
}

