using System.Runtime.CompilerServices;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Commands;

/// <summary>Typed payload bucket used by deferred command playback.</summary>
internal interface ICommandPayloadList
{
    void Clear();
}

internal interface IComponentCommandList : ICommandPayloadList
{
    void PlaybackAdd(World world, Entity entity, int dataIndex);
    void PlaybackReplace(World world, Entity entity, int dataIndex);
    void PlaybackRemove(World world, Entity entity);
}

internal interface IBufferCommandList : ICommandPayloadList
{
    void PlaybackAdd(World world, Entity entity, int dataIndex);
    void PlaybackReplace(World world, Entity entity, int dataIndex);
    void PlaybackRemove(World world, Entity entity);
}

/// <summary>Component values are stored by closed generic type without boxing.</summary>
internal sealed class CommandDataList<T> : IComponentCommandList
    where T : struct, IComponent
{
    private T[] _data = new T[8];
    private int _count;

    public int Append(in T value)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _data, _count + 1, 8);
        _data[_count] = value;
        return _count++;
    }

    public void PlaybackAdd(World world, Entity entity, int dataIndex) =>
        world.Add(entity, in _data[dataIndex]);

    public void PlaybackReplace(World world, Entity entity, int dataIndex) =>
        world.Replace(entity, in _data[dataIndex]);

    public void PlaybackRemove(World world, Entity entity) =>
        world.Remove<T>(entity);

    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_data, 0, _count);

        _count = 0;
    }
}

/// <summary>
/// Command-owned packed buffer payloads. Recording copies each logical buffer into this flat
/// typed arena so caller spans may expire without allocating one array per command.
/// </summary>
internal sealed class BufferCommandDataList<T> : IBufferCommandList
    where T : struct, IBufferElement
{
    private T[] _items = Array.Empty<T>();
    private int[] _starts = new int[8];
    private int[] _lengths = new int[8];
    private int _itemCount;
    private int _commandCount;

    internal int Append(scoped ReadOnlySpan<T> values)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _starts, _commandCount + 1, 8);
        ArrayGrowthExtensions.EnsureCapacity(ref _lengths, _commandCount + 1, 8);
        int end = checked(_itemCount + values.Length);
        ArrayGrowthExtensions.EnsureCapacity(ref _items, end, 8);
        values.CopyTo(_items.AsSpan(_itemCount, values.Length));
        int index = _commandCount++;
        _starts[index] = _itemCount;
        _lengths[index] = values.Length;
        _itemCount = end;
        return index;
    }

    public void PlaybackAdd(World world, Entity entity, int dataIndex)
    {
        Range(dataIndex, out int start, out int length);
        world.Buffers.Add<T>(entity, _items.AsMemory(start, length));
    }

    public void PlaybackReplace(World world, Entity entity, int dataIndex)
    {
        Range(dataIndex, out int start, out int length);
        world.Buffers.BorrowWrite<T>(entity).ReplaceWith(_items.AsSpan(start, length));
    }

    public void PlaybackRemove(World world, Entity entity) =>
        world.Buffers.Remove<T>(entity);

    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _items.AsSpan(0, _itemCount).Clear();
        _starts.AsSpan(0, _commandCount).Clear();
        _lengths.AsSpan(0, _commandCount).Clear();
        _itemCount = 0;
        _commandCount = 0;
    }

    private void Range(int dataIndex, out int start, out int length)
    {
        if ((uint)dataIndex >= (uint)_commandCount)
            throw new InvalidOperationException("Buffer command payload index is invalid.");
        start = _starts[dataIndex];
        length = _lengths[dataIndex];
    }
}
