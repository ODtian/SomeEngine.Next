using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SerializationConsumerTests
{
    [Fact]
    public void SerializationContextCanRoundTripEmptyWorld()
    {
        var world = new World();
        var bytes = WorldSerializer.Serialize(world);

        var restored = WorldSerializer.Deserialize(bytes);

        Assert.Equal(world.EntityCount, restored.EntityCount);
    }
}
