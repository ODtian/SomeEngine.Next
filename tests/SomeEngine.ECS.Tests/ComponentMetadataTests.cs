using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// ——————————————————————————————————————————————————
// 测试用组件类型
// ——————————————————————————————————————————————————

public struct Position : SomeEngine.ECS.IAddedComponent
{
    public float X;
    public float Y;
}

public struct Velocity : SomeEngine.ECS.IAddedComponent
{
    public float X;
    public float Y;
}

public struct Health : SomeEngine.ECS.IAddedComponent
{
    public int Value;
}

public struct PlayerTag : SomeEngine.ECS.ITag { }

public struct EnemyTag : SomeEngine.ECS.ITag { }

public struct Stunned : SomeEngine.ECS.IEnableableComponent, IAddedComponent
{
    public float Duration;
}

public struct Damage : SomeEngine.ECS.Components.ISparseComponent, IAddedComponent
{
    public int Amount;
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
public struct Likes : SomeEngine.ECS.IAddedComponent
{
    public float Strength;
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueSource)]
public struct Owns : SomeEngine.ECS.IAddedComponent
{
    public int Slot;
}

public struct NamedComponent : SomeEngine.ECS.IAddedComponent
{
    public string Name;   // managed 引用字段
    public int Id;
}

public struct PureUnmanaged : SomeEngine.ECS.IAddedComponent
{
    public int A;
    public float B;
    public double C;
}

public struct NameIndex : SomeEngine.ECS.IIndexedComponent<string>, IAddedComponent
{
    public string Value;
    public string GetKey() => Value;
}

// ——————————————————————————————————————————————————
// 测试
// ——————————————————————————————————————————————————

public class ComponentMetadataTests
{
    [Fact]
    public void ComponentIdentityCanAddressStoredBytes()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 3 });
        float value = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<float>(in world.ReadBytes(entity, ComponentId<Position>.Value));
        Assert.Equal(3, value);
    }

    [Fact]
    public void IdStaysStable()
    {
        int first = ComponentId<Position>.Value;
        int second = ComponentId<Position>.Value;
        int third = ComponentId<Position>.Value;
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void IdsDiffer()
    {
        var ids = new HashSet<int>
        {
            ComponentId<Position>.Value, ComponentId<Velocity>.Value, ComponentId<Health>.Value,
            ComponentId<PlayerTag>.Value, ComponentId<Damage>.Value,
        };
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void TableStorage()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 }, new Velocity { Y = 2 }, new Health { Value = 3 });
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.Read<Velocity>(entity).Y);
        Assert.Equal(3, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void TagStorage()
    {
        using var world = new World();
        var entity = world.Create(new PlayerTag(), new EnemyTag());
        world.Remove<PlayerTag>(entity);
        Assert.False(world.Has<PlayerTag>(entity));
        Assert.True(world.Has<EnemyTag>(entity));
    }

    [Fact]
    public void EnableableStorage()
    {
        using var world = new World();
        var entity = world.Create(new Stunned { Duration = 2 });
        world.Disable<Stunned>(entity);
        Assert.True(world.Has<Stunned>(entity));
        Assert.Equal(0, world.Query<Stunned>().Count);
        Assert.Equal(2, world.Read<Stunned>(entity).Duration);
    }

    [Fact]
    public void SparseStorage()
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new Damage { Amount = 3 });
        Assert.True(world.HasSparse<Damage>(entity));
        Assert.False(world.Has<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void RelationPayloadUsesOrdinaryTableStorage()
    {
        using var world = new World();
        var entity = world.Create(new Likes { Strength = 0.5f }, new Owns { Slot = 7 });
        Assert.Equal(0.5f, world.Read<Likes>(entity).Strength);
        Assert.Equal(7, world.Read<Owns>(entity).Slot);
        Assert.False(world.Has<RelationEndpoints<Likes>>(entity));
        Assert.Equal(RelationCardinality.Parallel, RelationSchema.For<Likes>().Cardinality);
        Assert.Equal(RelationCardinality.UniqueSource, RelationSchema.For<Owns>().Cardinality);
    }

    [Fact]
    public void IndexedStorage()
    {
        using var world = new World();
        ValueIndex<NameIndex, string> index = world.Index<NameIndex, string>();
        var entity = world.Create(new NameIndex { Value = "first" });
        world.Set(entity, new NameIndex { Value = "second" });
        using IndexMembers previous = index.Find("first");
        using IndexMembers current = index.Find("second");
        Assert.Equal(0, previous.Count);
        Assert.Equal([entity], current.Entities.ToArray());
    }

    [Fact]
    public void UnmanagedComponentBytesExposeTheStoredValue()
    {
        using var world = new World();
        var entity = world.Create(new PureUnmanaged { A = 7, B = 3, C = 2 });
        PureUnmanaged value = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<PureUnmanaged>(
            in world.ReadBytes(entity, ComponentId<PureUnmanaged>.Value));
        Assert.Equal(7, value.A);
        Assert.Equal(3, value.B);
        Assert.Equal(2, value.C);
    }

    [Fact]
    public void ManagedComponentValuesKeepTheirObjectReferences()
    {
        using var world = new World();
        string text = new(['I', 'r', 'i', 's']);
        var entity = world.Create(new NamedComponent { Name = text, Id = 3 });
        Assert.Same(text, world.Read<NamedComponent>(entity).Name);
        Assert.Equal(3, world.Read<NamedComponent>(entity).Id);
    }

    [Fact]
    public void ComponentBytesPreserveAdjacentFields()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 }, new Health { Value = 3 });
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref world.WriteBytes(entity,
            ComponentId<Position>.Value, sizeof(float), sizeof(float)), 7f);
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(7, world.Read<Position>(entity).Y);
        Assert.Equal(3, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void TagsParticipateInTypedQueries()
    {
        using var world = new World();
        var player = world.Create(new PlayerTag());
        world.Create(new EnemyTag());
        Query players = world.Query<PlayerTag>();
        using QueryScope scope = players.OpenRead();
        Assert.True(scope.Contains(player));
        Assert.Equal(1, players.Count);
    }
}
