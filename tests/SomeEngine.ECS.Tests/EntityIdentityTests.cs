using SomeEngine.ECS.Entities;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class EntityIdentityTests
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
        using var world = new World();
        var a = world.CreateEntity();
        var b = a;
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
    }

    [Fact]
    public void Entity_NotEquals_DifferentIndex()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();
        Assert.True(a != b);
        Assert.False(a == b);
    }

    [Fact]
    public void Entity_NotEquals_DifferentGeneration()
    {
        using var world = new World();
        var a = world.CreateEntity();
        world.DestroyEntity(a);
        var b = world.CreateEntity();
        Assert.True(a != b);
    }

    [Fact]
    public void Entity_GetHashCode_ConsistentForEqualValues()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = a;
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }


    [Fact]
    public void Entity_CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<Entity, string>();
        using var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();
        dict[e1] = "first";
        dict[e2] = "second";
        Assert.Equal("first", dict[e1]);
        Assert.Equal("second", dict[e2]);
    }

}
