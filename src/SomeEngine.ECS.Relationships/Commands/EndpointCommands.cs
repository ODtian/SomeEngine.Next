using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

public readonly record struct EndpointCommandEdit(CommandEntity Edge, CommandEntity Source, CommandEntity Target,
    EndpointEditKind Kind = EndpointEditKind.Retarget)
{
    public static EndpointCommandEdit Create(CommandEntity source, CommandEntity target)
        => new(default, source, target, EndpointEditKind.Create);
    public static EndpointCommandEdit Destroy(CommandEntity edge)
        => new(edge, default, default, EndpointEditKind.Destroy);
}

public static class EndpointCommandsExtensions
{
    public static void EditEndpoints<T>(this CommandBuffer commands, ReadOnlySpan<EndpointCommandEdit> edits, bool deferred = false)
        where T : struct
        => EditEndpoints<T>(commands, edits, Span<DeferredEntity>.Empty, deferred);

    public static void EditEndpoints<T>(this CommandBuffer commands, ReadOnlySpan<EndpointCommandEdit> edits,
        Span<DeferredEntity> created, bool deferred = false) where T : struct
    {
        commands.RequireRecording();
        int count = 0;
        bool retargetOnly = true;
        foreach (var edit in edits)
        {
            if (edit.Kind == EndpointEditKind.Create) count++;
            retargetOnly &= edit.Kind == EndpointEditKind.Retarget;
        }
        if (created.Length != count) throw new ArgumentException("Provide one result for each new edge.");
        if (!retargetOnly)
        {
            var editsBucket = commands.Bucket<EndpointEditCommands<T>>(out int editsType);
            if (editsBucket.Count == 0) commands.Use(editsType);
            int editsStart = editsBucket.Add(commands, edits, count);
            int first = commands.ReserveCreates(created);
            commands.Append(CommandKind.Composite, new(first, edits.Length | (deferred ? int.MinValue : 0)), editsType, editsStart);
            return;
        }
        var bucket = commands.Bucket<EndpointCommands<T>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int start = bucket.Add(commands, edits);
        commands.Append(CommandKind.Composite, new(edits.Length, deferred ? 1 : 0), type, start);
    }
}

internal sealed class EndpointCommands<T> : CommandBucket where T : struct
{
    private readonly record struct Row(Entity Edge, Entity Source, Entity Target);
    private Row[] _rows = [];
    private RelationRetarget<T>[] _resolved = [];
    internal int Count;
    public EndpointCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<EndpointCommandEdit> edits)
    {
        if (Count + edits.Length > _rows.Length)
            Array.Resize(ref _rows, Math.Max(Count + edits.Length, Math.Max(8, _rows.Length * 2)));
        int start = Count;
        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            _rows[start + i] = new(commands.Encode(edit.Edge), commands.Encode(edit.Source), commands.Encode(edit.Target));
        }
        Count += edits.Length;
        return start;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        int count = command.Target.Id;
        if (count > _resolved.Length) Array.Resize(ref _resolved, Math.Max(count, Math.Max(8, _resolved.Length * 2)));
        Entity Decode(Entity entity) => entity.Id >= 0 ? entity : results!.Decode(entity);
        for (int i = 0; i < count; i++)
        {
            Row row = _rows[command.DataIndex + i];
            _resolved[i] = new(new(Decode(row.Edge)), Decode(row.Source), Decode(row.Target));
        }
        Relations<T>.Retarget(world, _resolved.AsSpan(0, count), command.Target.Generation != 0);
        return default;
    }

    internal override void Clear(World world) => Count = 0;
}
