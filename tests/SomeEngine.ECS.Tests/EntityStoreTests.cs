using SomeEngine.ECS.Entities;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class EntityStoreTests
{
    // ——————————————————————————————————————————————————
    // Entity 基本语义
    // ——————————————————————————————————————————————————

    [Fact]
    public void Entity_Null_IsDefault()
    {
        Assert.Equal(default(Entity), Entity.Null);
        Assert.Equal(0, Entity.Null.Index);
        Assert.Equal(0, Entity.Null.Generation);
    }

    [Fact]
    public void Entity_Equals_SameValues()
    {
        var a = new Entity(5, 3);
        var b = new Entity(5, 3);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
    }

    [Fact]
    public void Entity_NotEquals_DifferentIndex()
    {
        var a = new Entity(1, 0);
        var b = new Entity(2, 0);
        Assert.True(a != b);
        Assert.False(a == b);
    }

    [Fact]
    public void Entity_NotEquals_DifferentGeneration()
    {
        var a = new Entity(1, 0);
        var b = new Entity(1, 1);
        Assert.True(a != b);
    }

    [Fact]
    public void Entity_GetHashCode_ConsistentForEqualValues()
    {
        var a = new Entity(42, 7);
        var b = new Entity(42, 7);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }


    [Fact]
    public void Entity_CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<Entity, string>();
        var e1 = new Entity(1, 0);
        var e2 = new Entity(2, 0);
        dict[e1] = "first";
        dict[e2] = "second";
        Assert.Equal("first", dict[e1]);
        Assert.Equal("second", dict[e2]);
    }

    // ——————————————————————————————————————————————————
    // EntityStore.Allocate
    // ——————————————————————————————————————————————————




    // ——————————————————————————————————————————————————
    // EntityStore.IsAlive
    // ——————————————————————————————————————————————————





    // ——————————————————————————————————————————————————
    // EntityStore.Free
    // ——————————————————————————————————————————————————



    // ——————————————————————————————————————————————————
    // EntityStore.GetRecord
    // ——————————————————————————————————————————————————






    // ——————————————————————————————————————————————————
    // 大批量 Allocate → Free → Allocate 复用
    // ——————————————————————————————————————————————————


}
