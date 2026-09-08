using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedRootCloneTests
{
    [Fact]
    public void BufferCloneRemainsIndependentAcrossGrowthClearAndComponentChanges()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        world.AddBuffer<Element>(source);
        world.ExecuteBufferWrite<Element>(source, static buffer =>
        {
            for (int i = 0; i < 32; i++) buffer.Add(new Element(i));
        });
        Entity copy = world.CloneEntity(source);
        world.ExecuteBufferWrite<Element>(copy, static buffer =>
        {
            buffer.Clear();
            for (int i = 0; i < 40; i++) buffer.Add(new Element(100 + i));
        });
        world.AddTag<Marker>(copy);
        world.Remove<Marker>(copy);
        Assert.Equal(Enumerable.Range(0, 32), Read(world, source).Select(x => x.Value));
        Assert.Equal(Enumerable.Range(100, 40), Read(world, copy).Select(x => x.Value));
        world.DestroyEntity(copy);
        Assert.Equal(Enumerable.Range(0, 32), Read(world, source).Select(x => x.Value));
    }
    private static Element[] Read(World world, Entity entity)
    {
        Element[] values = [];
        world.ExecuteBufferRead<Element, Element[]>(entity, ref values,
            static (BufferView<Element> buffer, ref Element[] result) => result = buffer.AsSpan().ToArray());
        return values;
    }
    [BufferCapacity(2)]
    private readonly record struct Element(int Value) : IBufferElement;
    private readonly struct Marker : ITag;
}
