using Entity = SomeEngine.ECS.Entities.Entity;
using Friflo.Engine.ECS;
using SomeEngine.Job;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

public readonly record struct ArchiveIdentity(Entity Value) : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;

internal interface INativeArchiveVisitor<T> where T : struct
{
    void Visit(ReadOnlySpan<ArchiveIdentity> identities, Span<T> values);
}

internal abstract class NativeArchiveStore(bool supportsDirectGroups = false) : IDisposable
{
    internal int[] Generations = new int[16];
    internal bool[] AliveIds = new bool[16];
    internal readonly List<int> FreeIds = new();
    internal int Used, Count;
    internal ulong Revision;
    internal ulong DestroyedSequence;
    internal readonly List<DestroyedEntity> Destroyed = new();
    internal NativeArchiveType[] RowTypes = null!;
    internal virtual bool HasNativeIndexes => false;
    internal virtual int IndexCount(int key, bool shared) => 0;
    internal readonly bool SupportsDirectGroups = supportsDirectGroups;
    protected bool DirectRestore;

    internal Entity Create(ulong componentTypes = 0)
    {
        int id;
        if (FreeIds.Count != 0) { id = FreeIds[^1]; FreeIds.RemoveAt(FreeIds.Count - 1); }
        else id = ++Used;
        Ensure(id + 1);
        if (Generations[id] == 0) Generations[id] = 1;
        Entity entity = new(id, Generations[id]);
        if (componentTypes == 0 || !SupportsDirectGroups) CreateNative(entity);
        else CreateNative(entity, componentTypes);
        AliveIds[id] = true; Count++;
        Revision++;
        return entity;
    }
    protected void Ensure(int capacity)
    {
        if (capacity <= Generations.Length) return;
        capacity = Math.Max(capacity, Generations.Length * 2);
        Array.Resize(ref Generations, capacity); Array.Resize(ref AliveIds, capacity); GrowNative(capacity);
    }
    internal bool IsAlive(Entity entity) => (uint)entity.Id < (uint)AliveIds.Length && AliveIds[entity.Id]
        && Generations[entity.Id] == entity.Generation && NativeAlive(entity.Id);
    internal void Destroy(Entity entity)
    {
        DestroyNative(entity.Id); AliveIds[entity.Id] = false; Generations[entity.Id]++; Count--; FreeIds.Add(entity.Id);
        Destroyed.Add(new(entity, ++DestroyedSequence));
        Revision++;
    }
    internal void RestoreIdentities(int[] generations, bool[] alive, int used, ReadOnlySpan<int> free,
        IReadOnlyList<NativeArchiveGroup> groups, bool disjointGroups, NativeArchiveType[] types)
    {
        int finalUsed = Math.Max(Used, used); Ensure(finalUsed + 1);
        Revision++;
        for (int id = 1; id <= finalUsed; id++)
            Generations[id] = id <= used && alive[id] ? generations[id]
                : Math.Max(id <= used ? generations[id] : 0, Generations[id] + (AliveIds[id] ? 1 : 0));
        ClearNative(); Array.Clear(AliveIds); Used = finalUsed; Count = 0;
        FreeIds.Clear(); for (int id = finalUsed; id > used; id--) FreeIds.Add(id);
        foreach (int id in free) FreeIds.Add(id);
        DirectRestore = SupportsDirectGroups && disjointGroups;
        if (DirectRestore) foreach (NativeArchiveGroup group in groups) CreateGroup(group, types);
        for (int id = 1; id <= used; id++) if (alive[id] && !AliveIds[id])
        { CreateNative(new(id, Generations[id])); AliveIds[id] = true; Count++; }
    }
    protected virtual void CreateGroup(NativeArchiveGroup group, NativeArchiveType[] types) { }
    protected void Created(Entity entity) { AliveIds[entity.Id] = true; Count++; }
    internal virtual void WriteColumn<T>(NativeArchiveGroup group, ReadOnlySpan<T> values, Func<T, T>? clone)
        where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    {
        for (int i = 0; i < values.Length; i++) Set(group.Identities[i].Value, clone is null ? values[i] : clone(values[i]));
    }
    internal virtual void ReadColumn<T, TCodec>(NativeArchiveGroup group, int count, ref DataReader reader, TCodec codec, bool raw)
        where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TCodec : struct, IValueCodec<T>
    {
        var values = new T[count]; NativeArchiveColumns.Read(values.AsSpan(), ref reader, codec, raw);
        WriteColumn<T>(group, values.AsSpan(), null);
    }
    internal void ClearWorld()
    {
        ClearNative(); FreeIds.Clear();
        for (int id = Used; id > 0; id--)
        { if (AliveIds[id]) Generations[id]++; FreeIds.Add(id); }
        Array.Clear(AliveIds); Count = 0; Destroyed.Clear(); Revision++;
    }
    internal void CloseIdentities()
    { Generations = []; AliveIds = []; FreeIds.Clear(); Destroyed.Clear(); Used = Count = 0; Revision++; }
    internal abstract void CreateNative(Entity entity);
    internal virtual void CreateNative(Entity entity, ulong componentTypes) => CreateNative(entity);
    internal abstract bool NativeAlive(int id);
    internal abstract void DestroyNative(int id);
    internal abstract void GrowNative(int capacity);
    internal abstract void ClearNative();
    internal abstract bool Has<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    internal abstract T Read<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    internal abstract void Set<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    internal abstract void Remove<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    internal abstract void Visit<T, TVisitor>(ref TVisitor visitor)
        where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TVisitor : struct, INativeArchiveVisitor<T>;
    internal abstract MixedLifecycleJobs ConnectLifecycleJobs(Scheduler scheduler, Func<ulong> version, int count);
    internal abstract TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler, NativeTrackedContext context);
    internal abstract ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler, Func<ulong> version);
    public abstract void Dispose();
}

