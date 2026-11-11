using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

public readonly record struct CapturedParentEdit(CommandEntity Child, Entity Before, CommandEntity After, int InsertAt = -1);
public readonly record struct CapturedEndpointEdit(CommandEntity Edge, Entity BeforeSource, Entity BeforeTarget,
    CommandEntity AfterSource, CommandEntity AfterTarget, EndpointEditKind Kind = EndpointEditKind.Retarget);

internal readonly record struct CapturedEditSegment(int RowStart, int RowCount, int DataGeneration,
    ulong MembershipVersion, QueryData? Query = null);

public static class CapturedTopologyCommandsExtensions
{
    public static void EditParents<TDomain>(this CommandBuffer commands, Query query,
        ReadOnlySpan<CapturedParentEdit> edits, int dataGeneration, ulong membershipVersion,
        bool deferred = false) where TDomain : struct, IHierarchyDomain
    {
        commands.RequireRecording();
        var bucket = commands.Bucket<CapturedParentCommands<TDomain>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int segment = bucket.Add(commands, edits, dataGeneration, membershipVersion, query.Data);
        commands.Append(CommandKind.Composite, new(edits.Length, deferred ? 1 : 0), type, segment);
    }

    public static void EditParents<TDomain>(this CommandBuffer commands, ReadOnlySpan<CapturedParentEdit> edits,
        int dataGeneration, bool deferred = false) where TDomain : struct, IHierarchyDomain
    {
        commands.RequireRecording();
        var bucket = commands.Bucket<CapturedParentCommands<TDomain>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int segment = bucket.Add(commands, edits, dataGeneration, 0);
        commands.Append(CommandKind.Composite, new(edits.Length, deferred ? 1 : 0), type, segment);
    }

    public static void EditEndpoints<T>(this CommandBuffer commands, ReadOnlySpan<CapturedEndpointEdit> edits,
        Span<DeferredEntity> created, int dataGeneration, bool deferred = false) where T : struct
        => RecordEndpoints<T>(commands, null, edits, created, dataGeneration, 0, deferred);

    public static void EditEndpoints<T>(this CommandBuffer commands, Query query,
        ReadOnlySpan<CapturedEndpointEdit> edits, Span<DeferredEntity> created, int dataGeneration,
        ulong membershipVersion, bool deferred = false) where T : struct
        => RecordEndpoints<T>(commands, query.Data, edits, created, dataGeneration, membershipVersion, deferred);

    private static void RecordEndpoints<T>(CommandBuffer commands, QueryData? query,
        ReadOnlySpan<CapturedEndpointEdit> edits, Span<DeferredEntity> created, int dataGeneration,
        ulong membershipVersion, bool deferred) where T : struct
    {
        commands.RequireRecording();
        int creates = 0;
        foreach (var edit in edits) if (edit.Kind == EndpointEditKind.Create) creates++;
        if (created.Length != creates) throw new ArgumentException("Provide one result for each new edge.");
        var bucket = commands.Bucket<CapturedEndpointCommands<T>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int segment = bucket.Add(commands, edits, creates, dataGeneration, membershipVersion, query);
        int first = commands.ReserveCreates(created);
        commands.Append(CommandKind.Composite, new(first, deferred ? int.MinValue : 0), type, segment);
    }
}

internal sealed class CapturedParentCommands<TDomain> : CommandBucket where TDomain : struct, IHierarchyDomain
{
    private readonly record struct Row(Entity Child, Entity Before, Entity After, int InsertAt);
    private Row[] _rows = [];
    private CapturedEditSegment[] _segments = [];
    private ParentEdit[] _resolved = [];
    private readonly HashSet<Entity> _seen = [];
    private int _rowCount;
    internal int Count;
    public CapturedParentCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<CapturedParentEdit> edits, int generation, ulong membershipVersion,
        QueryData? query = null)
    {
        if (_rowCount + edits.Length > _rows.Length)
            Array.Resize(ref _rows, Math.Max(_rowCount + edits.Length, Math.Max(8, _rows.Length * 2)));
        if (Count == _segments.Length) Array.Resize(ref _segments, Math.Max(8, Count * 2));
        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            _rows[_rowCount + i] = new(commands.Encode(edit.Child), edit.Before, commands.Encode(edit.After), edit.InsertAt);
        }
        _segments[Count] = new(_rowCount, edits.Length, generation, membershipVersion, query);
        _rowCount += edits.Length;
        return Count++;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        CapturedEditSegment segment = _segments[command.DataIndex];
        if (world.DataGeneration != segment.DataGeneration)
            throw new InvalidOperationException("The captured parent edits belong to an earlier World generation.");
        if (segment.Query is { } query && world.ReadMembershipVersion(query) != segment.MembershipVersion)
            throw new InvalidOperationException("The captured query membership changed before its command was played.");
        if (segment.RowCount > _resolved.Length) Array.Resize(ref _resolved, Math.Max(segment.RowCount, Math.Max(8, _resolved.Length * 2)));
        Entity Decode(Entity entity) => entity.Id >= 0 ? entity : results!.Decode(entity);
        _seen.Clear();
        for (int i = 0; i < segment.RowCount; i++)
        {
            Row row = _rows[segment.RowStart + i];
            Entity child = Decode(row.Child);
            if (_seen.Add(child) && Hierarchy<TDomain>.GetParent(world, child) != row.Before)
                throw new InvalidOperationException("The captured parent changed before its command was played.");
            _resolved[i] = new(child, Decode(row.After), row.InsertAt);
        }
        Hierarchy<TDomain>.SetParents(world, _resolved.AsSpan(0, segment.RowCount), command.Target.Generation != 0);
        return default;
    }

    internal override void Clear(World world) { Count = _rowCount = 0; _seen.Clear(); }
}

