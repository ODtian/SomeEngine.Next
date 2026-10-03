using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class ComponentBytesSmoke
{
    internal static void Check()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new ValueComponent { Number = 7, Opacity = 0.25f });
        ref byte field = ref world.WriteBytes(entity, ComponentId<ValueComponent>.Value, sizeof(int), sizeof(float));
        Unsafe.WriteUnaligned(ref field, 0.75f);
        Require(world.Read<ValueComponent>(entity).Opacity == 0.75f, "Component field byte write must update its value.");

        world.AddSparse(entity, new SparseValue { Number = 11 });
        Unsafe.WriteUnaligned(ref world.WriteBytes(entity, ComponentId<SparseValue>.Value), 23);
        Require(world.ReadSparse<SparseValue>(entity).Number == 23, "Sparse byte write must update its value.");

        QueryHandle query = world.Query(world.QueryDefinition().Write<ValueComponent>());
        world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                Span<byte> bytes = chunk.WriteColumn(ComponentId<ValueComponent>.Value, out int stride);
                Require(stride == 8 && bytes.Length == chunk.Count * stride, "Component byte columns must preserve row spacing.");
                Unsafe.WriteUnaligned(ref bytes[0], 31);
            }
        });
        Require(world.Read<ValueComponent>(entity).Number == 31, "Column byte write must update its value.");

        world.AddBuffer<Track<float>>(entity);
        world.AddBuffer<Track<Vector2>>(entity);
        world.ExecuteBufferWrite<Track<float>>(entity, static buffer =>
        {
            buffer.Add(new Track<float> { Value = 1 });
            buffer.Add(new Track<float> { Value = 2 });
            buffer.Add(new Track<float> { Value = 3 });
            buffer[1].Value = 42;
        });
        world.ExecuteBufferWrite<Track<Vector2>>(entity, static buffer =>
        {
            buffer.Add(new Track<Vector2> { Value = new Vector2(1, 2) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(3, 4) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(5, 6) });
            buffer[2].Value = new Vector2(7, 8);
        });
        world.ExecuteBufferRead<Track<float>>(entity, static buffer =>
            Require(buffer.Count == 3 && buffer.Read(1).Value == 42, "Generic float tracks must preserve written elements."));
        world.RemoveBuffer<Track<float>>(entity);
        world.ExecuteBufferRead<Track<Vector2>>(entity, static buffer =>
            Require(buffer.Count == 3 && buffer.Read(2).Value == new Vector2(7, 8), "Generic vector tracks must preserve written elements."));
        world.RemoveBuffer<Track<Vector2>>(entity);
        Require(!world.HasBuffer<Track<float>>(entity) && !world.HasBuffer<Track<Vector2>>(entity),
            "Removing generic tracks must release both buffer types.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private struct ValueComponent : IComponent
    {
        public int Number;
        public float Opacity;
    }

    private struct SparseValue : ISparseComponent
    {
        public int Number;
    }

    [BufferCapacity(2)]
    private struct Track<T> : IBufferElement where T : unmanaged
    {
        public T Value;
    }
}