internal sealed partial class EasyArchiveStore : NativeArchiveStore
{
    private EasyEcs.World _world = new();
    private EasyEcs.Entity[] _entities = new EasyEcs.Entity[16];
    private EasyEcs.ValueIndex<IndexedNumber, int> _index;
    private EasyEcs.ValueIndex<SharedNumberId, int> _groups;
    internal EasyArchiveStore() : base(true) { _index = _world.Index<IndexedNumber, int>(); _groups = _world.Index<SharedNumberId, int>(); }
    internal override bool HasNativeIndexes => true;
    internal override int IndexCount(int key, bool shared)
    {
        int count = 0;
        if (shared) foreach (var entity in _groups.Find(key)) count++;
        else foreach (var entity in _index.Find(key)) count++;
        return count;
    }
    internal override void GrowNative(int capacity) => Array.Resize(ref _entities, capacity);
    internal override void CreateNative(Entity entity) => _entities[entity.Id] = _world.CreateEntity(new ArchiveIdentity(entity));
    internal override bool NativeAlive(int id) => _entities[id].IsAlive;
    internal override void DestroyNative(int id) => _entities[id].Destroy();
    internal override void ClearNative() => _world.DestroyEntities<ArchiveIdentity>();
    internal override bool Has<T>(Entity entity) => HasValue<T>(entity);
    internal bool HasValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => IsAlive(entity) && _entities[entity.Id].Has<T>();
    internal override T Read<T>(Entity entity) => ReadValue<T>(entity);
    internal T ReadValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => _entities[entity.Id].Read<T>();
    internal override void Set<T>(Entity entity, in T value) => SetValue(entity, value);
    internal void SetValue<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].Set(value); Revision++; }
    internal override void Remove<T>(Entity entity) => RemoveValue<T>(entity);
    internal void RemoveValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].Remove<T>(); Revision++; }
    private struct Visitor<T, TVisitor>(TVisitor visitor) : EasyEcs.IForEachBatch<ArchiveIdentity, T>
        where T : struct, EasyEcs.IComponent where TVisitor : struct, INativeArchiveVisitor<T>
    {
        internal TVisitor Value = visitor;
        public static int Writes => 0;
        public void Execute(EasyEcs.EntitySpan entities, Span<ArchiveIdentity> identities, Span<T> values) => Value.Visit(identities, values);
    }
    internal override void Visit<T, TVisitor>(ref TVisitor visitor)
    {
        var query = new Visitor<T, TVisitor>(visitor);
        _world.ForEachBatch<ArchiveIdentity, T, Visitor<T, TVisitor>>(ref query);
        visitor = query.Value;
    }
    public override void Dispose()
    { _world.Dispose(); _world = null!; _entities = []; _index = default!; _groups = default!; _restoreTypes.Clear(); }
    internal override MixedLifecycleJobs ConnectLifecycleJobs(Scheduler scheduler, Func<ulong> version, int count)
        => new NativeMixedLifecycleJobs<EasyMotionRange, EasyMotionQuery>(scheduler, new(_world), version, count);
    internal EasyTrackedReads TrackedReads() => new(new(this), _entities);
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler, NativeTrackedContext context)
        => new NativeTrackedArchiveJobs<EasyTrackedRange, EasyTrackedQuery>(scheduler, new(_world, this), context);
}