internal sealed class CapturedEndpointCommands<T> : CommandBucket where T : struct
{
    private readonly record struct Row(Entity Edge, Entity BeforeSource, Entity BeforeTarget,
        Entity AfterSource, Entity AfterTarget, EndpointEditKind Kind);
    private Row[] _rows = [];
    private CapturedEditSegment[] _segments = [];
    private EndpointEdit[] _resolved = [];
    private readonly HashSet<Entity> _seen = [];
    private int _rowCount, _producer;
    internal int Count;
    public CapturedEndpointCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<CapturedEndpointEdit> edits, int creates, int generation,
        ulong membershipVersion, QueryData? query)
    {
        if (_rowCount + edits.Length > _rows.Length)
            Array.Resize(ref _rows, Math.Max(_rowCount + edits.Length, Math.Max(8, _rows.Length * 2)));
        if (Count == _segments.Length) Array.Resize(ref _segments, Math.Max(8, Count * 2));
        for (int i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            Entity edge = edit.Kind == EndpointEditKind.Create ? Entity.Null : commands.Encode(edit.Edge, creates);
            Entity source = edit.Kind == EndpointEditKind.Destroy ? Entity.Null : commands.Encode(edit.AfterSource, creates);
            Entity target = edit.Kind == EndpointEditKind.Destroy ? Entity.Null : commands.Encode(edit.AfterTarget, creates);
            _rows[_rowCount + i] = new(edge, edit.BeforeSource, edit.BeforeTarget, source, target, edit.Kind);
        }
        _producer = commands.Producer;
        _segments[Count] = new(_rowCount, edits.Length, generation, membershipVersion, query);
        _rowCount += edits.Length;
        return Count++;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        CapturedEditSegment segment = _segments[command.DataIndex];
        if (world.DataGeneration != segment.DataGeneration)
            throw new InvalidOperationException("The captured endpoint edits belong to an earlier World generation.");
        if (segment.Query is { } query && world.ReadMembershipVersion(query) != segment.MembershipVersion)
            throw new InvalidOperationException("The captured query membership changed before its command was played.");
        if (segment.RowCount > _resolved.Length) Array.Resize(ref _resolved, Math.Max(segment.RowCount, Math.Max(8, _resolved.Length * 2)));
        Entity Decode(Entity entity) => entity.Id < 0 && entity.Generation != 0 ? results!.Decode(entity) : entity;
        int creates = 0;
        _seen.Clear();
        for (int i = 0; i < segment.RowCount; i++)
        {
            Row row = _rows[segment.RowStart + i];
            Entity edge = Decode(row.Edge);
            if (row.Kind == EndpointEditKind.Create)
            {
                if (row.BeforeSource != Entity.Null || row.BeforeTarget != Entity.Null)
                    throw new InvalidOperationException("A captured creation has empty previous endpoints.");
                creates++;
            }
            else if (_seen.Add(edge))
            {
                if (edge.Id < 0 && edge.Generation == 0)
                {
                    if (row.BeforeSource != Entity.Null || row.BeforeTarget != Entity.Null)
                        throw new InvalidOperationException("An edge created in this batch has empty previous endpoints.");
                }
                else
                {
                    RelationEndpoints<T> before = world.ReadValue<RelationEndpoints<T>>(edge);
                    if (before.Source != row.BeforeSource || before.Target != row.BeforeTarget)
                        throw new InvalidOperationException("The captured endpoints changed before their command was played.");
                }
            }
            _resolved[i] = new(edge, Decode(row.AfterSource), Decode(row.AfterTarget), row.Kind);
        }
        Span<Entity> created = creates == 0 ? Span<Entity>.Empty
            : results!.Created.AsSpan(results.Index(_producer, command.Target.Id), creates);
        Relations<T>.Edit(world, _resolved.AsSpan(0, segment.RowCount), MemoryMarshal.Cast<Entity, RelationEdge<T>>(created), command.Target.Generation < 0);
        if (creates != 0) results!.CompletedCompositeCreates += creates;
        return default;
    }

    internal override void Clear(World world) { Count = _rowCount = 0; _seen.Clear(); }
}
