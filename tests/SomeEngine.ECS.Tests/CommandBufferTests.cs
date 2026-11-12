using SomeEngine.ECS.Serialization;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class CommandBufferTests
{
    // ════════════════════════════════════════════════
    // 基本回放
    // ════════════════════════════════════════════════

    [Fact]
    public void CreateEntity_Playback_EntityIsAlive()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        Assert.Equal(0, world.EntityCount);
        Assert.False(e.TryResolve(out _));
        cb.Playback();
        Assert.True(world.IsAlive(e.Resolve()));
    }

    [Fact]
    public void Add_Playback_HasComponentAndValueCorrect()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 10, Y = 20 });
        cb.Playback();
        Assert.True(world.Has<Position>(e));
        Assert.Equal(10f, world.Read<Position>(e).X);
        Assert.Equal(20f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Set_Playback_ValueUpdated()
    {
        var world = new World();
        var e = world.Create(new Position { X = 1, Y = 2 });
        var cb = new CommandBuffer(world);
        cb.Set(e, new Position { X = 99, Y = 88 });
        cb.Playback();
        Assert.Equal(99f, world.Read<Position>(e).X);
        Assert.Equal(88f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Remove_Playback_ComponentGone()
    {
        var world = new World();
        var e = world.Create(new Position { X = 1, Y = 2 });
        var cb = new CommandBuffer(world);
        cb.Remove<Position>(e);
        cb.Playback();
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void DestroyEntity_Playback_EntityDead()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Destroy(e);
        cb.Playback();
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void AddTag_Playback_HasTag()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, default(PlayerTag));
        cb.Playback();
        Assert.True(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void RemoveTag_Playback_TagGone()
    {
        var world = new World();
        var e = world.Create();
        world.Add(e, default(PlayerTag));
        var cb = new CommandBuffer(world);
        cb.Remove<PlayerTag>(e);
        cb.Playback();
        Assert.False(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void RemoveTag_MissingDuringPlayback_SkipsSilently()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Remove<PlayerTag>(e);

        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(e));
        Assert.False(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void AddTag_DuplicateDuringPlayback_SkipsSilently()
    {
        var world = new World();
        var e = world.Create();
        world.Add(e, default(PlayerTag));
        var cb = new CommandBuffer(world);
        cb.Add(e, default(PlayerTag));

        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(e));
        Assert.True(world.Has<PlayerTag>(e));
    }

    // ════════════════════════════════════════════════
    // 命令顺序
    // ════════════════════════════════════════════════

    [Fact]
    public void CreateThenAdd_CorrectResult()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        cb.Add(e, new Position { X = 42, Y = 0 });
        cb.Playback();
        Entity resolved = e.Resolve();
        Assert.True(world.IsAlive(resolved));
        Assert.True(world.Has<Position>(resolved));
        Assert.Equal(42f, world.Read<Position>(resolved).X);
    }

    [Fact]
    public void AddThenRemove_SameComponent_Removed()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Remove<Position>(e);
        cb.Playback();
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void MultipleAdd_DifferentTypes_AllPresent()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Add(e, new Velocity { X = 3, Y = 4 });
        cb.Playback();
        Assert.True(world.Has<Position>(e));
        Assert.True(world.Has<Velocity>(e));
        Assert.Equal(1f, world.Read<Position>(e).X);
        Assert.Equal(3f, world.Read<Velocity>(e).X);
    }

    [Fact]
    public void CandidatePlaybackFailurePreservesSourceAndStopsAfterSuccessfulPrefix()
    {
        using var world = new World();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        DeferredEntity first = commands.Create();
        commands.Add(first, new Position { X = 1, Y = 2 });
        commands.Add(Entity.Null, new Position());
        DeferredEntity later = commands.Create();
        commands.Add(later, new Velocity { X = 5, Y = 6 });
        Assert.Throws<ArgumentException>(commands.Playback);
        Assert.True(first.TryResolve(out Entity prefix));
        Assert.Equal(1, candidate.Read<Position>(prefix).X);
        Assert.False(later.TryResolve(out _));
        Assert.Equal(0, world.EntityCount);
    }

    // ════════════════════════════════════════════════
    // 命令局部延迟实体
    // ════════════════════════════════════════════════

    [Fact]
    public void CreateEntity_ReturnsUsableCommandLocalHandle()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        // 可以立即用于后续命令
        cb.Add(e, new Position { X = 100, Y = 200 });
        cb.Playback();
        Entity resolved = e.Resolve();
        Assert.True(world.IsAlive(resolved));
        Assert.Equal(100f, world.Read<Position>(resolved).X);
    }

    [Fact]
    public void MultipleCreateEntity_DifferentIds()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e1 = cb.Create();
        var e2 = cb.Create();
        var e3 = cb.Create();
        Assert.NotEqual(e1, e2);
        Assert.NotEqual(e2, e3);
        cb.Playback();
        Assert.True(world.IsAlive(e1.Resolve()));
        Assert.True(world.IsAlive(e2.Resolve()));
        Assert.True(world.IsAlive(e3.Resolve()));
    }

    [Fact]
    public void CreateEntity_PlaybackAlive_WithWorldIsAlive()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        // 在 Playback 之前 entity 尚未 finalized
        cb.Playback();
        Assert.True(world.IsAlive(e.Resolve()));
    }

    [Fact]
    public void CreateEntity_SecondPlayback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entity = cb.Create();

        cb.Playback();
        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(entity.Resolve()));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void RecordingCreate_DoesNotExposeOrAllocateLiveEntity()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entity = cb.Create();

        Assert.Equal(0, world.EntityCount);
        Assert.False(entity.TryResolve(out _));
        Assert.False(entity.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => entity.Resolve());

        cb.Playback();

        Entity resolved = entity.Resolve();
        Assert.True(world.IsAlive(resolved));
        Assert.Equal(1, world.EntityCount);
        Assert.False(world.Has<Position>(resolved));
    }

    // ════════════════════════════════════════════════
    // 迭代中使用
    // ════════════════════════════════════════════════

    [Fact]
    public void DuringIteration_CB_Add_DoesNotThrow()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        var query = world.Query(new QueryDescription().WithStored<Position>());
        using (QueryScope scope = query.OpenRead()) cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Playback();
        Assert.True(world.Has<Position>(e));
    }

    [Fact]
    public void DuringIteration_World_Add_Throws()
    {
        var world = new World();
        var e = world.Create();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        using QueryScope scope = query.OpenRead();
        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Position { X = 1, Y = 2 }));
    }

    [Fact]
    public void DuringIteration_Playback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.Create();
        var query = world.Query(new QueryDescription().WithStored<Position>());
        using QueryScope scope = query.OpenRead();
        Assert.Throws<InvalidOperationException>(() => cb.Playback());
    }

    // ════════════════════════════════════════════════
    // Clear / 复用
    // ════════════════════════════════════════════════

    [Fact]
    public void Clear_ThenPlayback_NoEffect()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Clear();
        cb.Playback();
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void Clear_InvalidatesPendingCreatedEntitiesWithoutAllocatorMutation()
    {
        var world = new World();
        var cb = new CommandBuffer(world);

        var entity = cb.Create();
        Assert.Equal(0, world.EntityCount);
        cb.Clear();

        Assert.False(entity.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => entity.Resolve());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void RecordingAndClear_DoNotConsumeNextAllocatorIdentity()
    {
        var recordedWorld = new World();
        Entity recordedSeed = recordedWorld.Create();
        recordedWorld.Destroy(recordedSeed);

        var controlWorld = new World();
        Entity controlSeed = controlWorld.Create();
        controlWorld.Destroy(controlSeed);

        var commands = new CommandBuffer(recordedWorld);
        DeferredEntity pending = commands.Create();

        Assert.Equal(0, recordedWorld.EntityCount);
        commands.Clear();

        Assert.False(pending.TryResolve(out _));
        Assert.Equal(controlWorld.Create(), recordedWorld.Create());
    }

    [Fact]
    public void RecordingAlone_DoesNotConsumeNextAllocatorIdentity()
    {
        var recordedWorld = new World();
        var controlWorld = new World();
        using var commands = new CommandBuffer(recordedWorld);
        DeferredEntity pending = commands.Create();

        Assert.Equal(controlWorld.Create(), recordedWorld.Create());
        Assert.False(pending.TryResolve(out _));
    }

    [Fact]
    public void FailedCandidatePlaybackPreservesSourceAllocatorIdentity()
    {
        using var source = new World();
        Entity seed = source.Create();
        source.Destroy(seed);
        using var control = new World();
        Entity controlSeed = control.Create();
        control.Destroy(controlSeed);
        using var checkpoint = new WorldCheckpoint().Capture(source, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        DeferredEntity prefix = commands.Create();
        commands.Add(prefix, new Position { X = 1 });
        commands.Add(Entity.Null, new Position());
        Assert.Throws<ArgumentException>(commands.Playback);
        Assert.True(prefix.TryResolve(out _));
        Assert.Equal(0, source.EntityCount);
        Assert.Equal(control.Create(), source.Create());
    }

    [Fact]
    public void Playback_PreservesExactNonSortedLifoFreeListAllocationOrder()
    {
        var commandWorld = new World();
        Entity commandFirst = commandWorld.Create();
        Entity commandSecond = commandWorld.Create();
        _ = commandWorld.Create();
        Entity commandFourth = commandWorld.Create();
        commandWorld.Destroy(commandFirst);
        commandWorld.Destroy(commandFourth);
        commandWorld.Destroy(commandSecond);

        var controlWorld = new World();
        Entity controlFirst = controlWorld.Create();
        Entity controlSecond = controlWorld.Create();
        _ = controlWorld.Create();
        Entity controlFourth = controlWorld.Create();
        controlWorld.Destroy(controlFirst);
        controlWorld.Destroy(controlFourth);
        controlWorld.Destroy(controlSecond);

        var commands = new CommandBuffer(commandWorld);
        DeferredEntity first = commands.Create();
        DeferredEntity second = commands.Create();
        DeferredEntity third = commands.Create();
        commands.Playback();

        Assert.Equal(controlWorld.Create(), first.Resolve());
        Assert.Equal(controlWorld.Create(), second.Resolve());
        Assert.Equal(controlWorld.Create(), third.Resolve());
    }

    [Fact]
    public void ClearAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity()
    {
        var world = new World();
        var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.Create();

        commands.Playback();
        Entity live = pending.Resolve();
        commands.Clear();

        Assert.True(world.IsAlive(live));
        Assert.True(pending.TryResolve(out Entity resolved));
        Assert.Equal(live, resolved);
        Assert.Equal(live, pending.Resolve());
        Assert.Throws<ArgumentException>(() => commands.Destroy(pending));
    }

    [Fact]
    public void DisposeAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity()
    {
        var world = new World();
        var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.Create();

        commands.Playback();
        Entity live = pending.Resolve();
        commands.Dispose();

        Assert.True(world.IsAlive(live));
        Assert.True(pending.TryResolve(out Entity resolved));
        Assert.Equal(live, resolved);
        Assert.Equal(live, pending.Resolve());
    }

    [Fact]
    public void BatchProducerPlaybackAppliesItsRecordedWrites()
    {
        using var world = new World();
        Entity entity = world.Create();
        using var batch = new CommandBatch(world, 1);
        CommandBuffer commands = batch[0];
        commands.Add(entity, new Position { X = 4, Y = 9 });
        Assert.Equal(1, commands.Count);
        commands.Playback();
        Assert.Equal(4, world.Read<Position>(entity).X);
        Assert.Equal(9, world.Read<Position>(entity).Y);
    }

    [Fact]
    public void CustomBufferRejectsRecordingAfterPlaybackUntilClear()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.Create();
        commands.Playback();
        Entity entity = pending.Resolve();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            commands.Add(entity, new Position { X = 1, Y = 2 }));

        commands.Clear();
        commands.Add(entity, new Position { X = 1, Y = 2 });
        commands.Playback();
        Assert.Equal(1, world.Read<Position>(entity).X);
    }

    [Fact]
    public void Clear_ThenRecordAndPlayback_Works()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Clear();
        cb.Add(e, new Velocity { X = 5, Y = 6 });
        cb.Playback();
        Assert.False(world.Has<Position>(e));
        Assert.True(world.Has<Velocity>(e));
        Assert.Equal(5f, world.Read<Velocity>(e).X);
    }

    // ════════════════════════════════════════════════
    // 边界情况
    // ════════════════════════════════════════════════

    [Fact]
    public void EmptyPlayback_NoThrow()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.Playback(); // 空回放不抛异常
    }

    [Fact]
    public void CommandsRejectExpiredEntityIdentity()
    {
        var world = new World();
        var e = world.Create();
        world.Destroy(e);
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        Assert.Throws<ArgumentException>(() => cb.Playback());
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void BulkCommands_1000_NoThrow()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entities = new DeferredEntity[1000];
        for (int i = 0; i < 1000; i++)
        {
            entities[i] = cb.Create();
            cb.Add(entities[i], new Position { X = i, Y = i * 2 });
        }
        cb.Playback();
        for (int i = 0; i < 1000; i++)
        {
            Entity resolved = entities[i].Resolve();
            Assert.True(world.IsAlive(resolved));
            Assert.Equal((float)i, world.Read<Position>(resolved).X);
        }
    }

    [Fact]
    public void Dispose_ThenUse_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cb.Create());
    }

    [Fact]
    public void Dispose_InvalidatesPendingCreatedEntitiesWithoutAllocatorMutation()
    {
        var world = new World();
        DeferredEntity entity;

        using (var cb = new CommandBuffer(world))
        {
            entity = cb.Create();
        }

        Assert.False(entity.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => entity.Resolve());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void CommandCount_TracksCommands()
    {
        var world = new World();
        var e = world.Create();
        var cb = new CommandBuffer(world);
        Assert.Equal(0, cb.Count);
        cb.Add(e, new Position { X = 1, Y = 2 });
        Assert.Equal(1, cb.Count);
        cb.Destroy(e);
        Assert.Equal(2, cb.Count);
        cb.Clear();
        Assert.Equal(0, cb.Count);
    }

    [Fact]
    public void CreateThenAddTag_Works()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.Create();
        cb.Add(e, default(PlayerTag));
        cb.Add(e, new Position { X = 1, Y = 2 });
        cb.Playback();
        Entity resolved = e.Resolve();
        Assert.True(world.IsAlive(resolved));
        Assert.True(world.Has<PlayerTag>(resolved));
        Assert.True(world.Has<Position>(resolved));
    }

    [Fact]
    public void DeferredEntity_AllOrdinaryCommandKindsResolveThroughSameFifo()
    {
        var world = new World();
        var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.Create();
        commands.Add(pending, new Position { X = 1, Y = 2 });
        commands.Set(pending, new Position { X = 3, Y = 4 });
        commands.Add(pending, default(PlayerTag));
        commands.Remove<PlayerTag>(pending);
        commands.Remove<Position>(pending);
        commands.Destroy(pending);

        commands.Playback();

        Entity createdThenDestroyed = pending.Resolve();
        Assert.False(world.IsAlive(createdThenDestroyed));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void DeferredEntity_CannotBeRecordedByAnotherBuffer()
    {
        var world = new World();
        using var owner = new CommandBuffer(world);
        using var other = new CommandBuffer(world);
        DeferredEntity pending = owner.Create();

        Assert.Throws<ArgumentException>(() =>
            other.Add(pending, new Position { X = 1, Y = 2 }));
    }

}
