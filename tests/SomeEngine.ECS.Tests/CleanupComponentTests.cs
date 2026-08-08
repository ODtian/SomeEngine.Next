using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct CleanupMarker : SomeEngine.ECS.ICleanupComponent
{
    public int Value;
}

public class CleanupComponentTests
{
    [Fact]
    public void Storage_Table_IsCleanup_ForICleanupComponent()
    {
        Assert.Equal(StoragePath.Table, ComponentMetadata<CleanupMarker>.Storage);
        Assert.True(ComponentMetadata<CleanupMarker>.IsCleanup);
    }

    [Fact]
    public void DestroyEntity_WithCleanup_SoftDestroys()
    {
        var world = new World();
        var entity = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 7 },
            Position = new Position { X = 1, Y = 2 },
        });

        world.DestroyEntity(entity);

        Assert.True(world.IsAlive(entity));
        Assert.True(world.Has<CleanupMarker>(entity));
        Assert.False(world.Has<Position>(entity));
        Assert.True(world.IsPendingCleanup(entity));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void DestroyEntity_WithCleanup_WarmedTransition_DoesNotAllocate()
    {
        var world = new World();
        var warm = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 1 },
            Position = new Position { X = 1, Y = 2 },
        });
        world.DestroyEntity(warm); // warm cleanup transition plan

        var first = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 2 },
            Position = new Position { X = 3, Y = 4 },
        });
        var entity = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 3 },
            Position = new Position { X = 5, Y = 6 },
        });

        // The last structural publication intentionally shares untouched chunks with the old
        // root. Warm the bounded first-write detach as well as the transition cache; subsequent
        // moves within the same published root retain the allocation-free hot path.
        world.DestroyEntity(first);

        long before = GC.GetAllocatedBytesForCurrentThread();
        world.DestroyEntity(entity);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.True(world.IsPendingCleanup(entity));
    }

    [Fact]
    public void RemoveLastCleanup_TriggersRealDestroy()
    {
        var world = new World();
        var entity = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 9 },
            Position = new Position { X = 3, Y = 4 },
        });

        world.DestroyEntity(entity);
        world.Remove<CleanupMarker>(entity);

        Assert.False(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void CleanupOnlyEntity_RemainsPendingUntilCleanupIsRemoved()
    {
        var world = new World();
        var entity = world.CreateEntity(new CleanupMarker { Value = 9 });

        world.DestroyEntity(entity);

        Assert.True(world.IsAlive(entity));
        Assert.True(world.IsPendingCleanup(entity));
        Assert.True(world.Has<CleanupMarker>(entity));

        world.Remove<CleanupMarker>(entity);
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void RemovedFactAlone_DoesNotRequestDestroy()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 5, Y = 6 });

        world.Remove<Position>(entity);
        Assert.True(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
        Assert.True(world.Has<Removed<Position>>(entity));

        world.DestroyEntity(entity);

        Assert.False(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
    }

    [Fact]
    public void DestroyEntity_WithoutCleanup_StillHardDestroys()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 5, Y = 6 });

        world.DestroyEntity(entity);

        Assert.False(world.IsAlive(entity));
        Assert.False(world.IsPendingCleanup(entity));
    }
}
