using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Owners;

internal sealed class Entities
{
    private World _world = null!;
    private Tables _tables = null!;
    private RelationGraph _relationGraph = null!;
    private Components _components = null!;
    private Sparse _sparse = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;

    internal Entities(int capacity)
    {
        Store = new EntityStore(capacity);
    }

    internal Entities(EntityStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal EntityStore Store { get; }

    internal void Bind(
        World world,
        Tables tables,
        RelationGraph relationGraph,
        Components components,
        Sparse sparse,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _world = world;
        _tables = tables;
        _relationGraph = relationGraph;
        _components = components;
        _sparse = sparse;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    internal Entity Create()
    {
        var entity = Store.Allocate();
        var (chunk, row) = _tables.AllocateInChunk(_tables.Empty, entity);
        EntityRecordWriter record = Store.GetRecord(entity);
        record.Archetype = _tables.Empty;
        record.Chunk = chunk;
        record.RowInChunk = row;
        return entity;
    }

    internal void DestroyNow(Entity entity)
    {
        _iteration.Throw();
        if (!_relationGraph.Any && !_hierarchy.Any && !_components.HasHooks)
        {
            EntityRecordWriter fastRecord = Row(entity);
            FreeLiveFast(entity, fastRecord, fastRecord.Archetype!);
            return;
        }

        var faults = new ExceptionAccumulator();
        try
        {
            _relationGraph.CleanupEntity(_world, entity);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        try
        {
            _hierarchy.OnEntityDestroying(entity);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        EntityRecordWriter record = Row(entity);
        var archetype = record.Archetype!;
        try
        {
            FreeLive(entity, record, archetype);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        faults.ThrowIfAny();
    }

    internal void Destroy(Entity entity)
    {
        _iteration.Throw();
        if (!_relationGraph.Any && !_hierarchy.Any && !_components.HasHooks)
        {
            EntityRecordWriter fastRecord = Row(entity);
            var fastArchetype = fastRecord.Archetype!;
            if (HasLifecycleCleanup(fastArchetype))
                SoftDestroyFast(entity, fastRecord, fastArchetype);
            else
                FreeLiveFast(entity, fastRecord, fastArchetype);
            return;
        }

        var faults = new ExceptionAccumulator();
        try
        {
            _relationGraph.CleanupEntity(_world, entity);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        try
        {
            _hierarchy.OnEntityDestroying(entity);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        EntityRecordWriter record = Row(entity);
        var archetype = record.Archetype!;

        if (HasLifecycleCleanup(archetype))
        {
            try
            {
                SoftDestroy(entity, record, archetype);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            faults.ThrowIfAny();
            return;
        }

        try
        {
            FreeLive(entity, record, archetype);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
        faults.ThrowIfAny();
    }

    internal bool Alive(Entity entity)
    {
        return Store.IsAlive(entity);
    }

    internal int Count => Store.AliveCount;

    internal bool Pending(Entity entity)
    {
        if (!Store.IsAlive(entity))
            return false;

        EntityRecord record = Store.GetRecordReadOnly(entity);
        if (record.Archetype is null)
            return false;

        return record.PendingDestroy;
    }

    internal void FinishCleanup(
        Entity entity,
        EntityRecordWriter record,
        Archetype sourceArchetype)
    {
        if (!record.PendingDestroy || record.Archetype != _tables.Empty)
            return;

        FreeLive(entity, record, _tables.Empty);
    }

    internal EntityRecordWriter Row(Entity entity)
    {
        EntityRecordWriter record = Store.GetRecord(entity);
        if (record.Archetype is null)
            throw new InvalidOperationException($"Entity {entity} is not alive.");

        return record;
    }

    internal EntityRecord ReadRow(Entity entity)
    {
        EntityRecord record = Store.GetRecordReadOnly(entity);
        if (record.Archetype is null)
            throw new InvalidOperationException($"Entity {entity} is not alive.");

        return record;
    }

    internal void ThrowDead(Entity entity)
    {
        _ = ReadRow(entity);
    }

    private void SoftDestroy(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype)
    {
        record.PendingDestroy = true;
        _sparse.RemoveAll(entity);

        NotifySoft(entity, archetype);
        Exception? componentFault = null;
        try
        {
            _components.RemoveLive(entity, archetype, record.Chunk!, record.RowInChunk);
        }
        catch (Exception exception)
        {
            componentFault = exception;
        }
        var plan = _tables.Registry.CleanupTransition(archetype);
        _tables.MoveEntity(entity, record, plan);
        if (componentFault is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(componentFault).Throw();
    }

    private void SoftDestroyFast(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype)
    {
        record.PendingDestroy = true;
        _sparse.RemoveAll(entity);
        NotifySoft(entity, archetype);
        _components.RemoveLive(entity, archetype, record.Chunk!, record.RowInChunk);
        var plan = _tables.Registry.CleanupTransition(archetype);
        _tables.MoveEntity(entity, record, plan);
    }

    private void FreeLive(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype)
    {
        _sparse.RemoveAll(entity);
        var currentChunk = record.Chunk!;
        NotifyDestroy(entity, archetype);
        Exception? componentFault = null;
        try
        {
            _components.RemoveAll(entity, archetype, currentChunk, record.RowInChunk);
        }
        catch (Exception exception)
        {
            componentFault = exception;
        }

        var movedEntity = currentChunk.RemoveRow(record.RowInChunk, archetype.ColumnOperations);
        if (movedEntity != Entity.Null)
        {
            EntityRecordWriter movedRecord = Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = record.RowInChunk;
        }

        _tables.TryRecycleChunk(archetype, currentChunk);

        record.Archetype = null;
        record.Chunk = null;
        record.RowInChunk = 0;

        Store.Free(entity);
        if (componentFault is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(componentFault).Throw();
    }

    private void FreeLiveFast(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype)
    {
        _sparse.RemoveAll(entity);
        var currentChunk = record.Chunk!;
        NotifyDestroy(entity, archetype);
        _components.RemoveAll(entity, archetype, currentChunk, record.RowInChunk);

        var movedEntity = currentChunk.RemoveRow(record.RowInChunk, archetype.ColumnOperations);
        if (movedEntity != Entity.Null)
        {
            EntityRecordWriter movedRecord = Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = record.RowInChunk;
        }

        _tables.TryRecycleChunk(archetype, currentChunk);
        record.Archetype = null;
        record.Chunk = null;
        record.RowInChunk = 0;
        Store.Free(entity);
    }

    private void NotifySoft(Entity entity, Archetype archetype)
    {
        // A Parent<TDomain> component registers its domain when it enters the World, so an
        // empty registry proves that none of this archetype's columns can participate in a
        // hierarchy. Avoid the component-registration fallback on the ordinary cleanup hot path.
        if (!_hierarchy.Any)
            return;

        for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
        {
            int componentId = archetype.TableComponentIds[columnIndex];
            if (archetype.CleanupComponentIds.BinarySearch(componentId) >= 0)
                continue;

            _hierarchy.TrackParent(entity, componentId);
        }
    }

    private void NotifyDestroy(Entity entity, Archetype archetype)
    {
        if (!_hierarchy.Any)
            return;

        for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
            _hierarchy.TrackParent(entity, archetype.TableComponentIds[columnIndex]);
    }

    private static bool HasLifecycleCleanup(Archetype archetype)
    {
        var cleanupIds = archetype.CleanupComponentIds;
        for (int i = 0; i < cleanupIds.Length; i++)
        {
            if (!Registry.ComponentRegistry.Get(cleanupIds[i]).IsRemovedFact)
                return true;
        }

        return false;
    }
}

