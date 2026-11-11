using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal sealed class CopyData
{
    internal Archetype[] Targets = [];
    internal readonly Dictionary<Archetype, int> Counts = new();
    internal readonly Dictionary<Archetype, TypeSet> Types = new();
    internal readonly Dictionary<long, int> Shared = new();
    internal readonly Dictionary<long, int> SharedReferences = new();
    internal EntityMapData? Entities;
}

public sealed partial class World
{
    private CopyData? _copies;
    public int DataGeneration { get; internal set; }

    public void Clone(ReadOnlySpan<Entity> sources, Span<Entity> targets)
        => CloneTo(this, sources, targets);

    public void CloneTo(World target, ReadOnlySpan<Entity> sources, Span<Entity> targets)
    {
        using WorldAccess sourceAccess = OpenStructure();
        using WorldAccess targetAccess = target.OpenStructure();
        RequireStructure(); target.RequireStructure();
        targets = targets[..sources.Length];
        CreateCopyTargets(target, sources, targets);
        var entities = new EntityMap(sources, targets, sources.Length > 8 ? target.CopyEntityMap() : null);
        CopyValuesTo(target, sources, targets, entities);
        target.CompleteCopies(targets);
    }

    public void Clone<TMapper>(ReadOnlySpan<Entity> sources, Span<Entity> targets, ref TMapper mapper)
        where TMapper : struct, IEntityMapper
        => CloneTo(this, sources, targets, ref mapper);

    public void CloneTo<TMapper>(World target, ReadOnlySpan<Entity> sources, Span<Entity> targets, ref TMapper mapper)
        where TMapper : struct, IEntityMapper
    {
        using WorldAccess sourceAccess = OpenStructure();
        using WorldAccess targetAccess = target.OpenStructure();
        RequireStructure(); target.RequireStructure();
        targets = targets[..sources.Length];
        CreateCopyTargets(target, sources, targets);
        var entities = EntityMap.Create(sources, targets, ref mapper, sources.Length > 8 ? target.CopyEntityMap() : null);
        CopyValuesTo(target, sources, targets, entities);
        target.CompleteCopies(targets);
    }

    internal EntityMapData CopyEntityMap() => (_copies ??= new()).Entities ??= new();

    private Archetype CopyTarget(World target, Entity source)
    {
        ref EntityRecord record = ref Resolve(source);
        Chunk chunk = record.Chunk!;
        Archetype archetype = chunk.Archetype;
        TypeSet types = archetype.Types;
        if (archetype.Computed.Length != 0)
        {
            var copies = _copies ??= new();
            if (!copies.Types.TryGetValue(archetype, out types!))
            {
                types = new(false, archetype.Components.Where(type => !type.IsComputed).Select(type => type.Id).ToArray());
                copies.Types.Add(archetype, types);
            }
        }
        if (!types.Bits.Intersects(target._groupedTypes))
            return this == target && types == archetype.Types ? archetype : target.Find(types);
        if (archetype.Groupable.Any(type => type.HasEntityReferences)) return target.Find(types);
        if (this == target && types == archetype.Types) return archetype;
        GroupTables groups = target.GetGroupTables(types);
        Span<int> keys = stackalloc int[groups.Groupings.Length];
        for (int i = 0; i < groups.Groupings.Length; i++)
        {
            ComponentGrouping grouping = groups.Groupings[i];
            int type = grouping.Type;
            keys[i] = archetype.TryGroup(type, out int group) ? group
                : chunk.Columns[type].Has(record.Row) ? grouping.Read(this, chunk, record.Row) : 0;
        }
        return groups.Get(keys);
    }

    internal Archetype[] BindCopyTargets(World target, ReadOnlySpan<Entity> sources)
    {
        var bound = new Archetype[sources.Length];
        for (int i = 0; i < sources.Length; i++) bound[i] = CopyTarget(target, sources[i]);
        return bound;
    }

    internal void CreateCopyTargets(World target, ReadOnlySpan<Entity> sources, Span<Entity> targets, Archetype[]? bound = null)
    {
        var copies = target._copies ??= new();
        if (bound is null)
        {
            if (copies.Targets.Length < sources.Length)
                Array.Resize(ref copies.Targets, Math.Max(sources.Length, Math.Max(4, copies.Targets.Length * 2)));
            bound = copies.Targets;
            for (int i = 0; i < sources.Length; i++) bound[i] = CopyTarget(target, sources[i]);
        }
        target.CreateCopies(bound.AsSpan(0, sources.Length), 1, targets);
    }

    internal void CreateCopies(ReadOnlySpan<Archetype> bound, int copiesCount, Span<Entity> targets)
    {
        var copies = _copies ??= new();
        copies.Counts.Clear(); copies.Shared.Clear();
        for (int i = 0; i < bound.Length; i++)
        {
            Archetype archetype = bound[i];
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(copies.Counts, archetype, out _);
            count += copiesCount;
        }
        ReserveEntities(Math.Max(Used, _count + bound.Length * copiesCount));
        foreach (var pair in copies.Counts)
        {
            int count = 0;
            foreach (Chunk chunk in pair.Key.AllChunks) count += chunk.Count;
            if (ChunkCapacity == 0 && (long)pair.Value * 2 >= pair.Key.PrimaryChunk.Capacity)
                pair.Key.ReserveRows(count + pair.Value);
            else pair.Key.Reserve(count + pair.Value);
        }
        ulong version = 0;
        if (copiesCount > 0)
            foreach (Archetype archetype in bound)
                if (archetype.Tracked.Length != 0) { version = NextVersion(); break; }
        for (int copy = 0; copy < copiesCount; copy++)
        for (int i = 0; i < bound.Length; i++)
        {
            Chunk chunk = bound[i].WritableChunk;
            targets[copy * bound.Length + i] = CreateKnown(ref chunk, version);
        }
    }

