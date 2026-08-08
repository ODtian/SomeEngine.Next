using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public static partial class WorldSerializer
{
    /// <summary>
    /// Computes the shared inline <see cref="Digest256"/> over the canonical serialized World
    /// image without materializing either the image or a separate digest byte array.
    /// </summary>
    /// <remarks>
    /// The hash covers the same contract marker, type manifest, entity identities and slots,
    /// component payloads, and relation/hierarchy topology as <see cref="WriteWorld"/>. Callers
    /// comparing hashes across builds or machines must select codecs and a serialization contract
    /// whose schema and byte representation are portable for that use case.
    /// </remarks>
    public static Digest256 ComputeWorldStateHash(
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        using var stream = new HashingWriteStream(Stream.Null);
        WriteWorld(stream, world, registry, options);
        return stream.CompleteDigest();
    }
}
