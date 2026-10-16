using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>A final field value ready for its generated typed write batch.</summary>
public readonly record struct FieldWrite<T>(Entity Target, Channel<T> Channel, T Value) where T : unmanaged;

internal static class FieldWrites<T> where T : unmanaged
{
    internal static void Add(ref FieldWrite<T>[] writes, ref int count, Entity target, Channel<T> channel, in T value)
    {
        if (count == writes.Length)
        {
            FieldWrite<T>[] previous = writes;
            writes = ArrayPool<FieldWrite<T>>.Shared.Rent(previous.Length * 2);
            previous.AsSpan(0, count).CopyTo(writes);
            ArrayPool<FieldWrite<T>>.Shared.Return(previous);
        }
        writes[count++] = new(target, channel, value);
    }

    internal static void Flush(World world, FieldWrite<T>[] writes, ref int count)
    {
        for (int index = 0; index < count; index++)
        {
            bool written = false;
            for (int previous = 0; previous < index; previous++)
                if (writes[previous].Channel == writes[index].Channel) { written = true; break; }
            if (!written) writes[index].Channel.WriteBatch(world, writes.AsSpan(0, count));
        }
        count = 0;
    }
}
