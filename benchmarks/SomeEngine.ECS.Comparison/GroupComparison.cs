using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public readonly record struct GroupedNumber(int Value, int Ordinal) : IGroupComponent, EasyEcs.IComponent, EasyEcs.IGroup,
    Friflo.Engine.ECS.IComponent
{
    public int GetGroup() { GroupProjection.Calls++; return Value; }
    int EasyEcs.IGroup.Group => GetGroup();
}
internal static class GroupProjection { internal static int Calls; }

internal static class GroupInputs
{
    private static GroupedNumber[] _initial = [];
    private static GroupedNumber[] _growth = [];
    internal static GroupedNumber[] Initial(int count)
    {
        if (_initial.Length != count)
        {
            var values = new GroupedNumber[count];
            for (int i = 0; i < count; i++) values[i] = new GroupedNumber(i % 16, i);
            _initial = values;
        }
        return _initial;
    }
    internal static GroupedNumber[] Growth(int count)
    {
        if (_growth.Length != count)
        {
            var values = new GroupedNumber[count];
            for (int i = 0; i < count; i++) values[i] = new GroupedNumber(i == 0 ? 16 : i % 16, count + i);
            _growth = values;
        }
        return _growth;
    }
}

internal struct GroupPass(int[] output, int count, int key) : IComponentAction<Position, Velocity, GroupedNumber>,
    EasyEcs.IForEachBatch<Position, Velocity, GroupedNumber>
{
    internal int Count = count;
    internal bool Valid = true;
    internal long Total;
    public static int Writes => 1;
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<GroupedNumber> numbers)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i].Y = velocities[i].X + numbers[i].Value;
            Total += (int)positions[i].Y;
            Valid &= numbers[i].Value == key && (int)positions[i].X == numbers[i].Ordinal;
            output[Count++] = numbers[i].Ordinal;
        }
    }
    public void Execute(EasyEcs.EntitySpan entities, Span<Position> positions, Span<Velocity> velocities, Span<GroupedNumber> numbers)
        => Execute(positions, velocities, numbers);
}

internal abstract partial class ComparisonWorld
{
    internal int GroupShift, GroupMemberCount, GroupProjectionCalls;
    internal bool GroupChanges, GroupValuesAligned;
    internal int[] GroupMembers = [];
    internal GroupedNumber[] GroupGrowthValues = [];

    internal void PrepareGroupResult(string workload)
    {
        GroupChanges = workload == "group-change";
        GroupMembers = new int[Count];
        GroupGrowthValues = GroupInputs.Growth(Count);
    }
    internal void BeginGroupCycle()
    {
        if (GroupChanges) GroupShift ^= 1;
        GroupProjection.Calls = 0;
        GroupMemberCount = 0;
        GroupValuesAligned = true;
        ReadTotal = 0;
    }
    internal void FinishGroupPass(in GroupPass pass)
    {
        GroupMemberCount = pass.Count;
        GroupValuesAligned &= pass.Valid;
        ReadTotal += pass.Total;
    }
    internal bool CheckGroupResult()
    {
        var seen = new bool[Count];
        foreach (int ordinal in GroupMembers.AsSpan(0, GroupMemberCount))
        {
            if ((uint)ordinal >= (uint)Count || seen[ordinal]) return false;
            seen[ordinal] = true;
        }
        long total = 0;
        bool correct = GroupProjectionCalls == Count / 4 && GroupValuesAligned && GroupMemberCount == Count / 2;
        for (int i = 0; i < Count; i++)
        {
            int key = (i + (i % 4 == 0 ? GroupShift : 0)) % 16;
            bool selected = key >= 4 && key <= 11;
            correct &= seen[i] == selected;
            if (selected) total += key + 1;
        }
        return correct && ReadTotal == total;
    }
}

internal readonly record struct GroupEntity(int Id, int Generation);
internal interface IGroupPartition { int Key { get; } bool Grouped { get; } }

internal interface IGroupingBackend<TEntity, TPartition>
    where TEntity : struct where TPartition : class, IGroupPartition
{
    TPartition Create(int key);
    TEntity Move(TPartition source, TPartition target, TEntity entity, in GroupedNumber value);
    void Set(TPartition partition, TEntity entity, in GroupedNumber value);
    GroupedNumber Read(TPartition partition, TEntity entity);
    void Destroy(TPartition partition, TEntity entity);
    void Visit(TPartition partition, ref GroupPass pass);
    void SetExtra(TPartition partition, TEntity entity);
    void RemoveExtra(TPartition partition, TEntity entity);
    int Count(TPartition partition);
    void Dispose(TPartition partition);
}

