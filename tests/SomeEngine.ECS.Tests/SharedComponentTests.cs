using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// 测试用 shared component 类型
public struct SceneId : SomeEngine.ECS.ISharedComponent, IEquatable<SceneId>, IAddedComponent
{
    public int Value;

    public bool Equals(SceneId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SceneId s && Equals(s);
    public override int GetHashCode() => Value;
}

public struct RenderGroup : SomeEngine.ECS.ISharedComponent, IEquatable<RenderGroup>, IAddedComponent
{
    public int GroupId;
    public string Material;

    public bool Equals(RenderGroup other) => GroupId == other.GroupId && Material == other.Material;
    public override bool Equals(object? obj) => obj is RenderGroup r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(GroupId, Material);
}

public class SharedComponentTests
{


    [Fact]
    public void SetShared_AddsToArchetype()
    {
        var world = new World();
        var entity = world.Create();

        world.SetShared(entity, new SceneId { Value = 1 });

        Assert.True(world.Has<SceneId>(entity));
    }

    [Fact]
    public void AddShared_ExistingSharedComponent_Throws()
    {
        var world = new World();
        var entity = world.Create();

        world.SetShared(entity, new SceneId { Value = 1 });

        Assert.Throws<InvalidOperationException>(() => world.Add(entity, new SceneId { Value = 2 }));
        Assert.Equal(1, world.ReadShared<SceneId>(entity).Value);
    }

    [Fact]
    public void GetShared_ReturnsCorrectValue()
    {
        var world = new World();
        var entity = world.Create();

        world.SetShared(entity, new SceneId { Value = 42 });

        var scene = world.ReadShared<SceneId>(entity);
        Assert.Equal(42, scene.Value);
    }

    [Fact]
    public void SetShared_UpdatesValue()
    {
        var world = new World();
        var entity = world.Create();

        world.SetShared(entity, new SceneId { Value = 1 });
        world.SetShared(entity, new SceneId { Value = 2 });

        Assert.Equal(2, world.ReadShared<SceneId>(entity).Value);
    }

    [Fact]
    public void SharedValuesPreserveOrdinaryComponentValues()
    {
        var world = new World();
        var entity = world.Create<Position>(new Position { X = 1, Y = 2 });

        world.SetShared(entity, new SceneId { Value = 10 });

        // Position 应该仍然可读
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.Read<Position>(entity).Y);
    }

    [Fact]
    public void SharedComponent_PreservesComponentsOnMigration()
    {
        var world = new World();
        var entity = world.Create<Position>(new Position { X = 5, Y = 10 });

        world.SetShared(entity, new SceneId { Value = 1 });
        world.Add(entity, new Health { Value = 100 });

        Assert.Equal(5, world.Read<Position>(entity).X);
        Assert.Equal(100, world.Read<Health>(entity).Value);
        Assert.Equal(1, world.ReadShared<SceneId>(entity).Value);
    }

    [Fact]
    public void SharedComponent_MultipleEntities_SameValue()
    {
        var world = new World();
        var e1 = world.Create();
        var e2 = world.Create();

        world.SetShared(e1, new SceneId { Value = 1 });
        world.SetShared(e2, new SceneId { Value = 1 });

        Assert.Equal(1, world.ReadShared<SceneId>(e1).Value);
        Assert.Equal(1, world.ReadShared<SceneId>(e2).Value);
    }

    [Fact]
    public void SharedComponent_MultipleEntities_DifferentValues()
    {
        var world = new World();
        var e1 = world.Create();
        var e2 = world.Create();

        world.SetShared(e1, new SceneId { Value = 1 });
        world.SetShared(e2, new SceneId { Value = 2 });

        Assert.Equal(1, world.ReadShared<SceneId>(e1).Value);
        Assert.Equal(2, world.ReadShared<SceneId>(e2).Value);
    }

    [Fact]
    public void GetShared_ThrowsIfNotPresent()
    {
        var world = new World();
        var entity = world.Create();

        Assert.Throws<ArgumentException>(() =>
            world.ReadShared<SceneId>(entity));
    }

    [Fact]
    public void SharedComponent_DestroyEntity_CleansUpIndex()
    {
        var world = new World();
        var entity = world.Create();
        world.SetShared(entity, new SceneId { Value = 42 });

        world.Destroy(entity);

        // 创建新 entity（可能复用同一 index）
        var newEntity = world.Create();

        // 新 entity 不应携带旧的 shared value
        Assert.False(world.Has<SceneId>(newEntity));
    }

    [Fact]
    public void HasShared_DeadEntity_ReturnsFalse()
    {
        var world = new World();
        var entity = world.Create();
        world.SetShared(entity, new SceneId { Value = 42 });

        world.Destroy(entity);

        Assert.False(world.HasComponentData(entity, ComponentId<SceneId>.Value));
    }

    [Fact]
    public void SharedComponent_DestroyEntity_MultipleShared_CleansUpAll()
    {
        var world = new World();
        var entity = world.Create();
        world.SetShared(entity, new SceneId { Value = 1 });
        world.SetShared(entity, new RenderGroup { GroupId = 5, Material = "metal" });

        world.Destroy(entity);

        var newEntity = world.Create();
        Assert.False(world.Has<SceneId>(newEntity));
        Assert.False(world.Has<RenderGroup>(newEntity));
    }




    [Fact]
    public void QueryWithSharedFilter_OnlyMatchingChunksIterated()
    {
        using var world = new World();
        Entity first = world.Create(new Position { X = 1 }, new SceneId { Value = 10 });
        world.Create(new Position { X = 2 }, new SceneId { Value = 20 });
        Entity third = world.Create(new Position { X = 3 }, new SceneId { Value = 10 });
        Query query = world.Query(new QueryDescription().WithStored<Position>().WithShared(world.InternShared(new SceneId { Value = 10 })));
        var results = new List<float>();
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (ref readonly Position position in chunk.Read<Position>()) results.Add(position.X);
        Assert.Equal(2, results.Count);
        Assert.Contains(1f, results);
        Assert.Contains(3f, results);
        Assert.DoesNotContain(2f, results);
    }

    [Fact]
    public void QueryWithSharedFilter_NoMatchingChunks_NoIteration()
    {
        using var world = new World();
        world.Create(new Position { X = 1 }, new SceneId { Value = 42 });
        Query query = world.Query(new QueryDescription().WithStored<Position>().WithShared(world.InternShared(new SceneId { Value = 999 })));
        Assert.Equal(0, query.Count);
    }

    [Fact]
    public void QueryRowsWithSharedValueReturnsMatchingEntities()
    {
        using var world = new World();
        Entity first = world.Create(new Position { X = 1 }, new SceneId { Value = 10 });
        world.Create(new Position { X = 2 }, new SceneId { Value = 20 });
        Query query = world.Query(new QueryDescription().WithStored<Position>().WithShared(world.InternShared(new SceneId { Value = 10 })));
        Assert.Equal(1, query.Count);
        Assert.Equal(1, query.Count);
        using QueryScope scope = query.OpenRead();
        Assert.True(scope.Contains(first));
    }
}
