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
        cb.Playback();
        Assert.True(world.IsAlive(e));
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
        Assert.Equal(10f, world.Get<Position>(e).X);
        Assert.Equal(20f, world.Get<Position>(e).Y);
    }

    [Fact]
    public void Set_Playback_ValueUpdated()
    {
        var world = new World();
        var e = world.CreateEntity(new Position { X = 1, Y = 2 });
        var cb = new CommandBuffer(world);
        cb.Replace(e, new Position { X = 99, Y = 88 });
        cb.Playback();
        Assert.Equal(99f, world.Get<Position>(e).X);
        Assert.Equal(88f, world.Get<Position>(e).Y);
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
        Assert.True(world.IsAlive(e));
        Assert.True(world.Has<Position>(e));
        Assert.Equal(42f, world.Get<Position>(e).X);
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
        Assert.Equal(1f, world.Get<Position>(e).X);
        Assert.Equal(3f, world.Get<Velocity>(e).X);
    }

    [Fact]
    public void PlaybackFailureStops()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var first = cb.CreateEntity();
        cb.Add(first, new Position { X = 1, Y = 2 });
        cb.Add(first, new Position { X = 3, Y = 4 });
        var later = cb.CreateEntity();
        cb.Add(later, new Velocity { X = 5, Y = 6 });

        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(first));
        Assert.True(world.Has<Position>(first));
        Assert.Equal(1, world.Read<Position>(first).X);
        Assert.True(world.IsAlive(later));
        Assert.False(world.Has<Velocity>(later));

        cb.Clear();

        Assert.True(world.IsAlive(first));
        Assert.False(world.IsAlive(later));
        Assert.Equal(1, world.EntityCount);
    }

    // ════════════════════════════════════════════════
    // 预分配 ID
    // ════════════════════════════════════════════════

    [Fact]
    public void CreateEntity_ReturnsUsableId()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.CreateEntity();
        // 可以立即用于后续命令
        cb.Add(e, new Position { X = 100, Y = 200 });
        cb.Playback();
        Assert.True(world.IsAlive(e));
        Assert.Equal(100f, world.Get<Position>(e).X);
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
        Assert.True(world.IsAlive(e1));
        Assert.True(world.IsAlive(e2));
        Assert.True(world.IsAlive(e3));
    }

    [Fact]
    public void CreateEntity_PlaybackAlive_WithWorldIsAlive()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var e = cb.CreateEntity();
        // 在 Playback 之前 entity 尚未 finalized
        cb.Playback();
        Assert.True(world.IsAlive(e));
    }

    [Fact]
    public void CreateEntity_SecondPlayback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entity = cb.CreateEntity();

        cb.Playback();
        Assert.Throws<InvalidOperationException>(() => cb.Playback());

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void ReservedRejectsWorld()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        var entity = cb.CreateEntity();
        var target = world.CreateEntity();

        Assert.True(world.IsAlive(entity));
        Assert.Equal(2, world.EntityCount);
        Assert.False(world.Has<Position>(entity));
        Assert.False(world.HasBuffer<IntElement>(entity));
        Assert.False(world.HasSparse<Damage>(entity));
        Assert.False(world.HasShared<SceneId>(entity));
        Assert.False(world.HasRelation<Likes>(entity, target));
        Assert.False(world.HasRelation<Likes>(target, entity));
        Assert.Throws<InvalidOperationException>(() => world.Add(entity, new Position { X = 1, Y = 2 }));
        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddBuffer<IntElement>(entity));
        Assert.Throws<InvalidOperationException>(() => world.GetBuffer<IntElement>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddSparse(entity, new Damage { Amount = 1 }));
        Assert.Throws<InvalidOperationException>(() => world.GetSparse<Damage>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddShared(entity, new SceneId { Value = 1 }));
        Assert.Throws<InvalidOperationException>(() => world.GetShared<SceneId>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddRelation(entity, target, new Likes { Strength = 1 }));
        Assert.Throws<InvalidOperationException>(() => world.AddRelation(target, entity, new Likes { Strength = 1 }));
        Assert.Throws<InvalidOperationException>(() => world.DestroyEntity(entity));

        cb.Playback();

        Assert.True(world.IsAlive(entity));
        Assert.Equal(2, world.EntityCount);
        Assert.False(world.Has<Position>(entity));
    }

    // ════════════════════════════════════════════════
    // 迭代中使用
    // ════════════════════════════════════════════════

    [Fact]
    public void DuringIteration_CB_Add_DoesNotThrow()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.BeginIteration();
        var cb = new CommandBuffer(world);
        cb.Add(e, new Position { X = 1, Y = 2 }); // 不抛异常
        world.EndIteration();
        cb.Playback();
        Assert.True(world.Has<Position>(e));
    }

    [Fact]
    public void DuringIteration_World_Add_Throws()
    {
        var world = new World();
        var e = world.CreateEntity();
        world.BeginIteration();
        Assert.Throws<InvalidOperationException>(() =>
            world.Add(e, new Position { X = 1, Y = 2 }));
        world.EndIteration();
    }

    [Fact]
    public void DuringIteration_Playback_Throws()
    {
        var world = new World();
        var cb = new CommandBuffer(world);
        cb.CreateEntity();
        world.BeginIteration();
        Assert.Throws<InvalidOperationException>(() => cb.Playback());
        world.EndIteration();
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
    public void Clear_ReleasesPendingCreatedEntities()
    {
        var world = new World();
        var cb = new CommandBuffer(world);

        var entity = cb.CreateEntity();
        cb.Clear();

        Assert.False(world.IsAlive(entity));
        Assert.Equal(0, world.EntityCount);
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
        Assert.Equal(5f, world.Get<Velocity>(e).X);
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
        var entities = new Entity[1000];
        for (int i = 0; i < 1000; i++)
        {
            entities[i] = cb.CreateEntity();
            cb.Add(entities[i], new Position { X = i, Y = i * 2 });
        }
        cb.Playback();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.Equal((float)i, world.Get<Position>(entities[i]).X);
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
    public void Dispose_ReleasesPendingCreatedEntities()
    {
        var world = new World();
        Entity entity;

        using (var cb = new CommandBuffer(world))
        {
            entity = cb.CreateEntity();
        }

        Assert.False(world.IsAlive(entity));
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
        Assert.True(world.IsAlive(e));
        Assert.True(world.Has<PlayerTag>(e));
        Assert.True(world.Has<Position>(e));
    }
}