internal sealed partial class FrifloArchiveStore : NativeArchiveStore
{
    private EntityStore _world = new();
    private Friflo.Engine.ECS.Entity[] _entities = new Friflo.Engine.ECS.Entity[16];
    private ComponentIndex<IndexedNumber, int> _index;
    private ComponentIndex<SharedNumberId, int> _groups;
    internal FrifloArchiveStore() : base(true) { _index = _world.ComponentIndex<IndexedNumber, int>(); _groups = _world.ComponentIndex<SharedNumberId, int>(); }
    internal override bool HasNativeIndexes => true;
    internal override int IndexCount(int key, bool shared)
    {
        int count = 0;
        if (shared) foreach (var entity in _groups[key]) count++;
        else foreach (var entity in _index[key]) count++;
        return count;
    }
    internal override void GrowNative(int capacity) => Array.Resize(ref _entities, capacity);
    internal override void CreateNative(Entity entity) => _entities[entity.Id] = _world.CreateEntity(new ArchiveIdentity(entity));
    internal override bool NativeAlive(int id) => !_entities[id].IsNull;
    internal override void DestroyNative(int id) => _entities[id].DeleteEntity();
    internal override void ClearNative()
    { for (int id = 1; id <= Used; id++) if (!_entities[id].IsNull) _entities[id].DeleteEntity(); }
    internal override bool Has<T>(Entity entity) => HasValue<T>(entity);
    internal bool HasValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => IsAlive(entity) && _entities[entity.Id].HasComponent<T>();
    internal override T Read<T>(Entity entity) => ReadValue<T>(entity);
    internal T ReadValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => _entities[entity.Id].GetComponent<T>();
    internal override void Set<T>(Entity entity, in T value) => SetValue(entity, value);
    internal void SetValue<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].AddComponent(value); Revision++; }
    internal override void Remove<T>(Entity entity) => RemoveValue<T>(entity);
    internal void RemoveValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].RemoveComponent<T>(); Revision++; }
    internal override void Visit<T, TVisitor>(ref TVisitor visitor)
    {
        foreach (var (identities, values, _) in _world.Query<ArchiveIdentity, T>().Chunks) visitor.Visit(identities.Span, values.Span);
    }
    public override void Dispose()
    { ClearNative(); _world = null!; _entities = []; _index = default!; _groups = default!; _restoreTypes.Clear(); }
    internal override MixedLifecycleJobs ConnectLifecycleJobs(Scheduler scheduler, Func<ulong> version, int count)
        => new NativeMixedLifecycleJobs<FrifloMotionRange, FrifloMotionQuery>(scheduler, new(_world, _world.Query<Position, Velocity>()), version, count);
    internal FrifloTrackedReads TrackedReads() => new(new(this), _entities);
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler, NativeTrackedContext context)
        => new NativeTrackedArchiveJobs<FrifloTrackedRange, FrifloTrackedQuery>(scheduler, new(_world.Query<SaveCounter, Position, SaveNativeVersions>(), this), context);
}

internal sealed partial class ArchArchiveStore : NativeArchiveStore
{
    internal ArchArchiveStore() : base(true) { }
    private Arch.Core.World _world = Arch.Core.World.Create();
    private Arch.Core.Entity[] _entities = new Arch.Core.Entity[16];
    internal override void GrowNative(int capacity) => Array.Resize(ref _entities, capacity);
    internal override void CreateNative(Entity entity) => _entities[entity.Id] = _world.Create(new ArchiveIdentity(entity));
    internal override bool NativeAlive(int id) => _world.IsAlive(_entities[id]);
    internal override void DestroyNative(int id) => _world.Destroy(_entities[id]);
    internal override void ClearNative() => _world.Clear();
    internal override bool Has<T>(Entity entity) => HasValue<T>(entity);
    internal bool HasValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => IsAlive(entity) && _world.Has<T>(_entities[entity.Id]);
    internal override T Read<T>(Entity entity) => ReadValue<T>(entity);
    internal T ReadValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => _world.Get<T>(_entities[entity.Id]);
    internal override void Set<T>(Entity entity, in T value) => SetValue(entity, value);
    internal void SetValue<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { if (_world.Has<T>(_entities[entity.Id])) _world.Set(_entities[entity.Id], value); else _world.Add(_entities[entity.Id], value); Revision++; }
    internal override void Remove<T>(Entity entity) => RemoveValue<T>(entity);
    internal void RemoveValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _world.Remove<T>(_entities[entity.Id]); Revision++; }
    internal override void Visit<T, TVisitor>(ref TVisitor visitor)
    {
        var query = new Arch.Core.QueryDescription().WithAll<ArchiveIdentity, T>();
        foreach (ref var chunk in _world.Query(in query).GetChunkIterator())
            visitor.Visit(chunk.GetSpan<ArchiveIdentity>()[..chunk.Count], chunk.GetSpan<T>()[..chunk.Count]);
    }
    public override void Dispose()
    { Arch.Core.World.Destroy(_world); _world = null!; _entities = []; _restoreTypes.Clear(); }
    internal override MixedLifecycleJobs ConnectLifecycleJobs(Scheduler scheduler, Func<ulong> version, int count)
        => new NativeMixedLifecycleJobs<ArchMotionRange, ArchMotionQuery>(scheduler, new(_world, new Arch.Core.QueryDescription().WithAll<Position, Velocity>()), version, count);
    internal ArchTrackedReads TrackedReads() => new(new(this), _world, _entities);
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler, NativeTrackedContext context)
        => new NativeTrackedArchiveJobs<ArchTrackedRange, ArchTrackedQuery>(scheduler, new(_world, this), context);
}

