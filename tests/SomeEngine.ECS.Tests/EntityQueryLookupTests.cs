using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Tests;

public sealed class EntityQueryLookupTests
{
    [Fact]
    public void LookupWritesOnlyTheRequestedRowAndTracksItsChange()
    {
        using var world = new World();
        Entity first = world.Create(new Position { X = 1 });
        Entity second = world.Create(new Position { X = 2 });
        Entity unrelated = world.Create(new Velocity { X = 3 });
        Entity removed = world.Create(new Position { X = 4 });
        world.Destroy(removed);
        Entity replacement = world.Create(new Position { X = 5 });
        Query query = world.Query<Position>();
        ulong before = world.Version;
        using (QueryScope members = query.OpenRead())
        {
            var values = world.OpenWrite<Position>();
            try
            {
                Assert.False(members.Contains(unrelated));
                Assert.False(members.Contains(removed));
                Assert.True(members.Contains(replacement));
                Assert.Equal(5, world.Read<Position>(replacement).X);
                Assert.True(members.Contains(second));
                values.Get(second).X = 9;
            }
            finally { values.Dispose(); }
        }
        Assert.Equal(1, world.Read<Position>(first).X);
        Assert.Equal(9, world.Read<Position>(second).X);
        Query changed = world.Query(new QueryDescription().WithChanged<Position>(before));
        using QueryScope changes = changed.OpenRead();
        Assert.False(changes.Contains(first));
        Assert.False(changes.Contains(replacement));
        Assert.True(changes.Contains(second));
    }

    [Fact]
    public void LookupHonorsEnabledAndChangedFilters()
    {
        using var world = new World();
        Entity first = world.Create(new VisibilityState { Value = 1 });
        Entity second = world.Create(new VisibilityState { Value = 2 });
        Entity third = world.Create(new VisibilityState { Value = 3 });
        world.Disable<VisibilityState>(second);
        ulong before = world.Version;
        world.Set(first, new VisibilityState { Value = 7 });
        world.Set(second, new VisibilityState { Value = 8 });
        Query query = world.Query(new QueryDescription().With<VisibilityState>().WithChanged<VisibilityState>(before));
        using QueryScope members = query.OpenRead();
        using ReadLookup<VisibilityState> values = world.OpenRead<VisibilityState>();
        Assert.True(members.Contains(first));
        Assert.Equal(7, values.Read(first).Value);
        Assert.False(members.Contains(second));
        Assert.False(members.Contains(third));
    }

    [Fact]
    public void LookupDoesNotGrantUndeclaredWriteAccess()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity entity = world.Create(new Position { X = 1 });
        Query query = world.Query<Position>();
        using (QueryScope scope = query.OpenRead())
        {
            Assert.True(scope.Contains(entity));
            foreach (QueryChunk chunk in scope)
                Assert.ThrowsAny<InvalidOperationException>(() => chunk.Write<Position>(0).X = 9);
        }
        Assert.Equal(1, world.ReadComponentData<Position>(entity).X);
    }
}
