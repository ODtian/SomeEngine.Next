using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Commands;

public sealed partial class CommandBuffer
{
    /// <summary>
    /// Records structural creation of a typed dynamic buffer and its complete initial contents.
    /// Values are copied into command-owned typed storage before this method returns.
    /// </summary>
    public void AddBuffer<T>(Entity entity, scoped ReadOnlySpan<T> values = default)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddBufferUnderGate(new CommandEntity(entity), values);
    }

    public void AddBuffer<T>(DeferredEntity entity, scoped ReadOnlySpan<T> values = default)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        AddBufferUnderGate(entity.AsCommandEntity(this), values);
    }

    private void AddBufferUnderGate<T>(CommandEntity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement
    {
        int componentId = BufferComponents.Header<T>();
        int dataIndex = BufferDataList<T>(componentId).Append(values);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.AddBuffer,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    /// <summary>Records complete replacement of an existing typed dynamic buffer.</summary>
    public void ReplaceBuffer<T>(Entity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        ReplaceBufferUnderGate(new CommandEntity(entity), values);
    }

    public void ReplaceBuffer<T>(DeferredEntity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        ReplaceBufferUnderGate(entity.AsCommandEntity(this), values);
    }

    private void ReplaceBufferUnderGate<T>(CommandEntity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement
    {
        int componentId = BufferComponents.Header<T>();
        int dataIndex = BufferDataList<T>(componentId).Append(values);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.ReplaceBuffer,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    public void RemoveBuffer<T>(Entity entity)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveBufferUnderGate<T>(new CommandEntity(entity));
    }

    public void RemoveBuffer<T>(DeferredEntity entity)
        where T : struct, IBufferElement
    {
        using RecordAccessScope access = EnterRecordAccess();
        RemoveBufferUnderGate<T>(entity.AsCommandEntity(this));
    }

    private void RemoveBufferUnderGate<T>(CommandEntity entity)
        where T : struct, IBufferElement
    {
        int componentId = BufferComponents.Header<T>();
        _ = BufferDataList<T>(componentId);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.RemoveBuffer,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = -1,
        });
    }

    private BufferCommandDataList<T> BufferDataList<T>(int componentId)
        where T : struct, IBufferElement
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _dataLists, componentId + 1, 8);

        ICommandPayloadList? existing = _dataLists[componentId];
        if (existing is not null)
            return (BufferCommandDataList<T>)existing;

        var list = new BufferCommandDataList<T>();
        _dataLists[componentId] = list;
        return list;
    }

    private IBufferCommandList BufferDataList(int componentId) =>
        (IBufferCommandList)_dataLists[componentId]!;
}