internal sealed partial class DefaultArchiveStore : NativeArchiveStore
{
    private DefaultEcs.World _world = new();
    private DefaultEcs.Entity[] _entities = new DefaultEcs.Entity[16];
    private readonly Dictionary<Type, DefaultEcs.EntitySet> _queries = new();
    internal override void GrowNative(int capacity) => Array.Resize(ref _entities, capacity);
    internal override void CreateNative(Entity entity) { var native = _world.CreateEntity(); native.Set(new ArchiveIdentity(entity)); _entities[entity.Id] = native; }
    internal override bool NativeAlive(int id) => _entities[id].IsAlive;
    internal override void DestroyNative(int id) => _entities[id].Dispose();
    internal override void ClearNative() { foreach (var entity in _world) entity.Dispose(); }
    internal override bool Has<T>(Entity entity) => HasValue<T>(entity);
    internal bool HasValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => IsAlive(entity) && _entities[entity.Id].Has<T>();
    internal override T Read<T>(Entity entity) => ReadValue<T>(entity);
    internal T ReadValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => _entities[entity.Id].Get<T>();
    internal override void Set<T>(Entity entity, in T value) => SetValue(entity, value);
    internal void SetValue<T>(Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].Set(value); Revision++; }
    internal override void Remove<T>(Entity entity) => RemoveValue<T>(entity);
    internal void RemoveValue<T>(Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { _entities[entity.Id].Remove<T>(); Revision++; }
    internal override void Visit<T, TVisitor>(ref TVisitor visitor)
    {
        if (!_queries.TryGetValue(typeof(T), out var query))
            _queries.Add(typeof(T), query = _world.GetEntities().With<ArchiveIdentity>().With<T>().AsSet());
        var identities = _world.GetComponents<ArchiveIdentity>(); var values = _world.GetComponents<T>();
        Span<T> all = _world.GetAll<T>();
        if (all.Length == query.GetEntities().Length)
        {
            var ordered = System.Buffers.ArrayPool<ArchiveIdentity>.Shared.Rent(all.Length);
            try
            {
                ref T first = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(all);
                foreach (ref readonly var entity in query.GetEntities())
                {
                    int row = (int)(System.Runtime.CompilerServices.Unsafe.ByteOffset(ref first, ref values[entity])
                        / System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
                    ordered[row] = identities[entity];
                }
                if (all.Length > 0) visitor.Visit(ordered.AsSpan(0, all.Length), all);
            }
            finally { System.Buffers.ArrayPool<ArchiveIdentity>.Shared.Return(ordered); }
            return;
        }
        var idBuffer = System.Buffers.ArrayPool<ArchiveIdentity>.Shared.Rent(1024);
        var valueBuffer = System.Buffers.ArrayPool<T>.Shared.Rent(1024);
        try
        {
            int count = 0;
            foreach (ref readonly var entity in query.GetEntities())
            {
                idBuffer[count] = identities[entity]; valueBuffer[count++] = values[entity];
                if (count == 1024) { visitor.Visit(idBuffer.AsSpan(0, count), valueBuffer.AsSpan(0, count)); count = 0; }
            }
            if (count != 0) visitor.Visit(idBuffer.AsSpan(0, count), valueBuffer.AsSpan(0, count));
        }
        finally
        {
            System.Buffers.ArrayPool<ArchiveIdentity>.Shared.Return(idBuffer);
            System.Buffers.ArrayPool<T>.Shared.Return(valueBuffer, System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }
    public override void Dispose()
    { foreach (var query in _queries.Values) query.Dispose(); _queries.Clear(); _world.Dispose(); _world = null!; _entities = []; }
    internal override MixedLifecycleJobs ConnectLifecycleJobs(Scheduler scheduler, Func<ulong> version, int count)
        => new NativeMixedLifecycleJobs<DefaultMotionRange, DefaultMotionQuery>(scheduler, new(_world), version, count);
    internal DefaultTrackedReads TrackedReads()
        => new(new(this), _entities, _world.GetComponents<ReferenceParent<Entity>>());
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler, NativeTrackedContext context)
        => new NativeTrackedArchiveJobs<DefaultTrackedRange, DefaultTrackedQuery>(scheduler,
            new(_world, _world.GetEntities().With<SaveCounter>().With<Position>().With<SaveNativeVersions>().AsSet(), this), context);
}
