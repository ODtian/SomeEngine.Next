using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class BufferCloneTests
{
    [Fact]
    public void BufferCloneRemainsIndependentAcrossGrowthClearAndComponentChanges()
    {
        var world = new World();
        Entity source = world.Create();
        world.AddBuffer<Element>(source);
        {
            var buffer = world.GetBuffer<Element>(source);
            try
            {

            for (int i = 0; i < 32; i++) buffer.Add(new Element(i));

            }
            finally { buffer.Dispose(); }
        };
        Entity copy = world.Clone(source);
        {
            var buffer = world.GetBuffer<Element>(copy);
            try
            {

            buffer.Clear();
            for (int i = 0; i < 40; i++) buffer.Add(new Element(100 + i));

            }
            finally { buffer.Dispose(); }
        };
        world.Add(copy, default(Marker));
        world.Remove<Marker>(copy);
        Assert.Equal(Enumerable.Range(0, 32), Read(world, source).Select(x => x.Value));
        Assert.Equal(Enumerable.Range(100, 40), Read(world, copy).Select(x => x.Value));
        world.Destroy(copy);
        Assert.Equal(Enumerable.Range(0, 32), Read(world, source).Select(x => x.Value));
    }
    private static Element[] Read(World world, Entity entity)
    {
        Element[] values = [];
        {
            using ReadOnlyBuffer<Element> buffer = world.ReadBuffer<Element>(entity);
values = buffer.AsSpan().ToArray();
        };
        return values;
    }

    private readonly record struct Element(int Value) : IBufferElement;
    private readonly struct Marker : ITag;
}