internal sealed class ReferenceGrouping<TEntity, TPartition, TBackend> : IDisposable
    where TEntity : struct where TPartition : class, IGroupPartition
    where TBackend : struct, IGroupingBackend<TEntity, TPartition>
{
    private struct Entry
    {
        internal TEntity Entity;
        internal TPartition Partition;
        internal int Generation;
        internal int Next;
    }
    private readonly TBackend _backend;
    private readonly TPartition _base;
    private readonly Dictionary<int, TPartition> _groups = new();
    private Entry[] _entities;
    private int _used, _free = -1;
    internal readonly GroupEntity[] Entities;

    internal ReferenceGrouping(TPartition initial, TEntity[] entities, TBackend backend)
    {
        _base = initial;
        _backend = backend;
        _entities = new Entry[entities.Length];
        _used = entities.Length;
        Entities = new GroupEntity[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            _entities[i] = new() { Entity = entities[i], Partition = initial, Generation = 1 };
            Entities[i] = new(i, 1);
        }
    }

    private ref Entry Resolve(GroupEntity entity)
    {
        if ((uint)entity.Id >= (uint)_entities.Length || _entities[entity.Id].Generation != entity.Generation)
            throw new ArgumentException("Grouped entity generation has expired.");
        return ref _entities[entity.Id];
    }

    internal GroupEntity Add(TEntity entity)
    {
        int id;
        if (_free >= 0) { id = _free; _free = _entities[id].Next; }
        else
        {
            id = _used++;
            if (id == _entities.Length) Array.Resize(ref _entities, Math.Max(16, _entities.Length * 2));
            _entities[id].Generation = 1;
        }
        ref Entry entry = ref _entities[id];
        entry.Entity = entity;
        entry.Partition = _base;
        return new(id, entry.Generation);
    }

    internal GroupedNumber Read(GroupEntity entity)
    {
        ref Entry entry = ref Resolve(entity);
        return _backend.Read(entry.Partition, entry.Entity);
    }

    internal void Destroy(GroupEntity entity)
    {
        ref Entry entry = ref Resolve(entity);
        _backend.Destroy(entry.Partition, entry.Entity);
        entry = new() { Generation = entry.Generation + 1, Next = _free };
        _free = entity.Id;
    }

    internal void Set(GroupEntity entity, in GroupedNumber value)
    {
        ref Entry entry = ref Resolve(entity);
        int key = value.GetGroup();
        if (entry.Partition.Grouped && entry.Partition.Key == key) { _backend.Set(entry.Partition, entry.Entity, value); return; }
        if (!_groups.TryGetValue(key, out TPartition? target))
        {
            target = _backend.Create(key);
            _groups.Add(key, target);
        }
        TEntity moved = _backend.Move(entry.Partition, target, entry.Entity, value);
        entry.Entity = moved;
        entry.Partition = target;
    }

    internal void Visit(int key, ref GroupPass pass)
    {
        if (_groups.TryGetValue(key, out TPartition? partition)) _backend.Visit(partition, ref pass);
    }

    internal void AddRemove(int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref Entry entry = ref Resolve(Entities[i]);
            _backend.SetExtra(entry.Partition, entry.Entity);
        }
        for (int i = 0; i < count; i++)
        {
            ref Entry entry = ref Resolve(Entities[i]);
            _backend.RemoveExtra(entry.Partition, entry.Entity);
        }
    }

    internal int EntityCount
    {
        get
        {
            int count = _backend.Count(_base);
            foreach (TPartition partition in _groups.Values) count += _backend.Count(partition);
            return count;
        }
    }
    public void Dispose()
    {
        foreach (TPartition partition in _groups.Values) _backend.Dispose(partition);
        _groups.Clear();
        _entities = [];
    }
}

