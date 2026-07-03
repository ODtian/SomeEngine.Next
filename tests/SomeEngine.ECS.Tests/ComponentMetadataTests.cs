using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// ——————————————————————————————————————————————————
// 测试用组件类型
// ——————————————————————————————————————————————————

public struct Position : SomeEngine.ECS.Components.IComponent
{
    public float X;
    public float Y;
}

public struct Velocity : SomeEngine.ECS.Components.IComponent
{
    public float X;
    public float Y;
}

public struct Health : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}

public struct PlayerTag : SomeEngine.ECS.Components.ITag { }

public struct EnemyTag : SomeEngine.ECS.Components.ITag { }

public struct Stunned : SomeEngine.ECS.Components.IEnableableComponent
{
    public float Duration;
}

public struct Damage : SomeEngine.ECS.Components.ISparseComponent
{
    public int Amount;
}

public struct Likes : SomeEngine.ECS.Components.IRelation
{
    public float Strength;
}

public struct Owns : SomeEngine.ECS.Components.IExclusiveRelation
{
    public int Slot;
}

public struct NamedComponent : SomeEngine.ECS.Components.IComponent
{
    public string Name;   // managed 引用字段
    public int Id;
}

public struct PureUnmanaged : SomeEngine.ECS.Components.IComponent
{
    public int A;
    public float B;
    public double C;
}

public struct NameIndex : SomeEngine.ECS.Components.IIndexedComponent<string>
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
    public void IdStartsOne()
    {
        // 任何组件的 Id 都应该 >= 1（0 保留为无效值）
        Assert.True(ComponentMetadata<Position>.Id >= 1);
    }

    [Fact]
    public void IdStaysStable()
    {
        int first = ComponentMetadata<Position>.Id;
        int second = ComponentMetadata<Position>.Id;
        int third = ComponentMetadata<Position>.Id;
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void IdsDiffer()
    {
        int posId = ComponentMetadata<Position>.Id;
        int velId = ComponentMetadata<Velocity>.Id;
        int healthId = ComponentMetadata<Health>.Id;
        int tagId = ComponentMetadata<PlayerTag>.Id;
        int sparseId = ComponentMetadata<Damage>.Id;

        var ids = new HashSet<int> { posId, velId, healthId, tagId, sparseId };
        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void TableStorage()
    {
        Assert.Equal(StoragePath.Table, ComponentMetadata<Position>.Storage);
        Assert.Equal(StoragePath.Table, ComponentMetadata<Velocity>.Storage);
        Assert.Equal(StoragePath.Table, ComponentMetadata<Health>.Storage);
    }

    [Fact]
    public void TagStorage()
    {
        Assert.Equal(StoragePath.Tag, ComponentMetadata<PlayerTag>.Storage);
        Assert.Equal(StoragePath.Tag, ComponentMetadata<EnemyTag>.Storage);
    }

    [Fact]
    public void EnableableStorage()
    {
        Assert.Equal(StoragePath.Table, ComponentMetadata<Stunned>.Storage);
        Assert.True(ComponentMetadata<Stunned>.IsEnableable);
    }

    [Fact]
    public void SparseStorage()
    {
        Assert.Equal(StoragePath.Sparse, ComponentMetadata<Damage>.Storage);
    }

    [Fact]
    public void RelationStorage()
    {
        Assert.Equal(StoragePath.Relation, ComponentMetadata<Likes>.Storage);
    }

    [Fact]
    public void ExclusiveStorage()
    {
        Assert.Equal(StoragePath.ExclusiveRelation, ComponentMetadata<Owns>.Storage);
    }

    [Fact]
    public void IndexedStorage()
    {
        Assert.Equal(StoragePath.Table, ComponentMetadata<NameIndex>.Storage);
        Assert.True(ComponentMetadata<NameIndex>.IsIndexed);
    }

    [Fact]
    public void PureRefsFalse()
    {
        Assert.False(ComponentMetadata<Position>.ContainsReferences);
        Assert.False(ComponentMetadata<PureUnmanaged>.ContainsReferences);
        Assert.False(ComponentMetadata<Health>.ContainsReferences);
    }

    [Fact]
    public void ManagedRefsTrue()
    {
        Assert.True(ComponentMetadata<NamedComponent>.ContainsReferences);
    }

    [Fact]
    public void KnownSizeCorrect()
    {
        // Position 有两个 float 字段 = 8 bytes
        Assert.Equal(8, ComponentMetadata<Position>.Size);
        // Health 有一个 int 字段 = 4 bytes
        Assert.Equal(4, ComponentMetadata<Health>.Size);
    }

    [Fact]
    public void TagSizePositive()
    {
        // Tag struct 即使无字段，Unsafe.SizeOf 也会返回 1（CLR 规则：空 struct size=1）
        Assert.True(ComponentMetadata<PlayerTag>.Size >= 1);
    }
}
