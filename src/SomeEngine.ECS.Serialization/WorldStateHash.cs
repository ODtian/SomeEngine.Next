using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer
{
    /// <summary>Computes the World save digest through the same version 5 encoding.</summary>
    public static Digest256 ComputeWorldStateHash(World world, SerializationRegistry registry,
        SerializeOptions options = default)
    {
        using var output = new HashingWriteStream(Stream.Null);
        new WorldSerializer(registry).Save(world, output, options);
        return output.CompleteDigest();
    }
}