internal sealed class FrifloGroupPartition(EntityStore world, int key, bool grouped = true) : IGroupPartition
{
    internal readonly EntityStore World = world;
    internal readonly ArchetypeQuery<Position, Velocity, GroupedNumber> Query = world.Query<Position, Velocity, GroupedNumber>();
    public int Key => key;
    public bool Grouped => grouped;
}
internal readonly struct FrifloGrouping : IGroupingBackend<Friflo.Engine.ECS.Entity, FrifloGroupPartition>
{
    public FrifloGroupPartition Create(int key) => new(new EntityStore(), key);
    public Friflo.Engine.ECS.Entity Move(FrifloGroupPartition source, FrifloGroupPartition target,
        Friflo.Engine.ECS.Entity entity, in GroupedNumber value)
    {
        var moved = target.World.CreateEntity();
        entity.CopyEntity(moved);
        moved.AddComponent(value);
        entity.DeleteEntity();
        return moved;
    }
    public void Set(FrifloGroupPartition partition, Friflo.Engine.ECS.Entity entity, in GroupedNumber value) => entity.AddComponent(value);
    public GroupedNumber Read(FrifloGroupPartition partition, Friflo.Engine.ECS.Entity entity) => entity.GetComponent<GroupedNumber>();
    public void Destroy(FrifloGroupPartition partition, Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
    public void Visit(FrifloGroupPartition partition, ref GroupPass pass)
    {
        foreach (var (positions, velocities, numbers, _) in partition.Query.Chunks)
            pass.Execute(positions.Span, velocities.Span, numbers.Span);
    }
    public void SetExtra(FrifloGroupPartition partition, Friflo.Engine.ECS.Entity entity) => entity.AddComponent(new Extra { Value = 7 });
    public void RemoveExtra(FrifloGroupPartition partition, Friflo.Engine.ECS.Entity entity) => entity.RemoveComponent<Extra>();
    public int Count(FrifloGroupPartition partition) => partition.World.Count;
    public void Dispose(FrifloGroupPartition partition) { }
}

internal sealed class ArchGroupPartition(Arch.Core.World world, int key, bool grouped = true) : IGroupPartition
{
    internal readonly Arch.Core.World World = world;
    internal readonly Arch.Core.QueryDescription Query = new Arch.Core.QueryDescription().WithAll<Position, Velocity, GroupedNumber>();
    public int Key => key;
    public bool Grouped => grouped;
}
internal readonly struct ArchGrouping : IGroupingBackend<Arch.Core.Entity, ArchGroupPartition>
{
    public ArchGroupPartition Create(int key) => new(Arch.Core.World.Create(), key);
    public Arch.Core.Entity Move(ArchGroupPartition source, ArchGroupPartition target, Arch.Core.Entity entity, in GroupedNumber value)
    {
        // These are the registered component types of the comparison input.
        Position position = source.World.Get<Position>(entity);
        Velocity velocity = source.World.Get<Velocity>(entity);
        Health health = source.World.Get<Health>(entity);
        Arch.Core.Entity moved = source.World.Has<Sleeping>(entity)
            ? target.World.Create(position, velocity, health, new Team(), new Sleeping(), value)
            : source.World.Has<Team>(entity) ? target.World.Create(position, velocity, health, new Team(), value)
                : target.World.Create(position, velocity, health, value);
        if (source.World.Has<Extra>(entity)) target.World.Add(moved, source.World.Get<Extra>(entity));
        if (source.World.Has<GrowthMarker>(entity)) target.World.Add(moved, source.World.Get<GrowthMarker>(entity));
        source.World.Destroy(entity);
        return moved;
    }
    public void Set(ArchGroupPartition partition, Arch.Core.Entity entity, in GroupedNumber value) => partition.World.Set(entity, value);
    public GroupedNumber Read(ArchGroupPartition partition, Arch.Core.Entity entity) => partition.World.Get<GroupedNumber>(entity);
    public void Destroy(ArchGroupPartition partition, Arch.Core.Entity entity) => partition.World.Destroy(entity);
    public void Visit(ArchGroupPartition partition, ref GroupPass pass)
    {
        foreach (ref var chunk in partition.World.Query(in partition.Query).GetChunkIterator())
            pass.Execute(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count],
                chunk.GetSpan<GroupedNumber>()[..chunk.Count]);
    }
    public void SetExtra(ArchGroupPartition partition, Arch.Core.Entity entity) => partition.World.Add(entity, new Extra { Value = 7 });
    public void RemoveExtra(ArchGroupPartition partition, Arch.Core.Entity entity) => partition.World.Remove<Extra>(entity);
    public int Count(ArchGroupPartition partition) => partition.World.Size;
    public void Dispose(ArchGroupPartition partition) => Arch.Core.World.Destroy(partition.World);
}

