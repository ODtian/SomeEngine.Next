using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

internal sealed class EndpointEditCommands<T> : CommandBucket where T : struct
{
    private readonly record struct Row(Entity Edge, Entity Source, Entity Target, EndpointEditKind Kind);
    private Row[] _rows = [];
    private EndpointEdit[] _resolved = [];
    private int _producer;
    internal int Count;
    public EndpointEditCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<EndpointCommandEdit> edits, int creates)
    {
        if (Count + edits.Length > _rows.Length)
            Array.Resize(ref _rows, Math.Max(Count + edits.Length, Math.Max(8, _rows.Length * 2)));
        int start = Count;
        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            Entity edge = edit.Kind == EndpointEditKind.Create ? Entity.Null : commands.Encode(edit.Edge, creates);
            Entity source = edit.Kind == EndpointEditKind.Destroy ? Entity.Null : commands.Encode(edit.Source, creates);
            Entity target = edit.Kind == EndpointEditKind.Destroy ? Entity.Null : commands.Encode(edit.Target, creates);
            _rows[start + i] = new(edge, source, target, edit.Kind);
        }
        _producer = commands.Producer;
        Count += edits.Length;
        return start;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        int count = command.Target.Generation & int.MaxValue, creates = 0;
        if (count > _resolved.Length) Array.Resize(ref _resolved, Math.Max(count, Math.Max(8, _resolved.Length * 2)));
        Entity Decode(Entity entity) => entity.Id < 0 && entity.Generation != 0 ? results!.Decode(entity) : entity;
        for (int i = 0; i < count; i++)
        {
            Row row = _rows[command.DataIndex + i];
            _resolved[i] = new(Decode(row.Edge), Decode(row.Source), Decode(row.Target), row.Kind);
            if (row.Kind == EndpointEditKind.Create) creates++;
        }
        Span<Entity> created = creates == 0 ? Span<Entity>.Empty
            : results!.Created.AsSpan(results.Index(_producer, command.Target.Id), creates);
        Relations<T>.Edit(world, _resolved.AsSpan(0, count), MemoryMarshal.Cast<Entity, RelationEdge<T>>(created), command.Target.Generation < 0);
        if (creates != 0) results!.CompletedCompositeCreates += creates;
        return default;
    }

    internal override void Clear(World world) => Count = 0;
}
