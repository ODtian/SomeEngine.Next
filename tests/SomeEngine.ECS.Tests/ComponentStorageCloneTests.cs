using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentStorageCloneTests
{
    [Fact]
    public void CopyCanChangeSparseSharedAndIndexedValuesWithoutChangingSource()
    {
        var world = new World();
        Entity source = world.Create(new Number(7));
        world.SetSparse(source, new SparseValue(8));
        world.SetShared(source, new SharedValue(9));
        Entity copy = world.Clone(source);
        world.Set(copy, new Number(17));
        world.SetSparse(copy, new SparseValue(18));
        world.SetShared(copy, new SharedValue(19));
        Assert.Equal(7, world.Read<Number>(source).Value);
        Assert.Equal(8, world.ReadSparse<SparseValue>(source).Value);
        Assert.Equal(9, world.ReadShared<SharedValue>(source).Value);
        Assert.Equal(17, world.Read<Number>(copy).Value);
        Assert.Equal(18, world.ReadSparse<SparseValue>(copy).Value);
        Assert.Equal(19, world.ReadShared<SharedValue>(copy).Value);
        Assert.Equal(new[] { source }, ReadIndexedEntities<Number, int>(world, 7).ToArray());
        Assert.Equal(new[] { copy }, ReadIndexedEntities<Number, int>(world, 17).ToArray());
    }
    private readonly record struct Number(int Value) : IIndexedComponent<int>
    {
        public int GetKey() => Value;
    }
    private readonly record struct SparseValue(int Value) : ISparseComponent;
    private readonly record struct SharedValue(int Value) : ISharedComponent;
}
