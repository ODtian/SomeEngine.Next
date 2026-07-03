using SomeEngine.ECS;

namespace SomeEngine.ECS.Tests;

public sealed class EcsConsumerTests
{
    [Fact]
    public void WorldCanCreateEntityAndStoreComponent()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.Add(entity, new Position { X = 3, Y = 7 });

        Assert.True(world.Has<Position>(entity));
        Assert.Equal(3, world.Get<Position>(entity).X);
        Assert.Equal(7, world.Get<Position>(entity).Y);
    }

    private struct Position : SomeEngine.ECS.Components.IComponent
    {
        public int X;
        public int Y;
    }
}