internal sealed class DefaultGroupPartition(DefaultEcs.World world, int key, bool grouped = true) : IGroupPartition
{
    internal readonly DefaultEcs.World World = world;
    public int Key => key;
    public bool Grouped => grouped;
}
internal readonly struct DefaultGrouping : IGroupingBackend<DefaultEcs.Entity, DefaultGroupPartition>
{
    public DefaultGroupPartition Create(int key) => new(new DefaultEcs.World(), key);
    public DefaultEcs.Entity Move(DefaultGroupPartition source, DefaultGroupPartition target, DefaultEcs.Entity entity, in GroupedNumber value)
    {
        var moved = entity.CopyTo(target.World);
        moved.Set(value);
        entity.Dispose();
        return moved;
    }
    public void Set(DefaultGroupPartition partition, DefaultEcs.Entity entity, in GroupedNumber value) => entity.Set(value);
    public GroupedNumber Read(DefaultGroupPartition partition, DefaultEcs.Entity entity) => entity.Get<GroupedNumber>();
    public void Destroy(DefaultGroupPartition partition, DefaultEcs.Entity entity) => entity.Dispose();
    public void Visit(DefaultGroupPartition partition, ref GroupPass pass)
        => pass.Execute(partition.World.GetAll<Position>(), partition.World.GetAll<Velocity>(), partition.World.GetAll<GroupedNumber>());
    public void SetExtra(DefaultGroupPartition partition, DefaultEcs.Entity entity) => entity.Set(new Extra { Value = 7 });
    public void RemoveExtra(DefaultGroupPartition partition, DefaultEcs.Entity entity) => entity.Remove<Extra>();
    public int Count(DefaultGroupPartition partition) => partition.World.Count();
    public void Dispose(DefaultGroupPartition partition) => partition.World.Dispose();
}

internal sealed partial class SomeEngineWorld
{
    private Query[]? _groupQueries;
    private SomeEngine.ECS.Entities.Entity[] _groupChangedEntities = [];
    private GroupedNumber[] _groupChangedValues = [];
    private void PrepareGroups(string workload)
    {
        if (_groupQueries is not null) return;
        using var structure = _world.OpenStructureWrite();
        PrepareGroupResult(workload);
        _world.GroupBy<GroupedNumber>();
        _world.Set<GroupedNumber>(_entities, GroupInputs.Initial(Count));
        if (GroupChanges)
        {
            _groupChangedEntities = new SomeEngine.ECS.Entities.Entity[Count / 4];
            _groupChangedValues = new GroupedNumber[Count / 4];
            for (int i = 0; i < _groupChangedEntities.Length; i++) _groupChangedEntities[i] = _entities[i * 4];
        }
        _groupQueries = new Query[8];
        for (int i = 0; i < 8; i++)
            _groupQueries[i] = _world.Query(new QueryDescription().InGroup<GroupedNumber>(4 + i).With<Position>().With<Velocity>());
    }
    internal override void GroupCycle()
    {
        using var structure = _world.OpenStructureWrite();
        BeginGroupCycle();
        if (GroupChanges)
        {
            for (int i = 0; i < _groupChangedValues.Length; i++)
            {
                int ordinal = i * 4;
                _groupChangedValues[i] = new GroupedNumber((ordinal + GroupShift) % 16, ordinal);
            }
            _world.Set<GroupedNumber>(_groupChangedEntities, _groupChangedValues);
        }
        else for (int i = 0; i < Count; i += 4) _world.Set(_entities[i], new GroupedNumber((i + GroupShift) % 16, i));
        GroupProjectionCalls = GroupProjection.Calls;
        for (int i = 0; i < 8; i++)
        {
            var pass = new GroupPass(GroupMembers, GroupMemberCount, i + 4);
            _groupQueries![i].ForEach<Position, Velocity, GroupedNumber, GroupPass>(ref pass);
            FinishGroupPass(in pass);
        }
    }
}

