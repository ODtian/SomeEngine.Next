using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class QueryPresenceTests
{
    [Fact]
    public void PresenceFollowsRequiredBuffersAndEnableChanges()
    {
        using var world = new World();
        QueryHandle query = world.Query(world.QueryDefinition().ReadWrite<Visible>().Enabled<Visible>().WriteBuffer<Sample>());
        Entity entity = world.CreateEntity(new Visible());
        Assert.False(world.HasQueryMembers(query));

        world.AddBuffer<Sample>(entity);
        Assert.True(world.HasQueryMembers(query));
        world.Disable<Visible>(entity);
        Assert.False(world.HasQueryMembers(query));
        world.Enable<Visible>(entity);
        Assert.True(world.HasQueryMembers(query));
        world.DestroyEntity(entity);
        Assert.False(world.HasQueryMembers(query));
    }

    [Fact]
    public void PresencePreservesWriteVersionsAndUsesTheQueriesStructuralMembership()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Visible());
        QueryHandle query = world.Query(world.QueryDefinition().ReadWrite<Visible>().Changed<Visible>());
        uint version = world.WriteVersion<Visible>(entity);
        uint tick = world.CurrentTick;

        Assert.True(world.HasQueryMembers(query));

        Assert.Equal(version, world.WriteVersion<Visible>(entity));
        Assert.Equal(tick, world.CurrentTick);
    }

    private struct Visible : IEnableableComponent;
    private struct Sample : IBufferElement;
}
