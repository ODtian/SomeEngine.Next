using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private ComponentGrouping?[] _groupings = [];
    private ComponentBits _groupedTypes;
    private readonly Dictionary<TypeSet, GroupTables> _groupTables = new();

    public void GroupBy<T>() where T : struct, IGroupComponent
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        int type = ComponentType<T>.TypeId;
        if ((uint)type < (uint)_groupings.Length && _groupings[type] is not null) return;
        if (type >= _groupings.Length) Array.Resize(ref _groupings, Math.Max(type + 1, Math.Max(16, _groupings.Length * 2)));
        var grouping = new ComponentGrouping<T>(this);
        _groupings[type] = grouping;
        _groupedTypes.Add(type);
        foreach (TypeSet key in _groupTables.Keys.Where(key => key.Bits.Has(type)).ToArray()) _groupTables.Remove(key);
        int initial = ArchetypeCount;
        for (int i = 0; i < initial; i++)
        {
            Archetype archetype = Archetypes[i];
            ColumnEntry[] columns = archetype.PrimaryChunk.Columns;
            if ((uint)type >= (uint)columns.Length || columns[type].ColumnPlusOne == 0) continue;
            archetype.GroupTargets = null;
            archetype.GroupAdditions = [];
            foreach (Chunk chunk in archetype.AllChunks)
            {
                int row = 0;
                while (row < chunk.Count)
                {
                    if (!chunk.Columns[type].Has(row)) { row++; continue; }
                    Entity entity = chunk.Entities[row];
                    int group = grouping.Read(this, chunk, row);
                    ref EntityRecord record = ref Records[entity.Id];
                    ArrangeGroup(entity, ref record, type, group);
                    if (record.Chunk == chunk) row++;
                }
            }
        }
    }

    private IComponentGrouping<T>? ActiveGrouping<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        ComponentGrouping?[] groupings = _groupings;
        return (uint)type < (uint)groupings.Length && groupings[type] is { } grouping
            ? Unsafe.As<IComponentGrouping<T>>(grouping) : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetGroupedMainValue<T>(Entity entity, ref EntityRecord record, in T value, int sharedId, bool added) where T : struct
    {
        if (ComponentType<T>.IsSharedType)
            SetSharedId<T>(entity, ref record, sharedId >= 0 ? sharedId : Shared<T>().Intern(value), added);
        else if (added)
        {
            int type = ComponentType<T>.TypeId;
            Chunk chunk = record.Chunk!;
            chunk.RestorePresence(type, record.Row);
            if (!ComponentType<T>.IsTagType) ColumnEntry.At<T>(ref chunk.Columns[type], record.Row) = value;
            if ((uint)type < (uint)_cachedByType.Length) _cachedByType[type]?.Bind(entity, chunk, record.Row);
            if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, record.Row, NextVersion(), true);
        }
        else ReplacePresentValue(record.Chunk!, record.Row, value);
    }

    internal void ArrangeGroup(Entity entity, ref EntityRecord record, int type, int group)
    {
        Chunk source = record.Chunk!;
        MoveToGroup(entity, ref record, GroupTarget(source, record.Row, type, group));
    }

    internal Archetype GroupTarget(Chunk source, int row, int type, int group)
    {
        ColumnEntry[] columns = source.Columns;
        if ((uint)type < (uint)columns.Length && columns[type].GroupPlusOne is > 0 and var groupColumn
            && source.Archetype.GroupKeys[groupColumn - 1] == group) return source.Archetype;
        Archetype archetype = source.Archetype;
        GroupTables tables = archetype.Types.Bits.Has(type)
            ? (archetype.GroupTargets ??= GetGroupTables(archetype.Types)) : GroupAddition(archetype, type);
        return tables.Groupings.Length == 1 ? tables.Get(group) : FindGroupCombination(tables, source, row, type, group);
    }

    internal void MoveToGroup(Entity entity, ref EntityRecord record, Archetype archetype)
    {
        Chunk source = record.Chunk!;
        if (source.Archetype == archetype) return;
        Chunk target = archetype.WritableChunk;
        MoveKnown(entity, ref record, source, target, source.Archetype.Pod, source.Archetype.Managed, null);
    }

    private Archetype FindGroupCombination(GroupTables tables, Chunk source, int row, int type, int group)
    {
        Span<int> keys = stackalloc int[tables.Groupings.Length];
        GetGroupKeys(tables, source, row, type, group, keys);
        return tables.Get(keys);
    }

    private GroupTables GetGroupTables(TypeSet types)
    {
        if (_groupTables.TryGetValue(types, out var found)) return found;
        var tables = new GroupTables(this, types, _groupings);
        _groupTables.Add(types, tables);
        return tables;
    }

    private GroupTables GroupAddition(Archetype source, int type)
    {
        if ((uint)type < (uint)source.GroupAdditions.Length && source.GroupAdditions[type] is { } found) return found;
        int[] included = new int[source.Types.Types.Length + 1];
        source.Types.Types.CopyTo(included, 0);
        included[^1] = type;
        GroupTables tables = GetGroupTables(new TypeSet(false, included));
        if (type >= source.GroupAdditions.Length)
            Array.Resize(ref source.GroupAdditions, Math.Max(type + 1, Math.Max(16, source.GroupAdditions.Length * 2)));
        source.GroupAdditions[type] = tables;
        return tables;
    }

    private void GetGroupKeys(GroupTables tables, Chunk source, int row, int changedType, int changedKey, Span<int> keys)
    {
        for (int i = 0; i < tables.Groupings.Length; i++)
        {
            ComponentGrouping grouping = tables.Groupings[i];
            int type = grouping.Type;
            if (type == changedType) keys[i] = changedKey;
            else if (source.Archetype.TryGroup(type, out int value)) keys[i] = value;
            else if ((uint)type < (uint)source.Columns.Length && source.Columns[type].Has(row))
                keys[i] = grouping.Read(this, source, row);
            else keys[i] = 0;
        }
    }

    private Archetype FindWithGroups(TypeSet types, Chunk source, int row)
    {
        if (!types.Bits.Intersects(_groupedTypes)) return Find(types);
        GroupTables tables = GetGroupTables(types);
        Span<int> keys = stackalloc int[tables.Groupings.Length];
        GetGroupKeys(tables, source, row, -1, 0, keys);
        return tables.Get(keys);
    }

    internal Archetype CreateGroupedArchetype(GroupTables tables, int[] keys)
    {
        var archetype = new Archetype(this, ArchetypeCount, tables.Types, 1, tables, keys);
        _hasRetainedTypes |= archetype.RetainsOnDestroy;
        _hasOwnedValues |= archetype.OwnedValues.Length != 0;
        RegisterCachedComponents(archetype);
        if (ArchetypeCount == Archetypes.Length) Array.Resize(ref Archetypes, ArchetypeCount * 2);
        Archetypes[ArchetypeCount++] = archetype;
        RefreshQueryMatches();
        return archetype;
    }

    private void ArrangeCreatedGroups(Entity entity)
    {
        ref EntityRecord record = ref Resolve(entity);
        Chunk source = record.Chunk!;
        if (!source.Archetype.Types.Bits.Intersects(_groupedTypes)) return;
        GroupTables tables = GetGroupTables(source.Archetype.Types);
        Span<int> keys = stackalloc int[tables.Groupings.Length];
        for (int i = 0; i < tables.Groupings.Length; i++)
        {
            var grouping = tables.Groupings[i];
            if (source.Columns[grouping.Type].Has(record.Row)) keys[i] = grouping.Read(this, source, record.Row);
            else keys[i] = 0;
        }
        Chunk target = tables.Get(keys).WritableChunk;
        if (target != source)
            MoveKnown(entity, ref record, source, target, source.Archetype.Pod, source.Archetype.Managed, null);
    }
}