internal sealed partial class EasyEcsWorld
{
    private EasyEcs.QueryFilter[]? _groupQueries;
    private void PrepareGroups(string workload)
    {
        if (_groupQueries is not null) return;
        PrepareGroupResult(workload);
        _world.GroupBy<GroupedNumber>();
        GroupedNumber[] initial = GroupInputs.Initial(Count);
        for (int i = 0; i < Count; i++) _entities[i].Set(initial[i]);
        _groupQueries = new EasyEcs.QueryFilter[8];
        for (int i = 0; i < 8; i++) _groupQueries[i] = EasyEcs.QueryFilter.Create().InGroup<GroupedNumber>(i + 4);
    }
    internal override void GroupCycle()
    {
        BeginGroupCycle();
        for (int i = 0; i < Count; i += 4) _entities[i].Set(new GroupedNumber((i + GroupShift) % 16, i));
        GroupProjectionCalls = GroupProjection.Calls;
        for (int i = 0; i < 8; i++)
        {
            var pass = new GroupPass(GroupMembers, GroupMemberCount, i + 4);
            _world.ForEachBatch<Position, Velocity, GroupedNumber, GroupPass>(ref pass, in _groupQueries![i]);
            FinishGroupPass(in pass);
        }
    }
}

internal sealed partial class FrifloWorld
{
    private ReferenceGrouping<Friflo.Engine.ECS.Entity, FrifloGroupPartition, FrifloGrouping>? _groupedValues;
    private void PrepareGroups(string workload)
    {
        if (_groupedValues is not null) return;
        PrepareGroupResult(workload);
        _groupedValues = new(new FrifloGroupPartition(_world, 0, false), _entities, new FrifloGrouping());
        GroupedNumber[] initial = GroupInputs.Initial(Count);
        for (int i = 0; i < Count; i++) _groupedValues.Set(_groupedValues.Entities[i], initial[i]);
    }
    internal override void GroupCycle()
    {
        BeginGroupCycle();
        for (int i = 0; i < Count; i += 4) _groupedValues!.Set(_groupedValues.Entities[i], new GroupedNumber((i + GroupShift) % 16, i));
        GroupProjectionCalls = GroupProjection.Calls;
        for (int i = 0; i < 8; i++)
        {
            var pass = new GroupPass(GroupMembers, GroupMemberCount, i + 4);
            _groupedValues!.Visit(i + 4, ref pass);
            FinishGroupPass(in pass);
        }
    }
}

internal sealed partial class ArchWorld
{
    private ReferenceGrouping<Arch.Core.Entity, ArchGroupPartition, ArchGrouping>? _groupedValues;
    private void PrepareGroups(string workload)
    {
        if (_groupedValues is not null) return;
        PrepareGroupResult(workload);
        _groupedValues = new(new ArchGroupPartition(_world, 0, false), _entities, new ArchGrouping());
        GroupedNumber[] initial = GroupInputs.Initial(Count);
        for (int i = 0; i < Count; i++) _groupedValues.Set(_groupedValues.Entities[i], initial[i]);
    }
    internal override void GroupCycle()
    {
        BeginGroupCycle();
        for (int i = 0; i < Count; i += 4) _groupedValues!.Set(_groupedValues.Entities[i], new GroupedNumber((i + GroupShift) % 16, i));
        GroupProjectionCalls = GroupProjection.Calls;
        for (int i = 0; i < 8; i++)
        {
            var pass = new GroupPass(GroupMembers, GroupMemberCount, i + 4);
            _groupedValues!.Visit(i + 4, ref pass);
            FinishGroupPass(in pass);
        }
    }
}

internal sealed partial class DefaultEcsWorld
{
    private ReferenceGrouping<DefaultEcs.Entity, DefaultGroupPartition, DefaultGrouping>? _groupedValues;
    private void PrepareGroups(string workload)
    {
        if (_groupedValues is not null) return;
        PrepareGroupResult(workload);
        _groupedValues = new(new DefaultGroupPartition(_world, 0, false), _entities, new DefaultGrouping());
        GroupedNumber[] initial = GroupInputs.Initial(Count);
        for (int i = 0; i < Count; i++) _groupedValues.Set(_groupedValues.Entities[i], initial[i]);
    }
    internal override void GroupCycle()
    {
        BeginGroupCycle();
        for (int i = 0; i < Count; i += 4) _groupedValues!.Set(_groupedValues.Entities[i], new GroupedNumber((i + GroupShift) % 16, i));
        GroupProjectionCalls = GroupProjection.Calls;
        for (int i = 0; i < 8; i++)
        {
            var pass = new GroupPass(GroupMembers, GroupMemberCount, i + 4);
            _groupedValues!.Visit(i + 4, ref pass);
            FinishGroupPass(in pass);
        }
    }
}
