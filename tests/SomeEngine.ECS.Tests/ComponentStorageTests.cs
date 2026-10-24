namespace SomeEngine.ECS.Tests;

public class ComponentStorageTests
{
    [Fact]
    public void CopyEntityPreservesManagedComponentValues()
    {
        using var world = new World();
        var source = world.CreateEntity(new NamedComponent { Name = "hello", Id = 42 });
        var copy = world.CloneEntity(source);
        Assert.Equal("hello", world.Read<NamedComponent>(copy).Name);
        Assert.Equal(42, world.Read<NamedComponent>(copy).Id);
    }

    [Fact]
    public void CopyEntityPreservesNullManagedValues()
    {
        using var world = new World();
        var source = world.CreateEntity(new NamedComponent { Name = null!, Id = 7 });
        var copy = world.CloneEntity(source);
        Assert.Null(world.Read<NamedComponent>(copy).Name);
        Assert.Equal(7, world.Read<NamedComponent>(copy).Id);
    }

    [Fact]
    public void DestroyEntityPreservesOtherManagedComponents()
    {
        using var world = new World();
        var first = world.CreateEntity(new NamedComponent { Name = "A", Id = 1 });
        var second = world.CreateEntity(new NamedComponent { Name = "B", Id = 2 });
        world.DestroyEntity(first);
        Assert.False(world.IsAlive(first));
        Assert.Equal("B", world.Read<NamedComponent>(second).Name);
        Assert.Equal(2, world.Read<NamedComponent>(second).Id);
    }
}
