using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

/// <summary>A final field value ready for its generated typed write batch.</summary>
public readonly struct FieldWrite<T>(Entity target, Channel<T> channel, T value) where T : unmanaged
{
    public readonly Entity Target = target;
    public readonly Channel<T> Channel = channel;
    public readonly T Value = value;
}

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

    internal static void Flush(World world, FieldWrite<T>[] writes, ref int count, List<Channel<T>> channels)
    {
        if (count == 0)
            return;
        Channel<T> channel = writes[0].Channel;
        int sameChannel = 1;
        while (sameChannel < count && writes[sameChannel].Channel == channel)
            sameChannel++;
        if (sameChannel == count)
        {
            channel.WriteBatch(world, writes.AsSpan(0, count));
            count = 0;
            return;
        }

        // Keep channel appearance order and submission order within each channel.
        channels.Clear();
        ReadOnlySpan<Channel<T>> previousChannels = default;
        for (int index = 0; index < count; index++)
        {
            channel = writes[index].Channel;
            int previous = 0;
            while (previous < previousChannels.Length && previousChannels[previous] != channel)
                previous++;
            if (previous == previousChannels.Length)
            {
                channels.Add(channel);
                previousChannels = CollectionsMarshal.AsSpan(channels);
            }
        }
        foreach (Channel<T> field in channels)
            field.WriteBatch(world, writes.AsSpan(0, count));
        count = 0;
    }
}
