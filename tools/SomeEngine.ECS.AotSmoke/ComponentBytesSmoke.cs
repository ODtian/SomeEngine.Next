using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class ComponentBytesSmoke
{
    internal static void Check()
    {
        using var world = new World();
        Entity entity = world.Create(new ValueComponent { Number = 7, Opacity = 0.25f });
        ulong before = world.ComponentWriteVersion<ValueComponent>(entity);
        Require(Unsafe.ReadUnaligned<float>(in world.ReadBytes(entity, ComponentId<ValueComponent>.Value, sizeof(int), sizeof(float))) == 0.25f,
            "Component byte reads return the selected field.");
        Require(before == world.ComponentWriteVersion<ValueComponent>(entity), "Component byte reads preserve the write version.");
        ref byte field = ref world.WriteBytes(entity, ComponentId<ValueComponent>.Value, sizeof(int), sizeof(float));
        Unsafe.WriteUnaligned(ref field, 0.75f);
        Require(world.Read<ValueComponent>(entity).Opacity == 0.75f, "Component byte writes update the selected field.");
        Require(before != world.ComponentWriteVersion<ValueComponent>(entity), "Component byte writes advance the write version.");

        world.Set(entity, new SparseValue { Number = 11 });
        before = world.ComponentWriteVersion<SparseValue>(entity);
        Require(Unsafe.ReadUnaligned<int>(in world.ReadBytes(entity, ComponentId<SparseValue>.Value)) == 11,
            "Sparse byte reads return the component value.");
        Require(before == world.ComponentWriteVersion<SparseValue>(entity), "Sparse byte reads preserve the write version.");
        Unsafe.WriteUnaligned(ref world.WriteBytes(entity, ComponentId<SparseValue>.Value), 23);
        Require(world.Read<SparseValue>(entity).Number == 23, "Sparse byte writes update the value.");
        ulong first = world.ComponentWriteVersion<SparseValue>(entity);
        world.WriteBytes(entity, ComponentId<SparseValue>.Value);
        Require(before != first && first != world.ComponentWriteVersion<SparseValue>(entity),
            "Each sparse byte write advances the write version.");

        Query query = world.Query(new QueryDescription().With<ValueComponent>());
        using (QueryScope rows = query.Open())
            foreach (QueryChunk chunk in rows)
            {
                Span<byte> bytes = MemoryMarshal.AsBytes(chunk.Write<ValueComponent>());
                int stride = Unsafe.SizeOf<ValueComponent>();
                Require(stride == 8 && bytes.Length == chunk.Count * stride, "Component byte columns preserve row spacing.");
                Unsafe.WriteUnaligned(ref bytes[0], 31);
            }
        Require(world.Read<ValueComponent>(entity).Number == 31, "Column byte writes update the value.");

        world.AddBuffer<Track<float>>(entity);
        world.AddBuffer<Track<Vector2>>(entity);
        using (DynamicBuffer<Track<float>> buffer = world.GetBuffer<Track<float>>(entity))
        {
            buffer.Add(new Track<float> { Value = 1 });
            buffer.Add(new Track<float> { Value = 2 });
            buffer.Add(new Track<float> { Value = 3 });
            buffer[1].Value = 42;
        }
        using (DynamicBuffer<Track<Vector2>> buffer = world.GetBuffer<Track<Vector2>>(entity))
        {
            buffer.Add(new Track<Vector2> { Value = new Vector2(1, 2) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(3, 4) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(5, 6) });
            buffer[2].Value = new Vector2(7, 8);
        }
        using (ReadOnlyBuffer<Track<float>> buffer = world.ReadBuffer<Track<float>>(entity))
            Require(buffer.Count == 3 && buffer[1].Value == 42, "Generic float tracks preserve written elements.");
        world.RemoveBuffer<Track<float>>(entity);
        using (ReadOnlyBuffer<Track<Vector2>> buffer = world.ReadBuffer<Track<Vector2>>(entity))
            Require(buffer.Count == 3 && buffer[2].Value == new Vector2(7, 8), "Generic vector tracks preserve written elements.");
        world.RemoveBuffer<Track<Vector2>>(entity);
        Require(!world.Has<Buffer<Track<float>>>(entity) && !world.Has<Buffer<Track<Vector2>>>(entity),
            "Removing generic tracks releases both buffer types.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private struct ValueComponent : IComponent, ITrackedComponent
    {
        public int Number;
        public float Opacity;
    }

    private struct SparseValue : ISparseComponent, ITrackedComponent
    {
        public int Number;
    }

    private struct Track<T> : IBufferElement where T : unmanaged
    {
        public T Value;
    }
}
