using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public interface IGroupComponent : IComponent
{
    int GetGroup();
}

internal abstract class ComponentGrouping(int type)
{
    internal readonly int Type = type;
    internal abstract int Read(World world, Chunk chunk, int row);
}

internal interface IComponentGrouping<T> where T : struct
{
    void Set(Entity entity, in T value);
    void Set(ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values);
    void SetKnown(Entity entity, ref EntityRecord record, in T value, int sharedId);
}

internal sealed class ComponentGrouping<T>(World world) : ComponentGrouping(ComponentType<T>.TypeId), IComponentGrouping<T>
    where T : struct, IGroupComponent
{
    private Archetype[] _batchTargets = [];
    private int[] _batchCounts = [];
    private readonly List<Archetype> _batchGroups = new();

    internal override int Read(World world, Chunk chunk, int row)
        => ComponentType<T>.IsSharedType ? world.ReadSharedColumn<T>(chunk.Columns[Type], row).GetGroup()
            : ColumnEntry.At<T>(ref chunk.Columns[Type], row).GetGroup();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(Entity entity, in T value)
    {
        world.RequireStructure();
        ref EntityRecord record = ref world.Resolve(entity);
        SetKnown(entity, ref record, value, -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKnown(Entity entity, ref EntityRecord record, in T value, int sharedId)
    {
        using MembershipEdit membership = world.BeginMembershipEdit(entity);
        Chunk source = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        ColumnEntry[] columns = source.Columns;
        bool added = (uint)type >= (uint)columns.Length || !columns[type].Has(record.Row);
        int group = value.GetGroup();
        int groupColumn = (uint)type < (uint)columns.Length ? columns[type].GroupPlusOne : 0;
        if (groupColumn == 0 || source.Archetype.GroupKeys[groupColumn - 1] != group)
            world.ArrangeGroup(entity, ref record, type, group);
        world.SetGroupedMainValue(entity, ref record, value, sharedId, added);
    }

    public void Set(ReadOnlySpan<Entity> entities, ReadOnlySpan<T> values)
    {
        world.RequireStructure();
        if (_batchTargets.Length < entities.Length) Array.Resize(ref _batchTargets, entities.Length);
        foreach (Archetype target in _batchGroups) _batchCounts[target.Id] = 0;
        _batchGroups.Clear();
        int type = ComponentType<T>.TypeId;
        Archetype? previousSource = null;
        GroupTables? previousTargets = null;
        for (int i = 0; i < entities.Length; i++)
        {
            ref EntityRecord record = ref world.Resolve(entities[i]);
            Archetype source = record.Chunk!.Archetype;
            int group = values[i].GetGroup();
            Archetype target;
            if (source == previousSource && previousTargets is { } targets) target = targets.Get(group);
            else
            {
                target = world.GroupTarget(record.Chunk, record.Row, type, group);
                previousSource = source;
                previousTargets = target.Groups is { Groupings.Length: 1 } single ? single : null;
            }
            _batchTargets[i] = target;
            if (target != source)
            {
                if (target.Id >= _batchCounts.Length)
                    Array.Resize(ref _batchCounts, Math.Max(world.ArchetypeCount, Math.Max(16, _batchCounts.Length * 2)));
                if (_batchCounts[target.Id]++ == 0) _batchGroups.Add(target);
            }
        }
        foreach (Archetype target in _batchGroups)
        {
            int count = 0;
            foreach (Chunk chunk in target.AllChunks) count += chunk.Count;
            target.Reserve(count + _batchCounts[target.Id]);
        }
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i];
            using MembershipEdit membership = world.BeginMembershipEdit(entity);
            ref EntityRecord record = ref world.Records[entity.Id];
            Chunk source = record.Chunk!;
            bool added = (uint)type >= (uint)source.Columns.Length || !source.Columns[type].Has(record.Row);
            world.MoveToGroup(entity, ref record, _batchTargets[i]);
            world.SetGroupedMainValue(entity, ref record, values[i], -1, added);
        }
    }
}

internal readonly struct GroupKeys(int[] values)
{
    internal readonly int[] Values = values;
    internal readonly int Hash = GroupKeysComparer.Hash(values);
}

internal sealed class GroupKeysComparer : IEqualityComparer<GroupKeys>, IAlternateEqualityComparer<ReadOnlySpan<int>, GroupKeys>
{
    internal static readonly GroupKeysComparer Instance = new();
    internal static int Hash(ReadOnlySpan<int> values)
    {
        int hash = 17;
        foreach (int value in values) hash = unchecked(hash * 31 + value);
        return hash;
    }
    public bool Equals(GroupKeys x, GroupKeys y) => x.Values.AsSpan().SequenceEqual(y.Values);
    public int GetHashCode(GroupKeys key) => key.Hash;
    public bool Equals(ReadOnlySpan<int> alternate, GroupKeys key) => alternate.SequenceEqual(key.Values);
    public int GetHashCode(ReadOnlySpan<int> alternate) => Hash(alternate);
    public GroupKeys Create(ReadOnlySpan<int> alternate) => new(alternate.ToArray());
}

internal sealed class GroupTables
{
    private readonly World _world;
    internal readonly TypeSet Types;
    internal readonly ComponentGrouping[] Groupings;
    private readonly Dictionary<int, Archetype> _single = new();
    private Dictionary<GroupKeys, Archetype>? _multiple;

    internal GroupTables(World world, TypeSet types, ComponentGrouping?[] groupings)
    {
        _world = world;
        Types = types;
        Groupings = types.Columns.Groupable.Where(t => (uint)t.Id < (uint)groupings.Length && groupings[t.Id] is not null)
            .Select(t => groupings[t.Id]!).ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype Get(int key)
    {
        if (_single.TryGetValue(key, out var found)) return found;
        return Add(key);
    }

    private Archetype Add(int key)
    {
        Archetype created = _world.CreateGroupedArchetype(this, [key]);
        _single.Add(key, created);
        return created;
    }

    internal Archetype Get(ReadOnlySpan<int> keys)
    {
        if (keys.Length == 1) return Get(keys[0]);
        _multiple ??= new(GroupKeysComparer.Instance);
        var lookup = _multiple.GetAlternateLookup<ReadOnlySpan<int>>();
        if (lookup.TryGetValue(keys, out var existing)) return existing;
        int[] values = keys.ToArray();
        Archetype added = _world.CreateGroupedArchetype(this, values);
        _multiple.Add(new GroupKeys(values), added);
        return added;
    }
}
