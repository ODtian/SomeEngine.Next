using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Commands;

public readonly record struct ParentCommandEdit(CommandEntity Child, CommandEntity Parent, int InsertAt = -1);

public static class ParentCommandsExtensions
{
    public static void EditParents<TDomain>(this CommandBuffer commands, ReadOnlySpan<ParentCommandEdit> edits, bool deferred = false)
        where TDomain : struct, IHierarchyDomain
    {
        commands.RequireRecording();
        var bucket = commands.Bucket<ParentCommands<TDomain>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int start = bucket.Add(commands, edits);
        commands.Append(CommandKind.Composite, new(edits.Length, deferred ? 1 : 0), type, start);
    }
}

internal sealed class ParentCommands<TDomain> : CommandBucket where TDomain : struct, IHierarchyDomain
{
    private readonly record struct Row(Entity Child, Entity Parent, int InsertAt);
    private Row[] _rows = [];
    private ParentEdit[] _resolved = [];
    internal int Count;
    public ParentCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<ParentCommandEdit> edits)
    {
        if (Count + edits.Length > _rows.Length)
            Array.Resize(ref _rows, Math.Max(Count + edits.Length, Math.Max(8, _rows.Length * 2)));
        int start = Count;
        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            _rows[start + i] = new(commands.Encode(edit.Child), commands.Encode(edit.Parent), edit.InsertAt);
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
            _resolved[i] = new(Decode(row.Child), Decode(row.Parent), row.InsertAt);
        }
        Hierarchy<TDomain>.SetParents(world, _resolved.AsSpan(0, count), command.Target.Generation != 0);
        return default;
    }

    internal override void Clear(World world) => Count = 0;
}
