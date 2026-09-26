using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using Xunit;

namespace SomeEngine.ECS.Tests;

public sealed class EntityQueryLookupTests
{
    [Fact]
    public void LookupWritesOnlyTheRequestedRowAndTracksItsChange()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Position { X = 1 });
        Entity second = world.CreateEntity(new Position { X = 2 });
        Entity unrelated = world.CreateEntity(new Velocity { X = 3 });
        Entity removed = world.CreateEntity(new Position { X = 4 });
        world.DestroyEntity(removed);
        Entity replacement = world.CreateEntity(new Position { X = 5 });
        QueryHandle query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        uint before = world.CurrentTick;
        world.ExecuteQuery(query, values =>
        {
            Assert.False(values.TryGetRow(unrelated, out _));
            Assert.False(values.TryGetRow(removed, out _));
            Assert.True(values.TryGetRow(replacement, out QueryRow replaced));
            Assert.Equal(5, replaced.Read<Position>().X);
            Assert.True(values.TryGetRow(second, out QueryRow row));
            row.Write<Position>().X = 9;
        });
        Assert.Equal(1, world.Read<Position>(first).X);
        Assert.Equal(9, world.Read<Position>(second).X);
        QueryHandle changed = world.Query(world.QueryDefinition().Read<Position>().Changed<Position>());
        world.ExecuteQuery(changed, before, values =>
        {
            Assert.False(values.TryGetRow(first, out _));
            Assert.False(values.TryGetRow(replacement, out _));
            Assert.True(values.TryGetRow(second, out _));
        });
        world.ReleaseQuery(changed);
        world.ReleaseQuery(query);
    }

    [Fact]
    public void LookupHonorsEnabledAndChangedFilters()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new VisibilityState { Value = 1 });
        Entity second = world.CreateEntity(new VisibilityState { Value = 2 });
        Entity third = world.CreateEntity(new VisibilityState { Value = 3 });
        world.Disable<VisibilityState>(second);
        uint before = world.AcquireSystemTick();
        world.Replace(first, new VisibilityState { Value = 7 });
        world.Replace(second, new VisibilityState { Value = 8 });
        QueryHandle query = world.Query(world.QueryDefinition()
            .Read<VisibilityState>().Enabled<VisibilityState>().Changed<VisibilityState>());
        world.ExecuteQuery(query, before, values =>
        {
            Assert.True(values.TryGetRow(first, out QueryRow row));
            Assert.Equal(7, row.Read<VisibilityState>().Value);
            Assert.False(values.TryGetRow(second, out _));
            Assert.False(values.TryGetRow(third, out _));
        });
        world.ReleaseQuery(query);
    }

    [Fact]
    public void LookupDoesNotGrantUndeclaredWriteAccess()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, values =>
        {
            Assert.True(values.TryGetRow(entity, out QueryRow row));
            row.Write<Position>().X = 9;
        }));
        Assert.Equal(1, world.Read<Position>(entity).X);
        world.ReleaseQuery(query);
    }
}
