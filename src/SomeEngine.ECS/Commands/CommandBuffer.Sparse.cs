using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Commands;

public sealed partial class CommandBuffer
{
    /// <summary>延迟添加 sparse 组件。</summary>
    public void AddSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RecordSparseValue(new CommandEntity(entity), in value, CommandType.AddSparse);
    }

    /// <summary>为延迟创建的实体添加 sparse 组件。</summary>
    public void AddSparse<T>(DeferredEntity entity, in T value)
        where T : struct, ISparseComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RecordSparseValue(entity.AsCommandEntity(this), in value, CommandType.AddSparse);
    }

    /// <summary>延迟替换已有 sparse 组件。</summary>
    public void ReplaceSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RecordSparseValue(new CommandEntity(entity), in value, CommandType.ReplaceSparse);
    }

    /// <summary>替换延迟创建实体上的已有 sparse 组件。</summary>
    public void ReplaceSparse<T>(DeferredEntity entity, in T value)
        where T : struct, ISparseComponent
    {
        using RecordAccessScope access = EnterRecordAccess();
        RecordSparseValue(entity.AsCommandEntity(this), in value, CommandType.ReplaceSparse);
    }

    private void RecordSparseValue<T>(CommandEntity entity, in T value, CommandType type)
        where T : struct, ISparseComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref _dataLists, componentId + 1, 8);
        var list = (SparseCommandDataList<T>?)_dataLists[componentId];
        if (list is null)
        {
            list = new SparseCommandDataList<T>();
            _dataLists[componentId] = list;
        }

        _commands.Add(new CommandHeader
        {
            Type = type,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = list.Append(in value),
        });
    }
}
