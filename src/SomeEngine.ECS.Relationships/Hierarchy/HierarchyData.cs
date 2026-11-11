using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Hierarchy;

internal struct AppliedParent
{
    internal Entity Parent;
    internal int Place, Generation;
}

internal sealed partial class HierarchyData<TDomain> : IWorldLifecycle where TDomain : struct, IHierarchyDomain
{
    private readonly World _world;
    private readonly DestroyedReader _reader;
    private readonly Query _changes, _removed;
    private AppliedParent[] _applied;
    private readonly Dictionary<Entity, AppliedParent> _retiredApplied = new();
    private readonly Dictionary<Entity, Children<TDomain>> _retiredChildren = new();
    private readonly DifferenceTargets _differences;
    private readonly Dictionary<Entity, List<Entity>> _projected = new();
    private readonly HashSet<Entity> _emptyOrdered = new();
    private ulong _version;
    private bool _hasDeferredChanges;

    private void ConfirmAppliedParents()
    {
        if (!_hasDeferredChanges) _version = _world.Version;
    }

    internal HierarchyData(World world)
    {
        HierarchySerializationType<TDomain>.Register();
        _world = world;
        _applied = new AppliedParent[world.Records.Length];
        _differences = new(world.Records.Length);
        _reader = world.WatchDestroyed();
        _changes = world.Query(new QueryDescription().WithChanged<Parent<TDomain>>(0));
        _removed = world.Query<Removed<Parent<TDomain>>>();
    }

    internal static void RequireActive(World world, Entity entity)
    {
        if (!world.IsAliveCore(entity) || World.HasDestroyed(in world.Records[entity.Id])) throw new ArgumentException("Entity has completed its active lifetime.");
    }

    private bool Active(Entity entity) => _world.IsAliveCore(entity) && !World.HasDestroyed(in _world.Records[entity.Id]);

    internal Entity ReadParent(Entity entity)
        => ReadParent(_world, entity);

    internal static Entity ReadParent(World world, Entity entity)
    {
        if (!world.IsAliveCore(entity)) return Entity.Null;
        return ReadParent(in world.Records[entity.Id]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity ReadParentKnown(Entity entity) => ReadParent(in _world.Records[entity.Id]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Entity ReadParent(in EntityRecord record)
    {
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<Parent<TDomain>>.TypeId;
        return (uint)type < (uint)columns.Length && columns[type].Has(record.Row)
            ? ColumnEntry.At<Parent<TDomain>>(ref columns[type], record.Row).Value : Entity.Null;
    }

    internal Children<TDomain> ReadChildren(Entity entity)
        => ReadChildren(_world, entity);

    internal static Children<TDomain> ReadChildren(World world, Entity entity)
    {
        if (!world.IsAliveCore(entity)) return default;
        ref EntityRecord record = ref world.Records[entity.Id];
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<Children<TDomain>>.TypeId;
        return (uint)type < (uint)columns.Length && columns[type].Has(record.Row)
            ? ColumnEntry.At<Children<TDomain>>(ref columns[type], record.Row) : default;
    }

    internal void Initialize()
    {
        var entities = new List<Entity>();
        using (var scope = _changes.Open())
            foreach (var rows in scope) entities.AddRange(rows.Entities);
        foreach (Entity entity in entities) InitializeChild(entity);
        _version = _world.Version;
    }

    internal void InitializeChild(Entity entity)
    {
        PrepareApplied(entity);
        Entity parent = ReadParent(entity);
        if (parent != Entity.Null && !Active(parent))
        {
            _world.RemoveValue<Parent<TDomain>>(entity);
            parent = Entity.Null;
        }
        UpdateProjected(entity, Entity.Null, parent, false, -1);
        Apply(entity, parent, -1);
        ConfirmAppliedParents();
    }

    private void EnsureCapacity()
    {
        if (_applied.Length < _world.Records.Length)
        {
            Array.Resize(ref _applied, _world.Records.Length);
            _differences.Reserve(_applied.Length);
        }
    }

    private void PrepareApplied(Entity entity)
    {
        EnsureCapacity();
        PrepareAppliedKnown(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareAppliedKnown(Entity entity)
    {
        ref AppliedParent entry = ref _applied[entity.Id];
        if (entry.Generation == entity.Generation) return;
        if (entry.Parent != Entity.Null) _retiredApplied[new(entity.Id, entry.Generation)] = entry;
        entry = new() { Generation = entity.Generation };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private AppliedParent Applied(Entity entity)
        => (uint)entity.Id < (uint)_applied.Length && _applied[entity.Id].Generation == entity.Generation
            ? _applied[entity.Id] : RetiredApplied(entity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private AppliedParent RetiredApplied(Entity entity) => _retiredApplied.GetValueOrDefault(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteApplied(Entity entity, AppliedParent value)
    {
        if (_applied[entity.Id].Generation == entity.Generation) _applied[entity.Id] = value;
        else WriteRetiredApplied(entity, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteRetiredApplied(Entity entity, AppliedParent value) => _retiredApplied[entity] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePlace(Entity entity, int place)
    {
        if (_applied[entity.Id].Generation == entity.Generation) _applied[entity.Id].Place = place;
        else WriteRetiredPlace(entity, place);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteRetiredPlace(Entity entity, int place) => CollectionsMarshal.GetValueRefOrNullRef(_retiredApplied, entity).Place = place;

    private void ValidateForest(Entity child, Entity parent)
    {
        if (parent == child) throw new ArgumentException("A parent link would form a cycle.");
        if (ReadChildren(child).Count == 0 && _differences.Members(child).Length == 0) return;
        for (Entity ancestor = parent; ancestor != Entity.Null && Active(ancestor); ancestor = ReadParent(ancestor))
            if (ancestor == child) throw new ArgumentException("A parent link would form a cycle.");
    }

    internal void SetParent(Entity child, Entity parent, bool deferred, int insertAt)
        => SetParent(child, parent, deferred, insertAt, true);

    private void SetParent(Entity child, Entity parent, bool deferred, int insertAt, bool validate, Entity emptyingParent = default)
    {
        _world.RequireStructure();
        RequireActive(_world, child);
        if (parent != Entity.Null) RequireActive(_world, parent);
        if (validate) ValidateForest(child, parent);
        PrepareApplied(child);
        Entity before = ReadParent(child);
        ValidateInsertion(child, parent, deferred, insertAt);
        UpdateProjected(child, before, parent, deferred, insertAt);
        if (parent == Entity.Null) _world.RemoveValue<Parent<TDomain>>(child);
        else
        {
            ref EntityRecord record = ref _world.Resolve(child);
            _world.SetValueKnown(child, ref record, new Parent<TDomain>(parent));
        }
        if (deferred)
        {
            _hasDeferredChanges = true;
            _differences.Set(child, parent == Applied(child).Parent ? Entity.Null : parent);
        }
        else
        {
            Apply(child, parent, insertAt, emptyingParent: emptyingParent);
            ConfirmAppliedParents();
        }
    }
}
