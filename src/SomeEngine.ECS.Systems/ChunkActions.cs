using System.ComponentModel;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Systems;

/// <summary>A partial row job whose Execute parameters generate its query, access declarations and execution entries.</summary>
public interface IJobEntity;
/// <summary>A partial range job whose spans generate component access and whose entity span identifies the selected rows.</summary>
public interface IJobChunk;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IChunkAction
{
    /// <summary>Component declarations used to bind ranges, register access and merge tracked writes after completion.</summary>
    static abstract ReadOnlySpan<ChunkAccess> Components { get; }
    static virtual SystemAccesses? AdditionalAccesses => null;
    static virtual void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources) { }
    void CollectAdditionalResources(ref JobResourceAccessBuilder resources) { }
    void Execute(SystemChunk rows);
}

public readonly struct ChunkAccess
{
    internal readonly int Type;
    internal readonly bool WritesValue, Tracked, Structural, Optional, HasResources;
    private ChunkAccess(int type, bool write, bool tracked, bool structural, bool optional = false, bool hasResources = false)
        => (Type, WritesValue, Tracked, Structural, Optional, HasResources) = (type, write, tracked, structural, optional, hasResources);
    public static ChunkAccess Read<T>(bool hasResources = false) where T : struct
        => new(ComponentType<T>.TypeId, false, ComponentType<T>.TracksWritesType, ComponentType<T>.Value.UsesStructureWrites,
            hasResources: hasResources);
    public static ChunkAccess ReadOptional<T>() where T : struct
        => new(ComponentType<T>.TypeId, false, ComponentType<T>.TracksWritesType, ComponentType<T>.Value.UsesStructureWrites, true);
    public static ChunkAccess WriteOptional<T>() where T : struct
    {
        ChunkAccess access = Write<T>();
        return new(access.Type, true, access.Tracked, access.Structural, true);
    }
    public static ChunkAccess Write<T>(bool hasResources = false) where T : struct
    {
        if (ComponentType<T>.IsSharedType || ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write entry.");
        return new(ComponentType<T>.TypeId, true, ComponentType<T>.TracksWritesType, false, hasResources: hasResources);
    }
    public static ChunkAccess ReadBuffer<T>() where T : struct => Read<Buffer<T>>();
    public static ChunkAccess ReadOptionalBuffer<T>() where T : struct => ReadOptional<Buffer<T>>();
    public static ChunkAccess WriteOptionalBuffer<T>() where T : struct => WriteOptional<Buffer<T>>();
    public static ChunkAccess WriteBuffer<T>() where T : struct
        => new(ComponentType<Buffer<T>>.TypeId, true, ComponentType<Buffer<T>>.TracksWritesType, false);
}

/// <summary>A selected contiguous range, borrowed for one generated callback under its declared World access.</summary>
public readonly ref partial struct SystemChunk
{
    private readonly Chunk _chunk;
    private readonly int _start;
    private readonly ulong _version;
    public int Count { get; }
    public ReadOnlySpan<Entity> Entities => _chunk.Entities.AsSpan(_start, Count);
    internal SystemChunk(Chunk chunk, int start, int count, ulong version)
        => (_chunk, _start, Count, _version) = (chunk, start, count, version);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Read<T>() where T : struct
    {
        if (ComponentType<T>.IsSharedType) throw new InvalidOperationException("Use the shared range entry.");
        return ColumnEntry.Span<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _chunk.Count).Slice(_start, Count);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Write<T>() where T : struct
    {
        if (ComponentType<T>.IsSharedType || ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write entry.");
        if (ComponentType<T>.TracksWritesType)
        {
            ref ComponentChanges changes = ref _chunk.Changes![_chunk.Columns[ComponentType<T>.TypeId].ColumnPlusOne - 1];
            changes.StampRange(_start, Count, _version, ComponentType<T>.TracksAddedType, publish: false);
        }
        return ColumnEntry.Span<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _chunk.Count).Slice(_start, Count);
    }
    public SharedReadOnlySpan<T> ReadShared<T>() where T : struct, ISharedComponent
        => _chunk.Archetype.World.ReadSharedRange<T>(_chunk.Columns[ComponentType<T>.TypeId], _start, Count);
    public ReadOnlyBuffer<T> ReadBuffer<T>(int row) where T : struct
    {
        ref Buffer<T> buffer = ref ColumnEntry.At<Buffer<T>>(ref _chunk.Columns[ComponentType<Buffer<T>>.TypeId], _start + row);
        return new(_chunk.Archetype.World, in buffer, false);
    }
    public DynamicBuffer<T> GetBuffer<T>(int row) where T : struct
    {
        World world = _chunk.Archetype.World;
        ref Buffer<T> buffer = ref ColumnEntry.At<Buffer<T>>(ref _chunk.Columns[ComponentType<Buffer<T>>.TypeId], _start + row);
        if (ComponentType<Buffer<T>>.TracksWritesType)
            _chunk.Changes![_chunk.Columns[ComponentType<Buffer<T>>.TypeId].ColumnPlusOne - 1]
                .StampWord(_start + row, _version, ComponentType<Buffer<T>>.TracksAddedType);
        if (buffer.Items is null) buffer = world.BufferPool<T>().Rent();
        return new(world, ref buffer, false);
    }
}

internal static class ChunkActionTypes<TAction> where TAction : struct, IChunkAction
{
    private static readonly Dictionary<QueryDefinition, QueryDescription> FilterDescriptions = [];
    internal static readonly ChunkAccess[] Components = TAction.Components.ToArray();
    internal static readonly int[] Writes = Components.Where(c => c.WritesValue).Select(c => c.Type).ToArray();
    internal static readonly int[] TrackedWrites = Components.Where(c => c.WritesValue && c.Tracked).Select(c => c.Type).ToArray();
    internal static readonly QueryDescription Query = MakeQuery();

    internal static Query Bind(Query query)
    {
        QueryDescription description = query.Description;
        bool complete = true;
        foreach (ChunkAccess access in Components)
            if (access.Optional
                ? !description.Required.Has(access.Type) && !description.OptionalReads.Has(access.Type) && !description.Any.Has(access.Type)
                : !description.Required.Has(access.Type)) { complete = false; break; }
        if (complete) return query;
        QueryDescription bound;
        lock (FilterDescriptions)
        {
            QueryDefinition definition = description.Definition;
            if (!FilterDescriptions.TryGetValue(definition, out bound!))
            {
                bound = description.Copy();
                foreach (ChunkAccess access in Components)
                    if (access.Optional) bound.OptionalReads.Add(access.Type);
                    else bound.Required.Add(access.Type);
                FilterDescriptions.Add(definition, bound);
            }
        }
        return query.World.Query(bound).Since(query.SinceVersion);
    }
    private static QueryDescription MakeQuery()
    {
        var query = new QueryDescription();
        foreach (ChunkAccess access in Components)
            if (access.Optional) query.OptionalReads.Add(access.Type);
            else query.Required.Add(access.Type);
        return query;
    }

    internal static bool Serial(SystemAccesses? additional)
    {
        if (additional is null) return false;
        if (additional.HasRandomWrites) return true;
        foreach (WorldValueAccess access in additional.Values)
            if (access.Kind == WorldValueKind.Table && !access.Write && Writes.Contains(access.Type)) return true;
        return false;
    }
}
