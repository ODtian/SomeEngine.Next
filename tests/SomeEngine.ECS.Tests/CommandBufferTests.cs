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
        var e = cb.CreateEntity();
        Assert.Equal(0, world.EntityCount);
        Assert.False(e.TryResolve(out _));
        cb.Playback();
        Assert.True(world.IsAlive(e.Resolve()));
    }

    [Fact]
    public void Add_Playback_HasComponentAndValueCorrect()
    {
        var world = new World();
        var e = world.CreateEntity();
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
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var cb = new CommandBuffer(world);
        cb.Replace(e, new Position { X = 99, Y = 88 });
        cb.Playback();
        Assert.Equal(99f, world.Read<Position>(e).X);
        Assert.Equal(88f, world.Read<Position>(e).Y);
    }

    [Fact]
    public void Remove_Playback_ComponentGone()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var cb = new CommandBuffer(world);
        cb.Remove<Position>(e);
        cb.Playback();
        Assert.False(world.Has<Position>(e));
    }

    [Fact]
    public void DestroyEntity_Playback_EntityDead()
    {
        var world = new World();
        var e = world.CreateEntity();
        var cb = new CommandBuffer(world);
        cb.DestroyEntity(e);
        cb.Playback();
        Assert.False(world.IsAlive(e));
    }

    [Fact]
    public void AddTag_Playback_HasTag()
    {
        var world = new World();
        var e = world.CreateEntity();
        var cb = new CommandBuffer(world);
        cb.AddTag<PlayerTag>(e);
        cb.Playback();
        Assert.True(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void RemoveTag_Playback_TagGone()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddTag<PlayerTag>(e);
        var cb = new CommandBuffer(world);
        cb.RemoveTag<PlayerTag>(e);
        cb.Playback();
        Assert.False(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void RemoveTag_MissingDuringPlayback_SkipsSilently()
    {
        var world = new World();
        var e = world.CreateEntity();
        var cb = new CommandBuffer(world);
        cb.RemoveTag<PlayerTag>(e);

        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(e));
        Assert.False(world.Has<PlayerTag>(e));
    }

    [Fact]
    public void AddTag_DuplicateDuringPlayback_SkipsSilently()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.AddTag<PlayerTag>(e);
        var cb = new CommandBuffer(world);
        cb.AddTag<PlayerTag>(e);

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
        var e = cb.CreateEntity();
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
        var e = world.CreateEntity();
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
        var e = world.CreateEntity();
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
    public void PlaybackFailureLeavesLiveWorldUnchanged()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var first = cb.CreateEntity();
        cb.Add(first, new Position { X = 1, Y = 2 });
        cb.Add(first, new Position { X = 3, Y = 4 });
        var later = cb.CreateEntity();
        cb.Add(later, new Velocity { X = 5, Y = 6 });

        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.False(first.TryResolve(out _));
        Assert.False(later.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => first.Resolve());
        Assert.Throws<InvalidOperationException>(() => later.Resolve());
        Assert.Equal(0, world.EntityCount);

        cb.Clear();

        Assert.False(first.TryResolve(out _));
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
        var e = cb.CreateEntity();
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
        var e1 = cb.CreateEntity();
        var e2 = cb.CreateEntity();
        var e3 = cb.CreateEntity();
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
        var e = cb.CreateEntity();
        // 在 Playback 之前 entity 尚未 finalized
        cb.Playback();
        Assert.True(world.IsAlive(e.Resolve()));
    }

    [Fact]
    public void CreateEntity_SecondPlayback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entity = cb.CreateEntity();

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
        var entity = cb.CreateEntity();

        Assert.Equal(0, world.EntityCount);
        Assert.False(entity.IsResolved);
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
        var e = world.CreateEntity();
        var cb = new CommandBuffer(world);
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, _ =>
            cb.Add(e, new Position { X = 1, Y = 2 }));
        cb.Playback();
        Assert.True(world.Has<Position>(e));
    }

    [Fact]
    public void DuringIteration_World_Add_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, _ =>
            Assert.Throws<InvalidOperationException>(() =>
                world.Add(e, new Position { X = 1, Y = 2 })));
    }

    [Fact]
    public void DuringIteration_Playback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.CreateEntity();
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, _ =>
            Assert.Throws<InvalidOperationException>(() => cb.Playback()));
    }

    // ════════════════════════════════════════════════
    // Clear / 复用
    // ════════════════════════════════════════════════

    [Fact]
    public void Clear_ThenPlayback_NoEffect()
    {
        var world = new World();
        var e = world.CreateEntity();
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

        var entity = cb.CreateEntity();
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
        Entity recordedSeed = recordedWorld.CreateEntity();
        recordedWorld.DestroyEntity(recordedSeed);

        var controlWorld = new World();
        Entity controlSeed = controlWorld.CreateEntity();
        controlWorld.DestroyEntity(controlSeed);

        var commands = new CommandBuffer(recordedWorld);
        DeferredEntity pending = commands.CreateEntity();

        Assert.Equal(0, recordedWorld.EntityCount);
        commands.Clear();

        Assert.False(pending.TryResolve(out _));
        Assert.Equal(controlWorld.CreateEntity(), recordedWorld.CreateEntity());
    }

    [Fact]
    public void RecordingAlone_DoesNotConsumeNextAllocatorIdentity()
    {
        var recordedWorld = new World();
        var controlWorld = new World();
        using var commands = new CommandBuffer(recordedWorld);
        DeferredEntity pending = commands.CreateEntity();

        Assert.Equal(controlWorld.CreateEntity(), recordedWorld.CreateEntity());
        Assert.False(pending.TryResolve(out _));
    }

    [Fact]
    public void ValidationFailure_DoesNotConsumeNextAllocatorIdentity()
    {
        var failedWorld = new World();
        Entity failedSeed = failedWorld.CreateEntity();
        failedWorld.DestroyEntity(failedSeed);

        var controlWorld = new World();
        Entity controlSeed = controlWorld.CreateEntity();
        controlWorld.DestroyEntity(controlSeed);

        var commands = new CommandBuffer(failedWorld);
        DeferredEntity pending = commands.CreateEntity();
        commands.Add(pending, new Position { X = 1, Y = 2 });
        commands.Add(pending, new Position { X = 3, Y = 4 });

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(0, failedWorld.EntityCount);
        Assert.False(pending.TryResolve(out _));
        Assert.Equal(controlWorld.CreateEntity(), failedWorld.CreateEntity());
    }

    [Fact]
    public void Playback_PreservesExactNonSortedLifoFreeListAllocationOrder()
    {
        var commandWorld = new World();
        Entity commandFirst = commandWorld.CreateEntity();
        Entity commandSecond = commandWorld.CreateEntity();
        _ = commandWorld.CreateEntity();
        Entity commandFourth = commandWorld.CreateEntity();
        commandWorld.DestroyEntity(commandFirst);
        commandWorld.DestroyEntity(commandFourth);
        commandWorld.DestroyEntity(commandSecond);

        var controlWorld = new World();
        Entity controlFirst = controlWorld.CreateEntity();
        Entity controlSecond = controlWorld.CreateEntity();
        _ = controlWorld.CreateEntity();
        Entity controlFourth = controlWorld.CreateEntity();
        controlWorld.DestroyEntity(controlFirst);
        controlWorld.DestroyEntity(controlFourth);
        controlWorld.DestroyEntity(controlSecond);

        var commands = new CommandBuffer(commandWorld);
        DeferredEntity first = commands.CreateEntity();
        DeferredEntity second = commands.CreateEntity();
        DeferredEntity third = commands.CreateEntity();
        commands.Playback();

        Assert.Equal(controlWorld.CreateEntity(), first.Resolve());
        Assert.Equal(controlWorld.CreateEntity(), second.Resolve());
        Assert.Equal(controlWorld.CreateEntity(), third.Resolve());
    }

    [Fact]
    public void ClearAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity()
    {
        var world = new World();
        var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.CreateEntity();

        commands.Playback();
        Entity live = pending.Resolve();
        commands.Clear();

        Assert.True(world.IsAlive(live));
        Assert.True(pending.TryResolve(out Entity resolved));
        Assert.Equal(live, resolved);
        Assert.Equal(live, pending.Resolve());
        Assert.Throws<InvalidOperationException>(() => commands.DestroyEntity(pending));
    }

    [Fact]
    public void DisposeAfterSuccessfulPlayback_PreservesPublishedHandleAndLiveEntity()
    {
        var world = new World();
        var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.CreateEntity();

        commands.Playback();
        Entity live = pending.Resolve();
        commands.Dispose();

        Assert.True(world.IsAlive(live));
        Assert.True(pending.TryResolve(out Entity resolved));
        Assert.Equal(live, resolved);
        Assert.Equal(live, pending.Resolve());
    }

    [Fact]
    public void WorldOwnedBufferRejectsDirectLifecycleAndRemainsFlushable()
    {
        var world = new World();
        Entity entity = world.CreateEntity();
        CommandBuffer commands = world.Commands();
        commands.Add(entity, new Position { X = 4, Y = 9 });

        InvalidOperationException playback = Assert.Throws<InvalidOperationException>(commands.Playback);
        InvalidOperationException clear = Assert.Throws<InvalidOperationException>(commands.Clear);
        InvalidOperationException dispose = Assert.Throws<InvalidOperationException>(commands.Dispose);

        Assert.Contains("World.Flush", playback.Message, StringComparison.Ordinal);
        Assert.Contains("World.Flush", clear.Message, StringComparison.Ordinal);
        Assert.Contains("World.Flush", dispose.Message, StringComparison.Ordinal);
        Assert.Equal(1, commands.CommandCount);

        world.Flush();
        Position value = world.Read<Position>(entity);
        Assert.Equal(4, value.X);
        Assert.Equal(9, value.Y);
    }

    [Fact]
    public void CustomBufferRejectsRecordingAfterPlaybackUntilClear()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity pending = commands.CreateEntity();
        commands.Playback();
        Entity entity = pending.Resolve();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            commands.Add(entity, new Position { X = 1, Y = 2 }));
        Assert.Contains("Clear", error.Message, StringComparison.Ordinal);

        commands.Clear();
        commands.Add(entity, new Position { X = 1, Y = 2 });
        commands.Playback();
        Assert.Equal(1, world.Read<Position>(entity).X);
    }

    [Fact]
    public void Clear_ThenRecordAndPlayback_Works()
    {
        var world = new World();
        var e = world.CreateEntity();
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
    public void CommandDataListClear_ClearsReferenceContainingSlots()
    {
        var list = new CommandDataList<NamedComponent>();
        list.Append(new NamedComponent { Name = "held", Id = 1 });

        list.Clear();

        var field = typeof(CommandDataList<NamedComponent>).GetField(
            "_data",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var data = (NamedComponent[])field!.GetValue(list)!;
        Assert.Null(data[0].Name);
    }

    [Fact]
    public void EmptyPlayback_NoThrow()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.Playback(); // 空回放不抛异常
    }

    [Fact]
    public void DeadEntity_Command_SkippedSilently()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.DestroyEntity(e);
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 });
        Assert.Throws<InvalidOperationException>(() => cb.Playback());
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
            entities[i] = cb.CreateEntity();
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
        Assert.Throws<ObjectDisposedException>(() => cb.CreateEntity());
    }

    [Fact]
    public void Dispose_InvalidatesPendingCreatedEntitiesWithoutAllocatorMutation()
    {
        var world = new World();
        DeferredEntity entity;

        using (var cb = new CommandBuffer(world))
        {
            entity = cb.CreateEntity();
        }

        Assert.False(entity.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => entity.Resolve());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void CommandCount_TracksCommands()
    {
        var world = new World();
        var e = world.CreateEntity();
        var cb = new CommandBuffer(world);
        Assert.Equal(0, cb.CommandCount);
        cb.Add(e, new Position { X = 1, Y = 2 });
        Assert.Equal(1, cb.CommandCount);
        cb.DestroyEntity(e);
        Assert.Equal(2, cb.CommandCount);
        cb.Clear();
        Assert.Equal(0, cb.CommandCount);
    }

    [Fact]
    public void CreateThenAddTag_Works()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.CreateEntity();
        cb.AddTag<PlayerTag>(e);
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
        DeferredEntity pending = commands.CreateEntity();
        commands.Add(pending, new Position { X = 1, Y = 2 });
        commands.Replace(pending, new Position { X = 3, Y = 4 });
        commands.AddTag<PlayerTag>(pending);
        commands.RemoveTag<PlayerTag>(pending);
        commands.Remove<Position>(pending);
        commands.DestroyEntity(pending);

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
        DeferredEntity pending = owner.CreateEntity();

        Assert.Throws<InvalidOperationException>(() =>
            other.Add(pending, new Position { X = 1, Y = 2 }));
    }

    [Fact]
    public void DeferredEntity_HasNoImplicitEntityConversion()
    {
        Assert.DoesNotContain(
            typeof(DeferredEntity).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static),
            method => method.Name == "op_Implicit" && method.ReturnType == typeof(Entity));
    }
}