    internal void CopyValuesTo(World target, ReadOnlySpan<Entity> sources, ReadOnlySpan<Entity> targets, in EntityMap entities)
    {
        var copies = target._copies!;
        copies.SharedReferences.Clear();
        for (int i = 0; i < sources.Length; i++)
        {
            ref EntityRecord source = ref Records[sources[i].Id];
            ref EntityRecord destination = ref target.Records[targets[i].Id];
            Chunk from = source.Chunk!, to = destination.Chunk!;
            int sourceRow = source.Row, targetRow = destination.Row;
            Archetype archetype = from.Archetype;
            if (archetype.Types.Columns.CanCopyPodAndBuffers && (archetype.TransferKinds & 4) == 0)
            {
                foreach (ComponentType type in archetype.Components)
                {
                    ref ColumnEntry column = ref from.Columns[type.Id];
                    ref ColumnEntry copied = ref to.Columns[type.Id];
                    if (type.ContainsReferences) type.Clone(target, ref column, sourceRow, ref copied, targetRow);
                    else if (!type.IsTag)
                        Chunk.CopyBytes(column.Rows + (nint)sourceRow * type.Size,
                            copied.Rows + (nint)targetRow * type.Size, type.Size);
                    if (type.HasEntityReferences) type.Remap(ref copied, targetRow, entities);
                }
            }
            else foreach (ComponentType type in archetype.Components)
            {
                if (type.IsComputed) continue;
                ref ColumnEntry column = ref from.Columns[type.Id];
                ref ColumnEntry copied = ref to.Columns[type.Id];
                if (!column.Has(sourceRow))
                {
                    target.EnsurePresence(to, type.Id)[targetRow >> 6] &= ~(1UL << (targetRow & 63));
                    if (type.TracksWrites) to.Changes![copied.ColumnPlusOne - 1].ClearRow(targetRow, type.TracksAdded);
                    if ((uint)type.Id < (uint)target._cachedByType.Length) target._cachedByType[type.Id]?.Remove(targets[i]);
                    continue;
                }
                if (type.IsShared && (this != target || type.HasEntityReferences))
                {
                    var shared = type.HasEntityReferences ? copies.SharedReferences : copies.Shared;
                    long key = ((long)type.Id << 32) | (uint)ColumnEntry.At<int>(ref column, sourceRow);
                    if (!shared.TryGetValue(key, out int id))
                    {
                        id = type.InternShared(this, target, column, sourceRow, entities);
                        shared.Add(key, id);
                    }
                    ColumnEntry.At<int>(ref copied, targetRow) = id;
                }
                else
                {
                    if (!type.ContainsReferences && !type.HasValueOperations && !type.IsShared)
                    {
                        if (!type.IsTag)
                            Chunk.CopyBytes(column.Rows + (nint)sourceRow * type.Size,
                                copied.Rows + (nint)targetRow * type.Size, type.Size);
                    }
                    else type.Clone(target, ref column, sourceRow, ref copied, targetRow);
                    if (type.HasEntityReferences && !type.IsShared) type.Remap(ref copied, targetRow, entities);
                }
                if (!from.IsEnabled(type.Id, sourceRow)) target.SetEnabled(to, type.Id, targetRow, false);
            }
            _sparse?.CloneTo(target, sources[i].Id, targets[i], entities);
        }
    }

    internal void CompleteCopies(ReadOnlySpan<Entity> entities, bool publishMembership = true)
    {
        foreach (Entity entity in entities)
        {
            ref EntityRecord record = ref Records[entity.Id];
            Chunk chunk = record.Chunk!;
            foreach (ComponentType type in chunk.Archetype.Shared)
                if (chunk.Columns[type.Id].Has(record.Row)) CopySharedMember(entity, chunk, type.Id, record.Row);
            foreach (ComponentType type in chunk.Archetype.Indexed)
                if (chunk.Columns[type.Id].Has(record.Row) && (uint)type.Id < (uint)_valueIndexes.Length)
                    _valueIndexes[type.Id]?.AddFromEntity(entity, in chunk.Columns[type.Id], record.Row);
            if (chunk.Archetype.Groupable.Length != 0) ArrangeCreatedGroups(entity);
        }
        // Clear copied derived values before primary initializers rebuild their inverses.
        InitializeCopiedComponents(entities, computed: true);
        InitializeCopiedComponents(entities, computed: false);
        if (publishMembership) PublishCreatedMembership(entities);
    }

    private void InitializeCopiedComponents(ReadOnlySpan<Entity> entities, bool computed)
    {
        foreach (Entity entity in entities)
        {
            ComponentType[] initializers = Records[entity.Id].Chunk!.Archetype.Initializers;
            foreach (ComponentType type in initializers)
            {
                if (type.IsComputed != computed) continue;
                ref EntityRecord record = ref Records[entity.Id];
                if (record.Chunk!.Columns[type.Id].Has(record.Row)) type.InitializeEntity(this, entity);
            }
        }
    }
}
