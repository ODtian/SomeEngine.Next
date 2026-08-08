using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SerializationConsumerTests
{
    [Fact]
    public void SerializationContextCanRoundTripEmptyWorld()
    {
        var world = new World();
        var registry = new SerializationRegistry();
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, world, registry);

        stream.Position = 0;
        using World restored = WorldSerializer.ReadWorld(stream, registry);

        Assert.Equal(world.EntityCount, restored.EntityCount);
    }
}
