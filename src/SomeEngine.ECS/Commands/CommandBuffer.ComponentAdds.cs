using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Commands;

/// <summary>Records adjacent component additions to one entity as one bundle transition.</summary>
public readonly ref struct ComponentAddScope
{
    private readonly CommandBuffer _commands;
    private readonly CommandEntity _entity;
    private readonly int _start;
    internal ComponentAddScope(CommandBuffer commands, CommandEntity entity, int start)
    { _commands = commands; _entity = entity; _start = start; }
    public void Dispose() => _commands.CombineComponentAdds(_entity, _start);
}

public sealed partial class CommandBuffer
{
    private readonly record struct ComponentAddValue(CommandType Type, int ComponentId, int DataIndex);
    private readonly List<ComponentAddValue> _componentAdds = [];
    private readonly List<int> _addedComponentIds = [];

    public ComponentAddScope AddComponents(Entity entity)
    {
        using RecordAccessScope access = EnterRecordAccess();
        return new(this, new CommandEntity(entity), _commands.Count);
    }
    public ComponentAddScope AddComponents(DeferredEntity entity)
    {
        using RecordAccessScope access = EnterRecordAccess();
        return new(this, entity.AsCommandEntity(this), _commands.Count);
    }
    internal void CombineComponentAdds(CommandEntity entity, int start)
    {
        using RecordAccessScope access = EnterRecordAccess();
        int first = start;
        while (first < _commands.Count)
        {
            int end = first;
            while (end < _commands.Count && _commands[end].Entity.Matches(entity)
                && _commands[end].Type is CommandType.AddComponent or CommandType.AddBuffer) end++;
            int count = end - first;
            if (count > 1)
            {
                int data = _componentAdds.Count;
                for (int index = first; index < end; index++)
                {
                    CommandHeader command = _commands[index];
                    _componentAdds.Add(new(command.Type, command.ComponentId, command.DataIndex));
                }
                if (first > 0 && _commands[first - 1].Type == CommandType.CreateEntity
                    && _commands[first - 1].Entity.Matches(entity))
                {
                    _commands[first - 1] = new() { Type = CommandType.CreateComponents, Entity = entity, DataIndex = data, ComponentId = count };
                    _commands.RemoveRange(first, count);
                    continue;
                }
                _commands[first] = new() { Type = CommandType.AddComponents, Entity = entity, DataIndex = data, ComponentId = count };
                _commands.RemoveRange(first + 1, count - 1);
                first++;
            }
            else first = Math.Max(first + 1, end);
        }
    }

    private bool SameComponentAdds(CommandHeader first, CommandHeader second)
    {
        if (first.ComponentId != second.ComponentId) return false;
        for (int index = 0; index < first.ComponentId; index++)
        {
            ComponentAddValue a = _componentAdds[first.DataIndex + index], b = _componentAdds[second.DataIndex + index];
            if (a.Type != b.Type || a.ComponentId != b.ComponentId) return false;
        }
        return true;
    }

    private void PlaybackCreatedComponents(World world, CommandPlaybackContext context, int first, int count)
    {
        CommandHeader firstCommand = _commands[first];
        _addedComponentIds.Clear();
        for (int index = firstCommand.DataIndex; index < firstCommand.DataIndex + firstCommand.ComponentId; index++)
        {
            ComponentAddValue value = _componentAdds[index];
            if (value.Type == CommandType.AddBuffer) BufferDataList(value.ComponentId).AddComponentIds(_addedComponentIds);
            else _addedComponentIds.Add(value.ComponentId);
        }
        var values = (Commands: this, Context: context, First: first);
        world.Bundles.ExecuteSpawnBatch(CollectionsMarshal.AsSpan(_addedComponentIds), ReadOnlySpan<int>.Empty, count,
            ref values, static (BundleWriteView row, ref (CommandBuffer Commands, CommandPlaybackContext Context, int First) values) =>
            {
                CommandHeader command = values.Commands._commands[values.First + row.Index];
                for (int index = command.DataIndex; index < command.DataIndex + command.ComponentId; index++)
                {
                    ComponentAddValue component = values.Commands._componentAdds[index];
                    if (component.Type == CommandType.AddBuffer)
                        values.Commands.BufferDataList(component.ComponentId).WriteBundle(row, component.DataIndex);
                    else values.Commands.DataList(component.ComponentId).WriteBundle(row, component.DataIndex);
                }
                command.Entity.Complete(values.Context, row.Entity);
            });
    }

    private void PlaybackComponentAdds(World world, Entity entity, int start, int count)
    {
        _addedComponentIds.Clear();
        for (int index = start; index < start + count; index++)
        {
            ComponentAddValue command = _componentAdds[index];
            if (command.Type == CommandType.AddBuffer) BufferDataList(command.ComponentId).AddComponentIds(_addedComponentIds);
            else _addedComponentIds.Add(command.ComponentId);
        }
        var values = (Commands: this, Start: start, Count: count);
        world.Bundles.ExecuteAdd(entity, CollectionsMarshal.AsSpan(_addedComponentIds), ReadOnlySpan<int>.Empty,
            ref values, static (BundleWriteView row, ref (CommandBuffer Commands, int Start, int Count) values) =>
            {
                for (int index = values.Start; index < values.Start + values.Count; index++)
                {
                    ComponentAddValue command = values.Commands._componentAdds[index];
                    if (command.Type == CommandType.AddBuffer)
                        values.Commands.BufferDataList(command.ComponentId).WriteBundle(row, command.DataIndex);
                    else values.Commands.DataList(command.ComponentId).WriteBundle(row, command.DataIndex);
                }
            });
    }
}
