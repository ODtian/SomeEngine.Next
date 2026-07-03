using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct CleanupMarker : SomeEngine.ECS.Components.ICleanupComponent
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

        var entity = world.Spawn(new CleanupPositionBundle
        {
            Cleanup = new CleanupMarker { Value = 2 },
            Position = new Position { X = 3, Y = 4 },
        });

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
    public void PendingCleanup_Destroy()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 5, Y = 6 });

        world.Remove<Position>(entity);
        Assert.True(world.IsPendingCleanup(entity));

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
