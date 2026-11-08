using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

public sealed partial class CommandBuffer
{
    internal DeferredRelationEdge<T> RecordRelationCreateUnderGate<T>(
        CommandEntity first,
        CommandEntity second,
        in T payload,
        RelationCreatePlacement placementKind,
        DirectedRelationPlacement directedPlacement,
        UndirectedRelationPlacement undirectedPlacement,
        RelationMaintenanceTiming timing)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref _relationCreates, componentId + 1, 8);
        var creates = (RelationCreateCommands<T>?)_relationCreates[componentId];
        if (creates is null)
            _relationCreates[componentId] = creates = new RelationCreateCommands<T>();
        var cell = new DeferredRelationEdgeCell<T>(this);
        int dataIndex = creates.Append(new(cell, first, second, payload, placementKind,
            directedPlacement, undirectedPlacement, timing));
        _commands.Add(new CommandHeader
        {
            Type = CommandType.CreateRelation,
            Entity = new CommandEntity(Entity.Null),
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
        return new(cell);
    }
}

internal interface IRelationCreateCommands
{
    void Playback(World world, CommandPlaybackContext context, int index);
    void PlaybackFailed();
    void Clear();
}

internal readonly record struct RelationCreateData<T>(
    DeferredRelationEdgeCell<T> Cell,
    CommandEntity First,
    CommandEntity Second,
    T Payload,
    RelationCreatePlacement PlacementKind,
    DirectedRelationPlacement DirectedPlacement,
    UndirectedRelationPlacement UndirectedPlacement,
    RelationMaintenanceTiming Timing)
    where T : struct, IComponent;

internal sealed class RelationCreateCommands<T> : IRelationCreateCommands
    where T : struct, IComponent
{
    private RelationCreateData<T>[] _data = new RelationCreateData<T>[8];
    private int _count;

    internal int Append(in RelationCreateData<T> value)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _data, _count + 1, 8);
        _data[_count] = value;
        return _count++;
    }

    public void Playback(World world, CommandPlaybackContext context, int index)
    {
        ref readonly RelationCreateData<T> data = ref _data[index];
        if (context.IsResolved(data.Cell))
            throw new InvalidOperationException("Relation Create command has already been played back.");
        var first = data.First.Resolve(context);
        var second = data.Second.Resolve(context);
        T payload = data.Payload;
        RelationEdge<T> edge = data.PlacementKind switch
        {
            RelationCreatePlacement.None => world.RelationGraph.Create(world, first, second, in payload, timing: data.Timing),
            RelationCreatePlacement.Directed => world.RelationGraph.Create(world, first, second, in payload,
                data.DirectedPlacement.OutgoingIndex, data.DirectedPlacement.IncomingIndex, data.Timing),
            RelationCreatePlacement.Undirected => world.RelationGraph.Create(world, first, second, in payload,
                data.UndirectedPlacement.EndpointAIndex, data.UndirectedPlacement.EndpointBIndex, data.Timing),
            _ => throw new InvalidOperationException("Unknown relation create placement kind."),
        };
        context.Complete(data.Cell, edge);
    }

    public void PlaybackFailed()
    {
        for (int index = 0; index < _count; index++)
            _data[index].Cell.Invalidate();
    }

    public void Clear()
    {
        for (int index = 0; index < _count; index++)
            _data[index].Cell.InvalidatePending();
        Array.Clear(_data, 0, _count);
        _count = 0;
    }
}
