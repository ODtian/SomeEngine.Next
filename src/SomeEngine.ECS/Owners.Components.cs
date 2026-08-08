using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Components
{
    private World _world = null!;
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Indices _indices = null!;
    private Hooks _hooks = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;
    private RelationGraph _relationGraph = null!;

    internal bool HasHooks => _hooks.Any;

    internal void Bind(
        World world,
        Entities entities,
        Tables tables,
        Indices indices,
        RelationGraph relationGraph,
        Hooks hooks,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _world = world;
        _entities = entities;
        _tables = tables;
        _indices = indices;
        _relationGraph = relationGraph;
        _hooks = hooks;
        _clock = clock;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    internal void Add<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        Add(entity, ComponentMetadata<T>.Id, typeof(T).Name, in value);
    }

    internal void AddTag<T>(Entity entity)
        where T : struct, ITag
    {
        AddTag(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    internal void AddTag(Entity entity, int componentId)
    {
        AddTag(entity, componentId, $"id {componentId}");
    }

    private void AddTag(Entity entity, int componentId, string name)
    {
        _iteration.Throw();
        EntityRecordWriter record = _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has tag {name}.");

        var edge = _tables.Registry.AddEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, record, edge);
    }

    internal void Remove<T>(Entity entity)
        where T : struct, IComponent
    {
        Remove<T>(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    internal void RemoveTag<T>(Entity entity)
        where T : struct, ITag
    {
        RemoveTag(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    internal void RemoveTag(Entity entity, int componentId)
    {
        RemoveTag(entity, componentId, $"id {componentId}");
    }

    private void RemoveTag(Entity entity, int componentId, string name)
    {
        _iteration.Throw();
        EntityRecordWriter record = _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (!sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have tag {name}.");

        var edge = _tables.Registry.RemoveEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, record, edge);
        _entities.FinishCleanup(entity, record, sourceArchetype);
    }

    internal T Read<T>(Entity entity)
        where T : struct, IComponent
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        return record.Chunk!.ReadComponent<T>(columnIndex, record.RowInChunk);
    }

    internal ref readonly T ReadRef<T>(Entity entity)
        where T : struct, IComponent
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        return ref record.Chunk!.GetComponentReadOnlyRef<T>(columnIndex, record.RowInChunk);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        Replace(entity, in value, _clock.Tick);
    }

    internal void Replace<T>(Entity entity, in T value, uint version)
        where T : struct, IComponent
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        var chunk = record.Chunk!;
        var currentValue = chunk.ReadComponent<T>(columnIndex, record.RowInChunk);
        WriteExisting(
            entity,
            chunk,
            record.RowInChunk,
            columnIndex,
            in currentValue,
            in value,
            version);
    }

    internal bool Has<T>(Entity entity)
        where T : struct
    {
        if (!_entities.Store.IsAlive(entity))
            return false;

        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        return record.Archetype is not null && record.Archetype.HasComponent(ComponentMetadata<T>.Id);
    }

    private void Add<T>(Entity entity, int componentId, string name, in T value)
        where T : struct, IComponent
    {
        _iteration.Throw();
        EntityRecordWriter record = _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has component {name}.");

        _hierarchy.TrackParent<T>(entity);

        MoveAndWriteAdded(entity, record, sourceArchetype, componentId, in value);
    }

    private void MoveAndWriteAdded<T>(
        Entity entity,
        EntityRecordWriter record,
        Archetypes.Archetype sourceArchetype,
        int componentId,
        in T value)
        where T : struct, IComponent
    {
        var edge = _tables.Registry.AddEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, record, edge);

        var destinationArchetype = record.Archetype!;
        var destinationChunk = record.Chunk!;
        int columnIndex = destinationArchetype.Column(componentId);
        WriteAdded(entity, destinationArchetype, destinationChunk, record.RowInChunk, columnIndex, in value);
    }

    private void Remove<T>(
        Entity entity,
        int componentId,
        string name)
        where T : struct, IComponent
    {
        _iteration.Throw();
        EntityRecordWriter record = _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (!sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int columnIndex = sourceArchetype.Column(componentId);
        var removedValue = record.Chunk!.ReadComponent<T>(columnIndex, record.RowInChunk);
        _hierarchy.TrackParent<T>(entity);
        _indices.Drop(entity, in removedValue);
        MoveAndCommitRemoved(entity, record, sourceArchetype, componentId, in removedValue);
    }

    private void MoveAndCommitRemoved<T>(
        Entity entity,
        EntityRecordWriter record,
        Archetypes.Archetype sourceArchetype,
        int componentId,
        in T removedValue)
        where T : struct, IComponent
    {
        var edge = _tables.Registry.RemoveEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, record, edge);
        CommitRemove(entity, in removedValue);
        WriteRemoved(entity, in removedValue);
        _entities.FinishCleanup(entity, record, sourceArchetype);
    }
}

internal sealed partial class Components
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CommitAdd<T>(
        Entity entity,
        Archetypes.Archetype archetype,
        Archetypes.Chunk chunk,
        int columnIndex,
        int row)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (ComponentMetadata<T>.IsEnableable)
        {
            int maskIndex = archetype.EnableMask(componentId);
            chunk.WriteEnabled(maskIndex, row, true);
        }

        _indices.Fix(entity, componentId, chunk, columnIndex, row);
        if (_hooks.Any)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            ref byte value = ref chunk.ComponentRowReference(
                columnIndex,
                row,
                in info.Operations);
            _hooks.Add(componentId, entity, ref value);
            _hooks.Insert(componentId, entity, ref value);
        }
    }

    internal void CommitAdd(
        Entity entity,
        int componentId,
        Chunk chunk,
        int column,
        int row)
    {
        _indices.Fix(entity, componentId, chunk, column, row);
        if (_hooks.Any)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            ref byte value = ref chunk.ComponentRowReference(
                column,
                row,
                in info.Operations);
            _hooks.Add(componentId, entity, ref value);
            _hooks.Insert(componentId, entity, ref value);
        }
    }

    internal void CommitReplace<T>(Entity entity, in T oldValue, in T newValue)
        where T : struct, IComponent
    {
        CommitReplace(entity, in oldValue, in newValue, _clock.Tick);
    }

    internal void CommitReplace<T>(
        Entity entity,
        in T oldValue,
        in T newValue,
        uint version)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        _indices.Fix(entity, in oldValue, in newValue);
        if (_hooks.Any)
        {
            _hooks.Replace(entity, in oldValue);
            _hooks.Insert(entity, in newValue);
        }
    }

    internal unsafe void CommitReplace(
        int componentId,
        Entity entity,
        Array ownedOldValueSnapshot,
        Chunk chunk,
        int column,
        int row)
    {
        _indices.Fix(entity, componentId, chunk, column, row);
        if (_hooks.Any)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            ref byte oldValue =
                ref info.Operations.GetReference(ownedOldValueSnapshot, 0);
            ref byte newValue = ref chunk.ComponentRowReference(
                column,
                row,
                in info.Operations);
            _hooks.Replace(componentId, entity, ref oldValue);
            _hooks.Insert(componentId, entity, ref newValue);
        }
    }

    internal void CommitRemove<T>(Entity entity, in T oldValue)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (_hooks.Any)
        {
            _hooks.Replace(entity, in oldValue);
            _hooks.Remove(entity, in oldValue);
        }
    }

    internal unsafe void CommitRemove(
        int componentId,
        Entity entity,
        Array ownedOldValueSnapshot)
    {
        if (_hooks.Any)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            ref byte oldValue =
                ref info.Operations.GetReference(ownedOldValueSnapshot, 0);
            _hooks.Replace(componentId, entity, ref oldValue);
            _hooks.Remove(componentId, entity, ref oldValue);
        }
    }

    internal void WriteAdded<T>(
        Entity entity,
        Archetypes.Archetype archetype,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex,
        in T value)
        where T : struct, IComponent
    {
        _hierarchy.TrackParent<T>(entity);
        chunk.WriteComponent(columnIndex, row, value);
        MarkAdd(chunk, columnIndex, row);
        CommitAdd<T>(entity, archetype, chunk, columnIndex, row);
    }

    internal void WriteExisting<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex,
        in T value)
        where T : struct, IComponent
    {
        var currentValue = chunk.ReadComponent<T>(columnIndex, row);
        WriteExisting(entity, chunk, row, columnIndex, in currentValue, in value);
    }

    internal void WriteExisting<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex,
        in T oldValue,
        in T newValue)
        where T : struct, IComponent
    {
        WriteExisting(
            entity,
            chunk,
            row,
            columnIndex,
            in oldValue,
            in newValue,
            _clock.Tick);
    }

    internal void WriteExisting<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex,
        in T oldValue,
        in T newValue,
        uint version)
        where T : struct, IComponent
    {
        _hierarchy.TrackParent<T>(entity);
        chunk.WriteComponent(columnIndex, row, newValue);
        MarkWrite(chunk, columnIndex, row, version);
        CommitReplace(entity, in oldValue, in newValue, version);
    }

    internal ref T WriteRef<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex)
        where T : struct
    {
        return ref WriteRef<T>(entity, chunk, row, columnIndex, _clock.Tick);
    }

    internal ref T WriteRef<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex,
        uint version)
        where T : struct
    {
        if (ComponentMetadata<T>.IsRelationshipSource ||
            ComponentMetadata<T>.IsRelationshipTarget)
        {
            _world.RequireRelationshipWriteOwner();
        }

        MarkWrite(chunk, columnIndex, row, version);
        _hierarchy.TrackParent<T>(entity);
        _relationGraph.TrackEndpoint<T>(_world, entity);
        _indices.Dirty<T>();
        return ref chunk.GetComponentRef<T>(columnIndex, row);
    }

    internal Span<T> WriteChunk<T>(
        Archetypes.Chunk chunk,
        int columnIndex)
        where T : struct
    {
        WriteChunk(chunk, columnIndex, ComponentMetadata<T>.Id, _clock.Tick);
        return chunk.ComponentRows<T>(columnIndex)[..chunk.Count];
    }

    internal Span<T> WriteChunk<T>(
        Archetypes.Chunk chunk,
        int columnIndex,
        uint version)
        where T : struct
    {
        WriteChunk(chunk, columnIndex, ComponentMetadata<T>.Id, version);
        return chunk.ComponentRows<T>(columnIndex)[..chunk.Count];
    }

    internal void WriteChunk(
        Archetypes.Chunk chunk,
        int columnIndex,
        int componentId)
    {
        WriteChunk(chunk, columnIndex, componentId, _clock.Tick);
    }

    internal void WriteChunk(
        Archetypes.Chunk chunk,
        int columnIndex,
        int componentId,
        uint version)
    {
        ref readonly var info = ref ComponentRegistry.Get(componentId);
        if (info.IsRelationshipSource || info.IsRelationshipTarget)
            _world.RequireRelationshipWriteOwner();

        chunk.MarkWriteRange(columnIndex, 0, chunk.Count, version);
        _hierarchy.RequireScan(componentId);
        _relationGraph.TrackEndpointRange(
            _world,
            chunk.Entities[..chunk.Count],
            componentId);
        _indices.Dirty(componentId);
    }

    internal bool IsEnabled<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        return IsEnabled(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    internal bool IsEnabled(Entity entity, int componentId)
    {
        return IsEnabled(entity, componentId, $"id {componentId}");
    }

    internal void WriteEnabled<T>(Entity entity, bool enabled)
        where T : struct, IEnableableComponent
    {
        WriteEnabled(entity, ComponentMetadata<T>.Id, enabled, typeof(T).Name);
    }

    internal void WriteEnabled(Entity entity, int componentId, bool enabled)
    {
        WriteEnabled(entity, componentId, enabled, $"id {componentId}");
    }
}

internal sealed partial class Components
{
    internal void ClearRemoved<T>(uint throughVersion)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<Removed<T>>.Id;
        List<Entity>? entities = null;

        foreach (var archetype in _tables.All)
        {
            if (!archetype.TryColumn(componentId, out int columnIndex))
                continue;

            foreach (var chunk in archetype.Chunks)
            {
                for (int row = 0; row < chunk.Count; row++)
                {
                    var removed = chunk.ReadComponent<Removed<T>>(columnIndex, row);
                    if (VersionClock.IsNewer(removed.Version, throughVersion))
                        continue;

                    entities ??= new List<Entity>();
                    entities.Add(chunk.Entities[row]);
                }
            }
        }

        if (entities is null)
            return;

        for (int i = 0; i < entities.Count; i++)
        {
            if (_entities.Alive(entities[i]) && Has<Removed<T>>(entities[i]))
                Remove<Removed<T>>(entities[i]);
        }
    }

    internal void RemoveAll(
        Entity entity,
        Archetypes.Archetype archetype,
        Archetypes.Chunk chunk,
        int row)
    {
        if (!_hooks.Any)
        {
            for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
            {
                int componentId = archetype.TableComponentIds[columnIndex];
                _indices.Drop(entity, componentId, chunk, columnIndex, row);
            }
            return;
        }

        var faults = new ExceptionAccumulator();
        for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
        {
            int componentId = archetype.TableComponentIds[columnIndex];
            try
            {
                _indices.Drop(entity, componentId, chunk, columnIndex, row);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            try
            {
                ref readonly ComponentOperations operations =
                    ref archetype.ColumnOperations[columnIndex];
                ref byte value = ref chunk.ComponentRowReference(
                    columnIndex,
                    row,
                    in operations);
                _hooks.Replace(componentId, entity, ref value);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            try
            {
                ref readonly ComponentOperations operations =
                    ref archetype.ColumnOperations[columnIndex];
                ref byte value = ref chunk.ComponentRowReference(
                    columnIndex,
                    row,
                    in operations);
                _hooks.Remove(componentId, entity, ref value);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            try
            {
                ref readonly ComponentOperations operations =
                    ref archetype.ColumnOperations[columnIndex];
                ref byte value = ref chunk.ComponentRowReference(
                    columnIndex,
                    row,
                    in operations);
                _hooks.Despawn(componentId, entity, ref value);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
        }
        faults.ThrowIfAny();
    }

    internal void RemoveLive(
        Entity entity,
        Archetypes.Archetype archetype,
        Archetypes.Chunk chunk,
        int row)
    {
        if (!_hooks.Any)
        {
            for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
            {
                int componentId = archetype.TableComponentIds[columnIndex];
                if (archetype.CleanupComponentIds.BinarySearch(componentId) >= 0)
                    continue;
                _indices.Drop(entity, componentId, chunk, columnIndex, row);
            }
            return;
        }

        var faults = new ExceptionAccumulator();
        for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
        {
            int componentId = archetype.TableComponentIds[columnIndex];
            if (archetype.CleanupComponentIds.BinarySearch(componentId) >= 0)
                continue;

            try
            {
                _indices.Drop(entity, componentId, chunk, columnIndex, row);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            try
            {
                ref readonly ComponentOperations operations =
                    ref archetype.ColumnOperations[columnIndex];
                ref byte value = ref chunk.ComponentRowReference(
                    columnIndex,
                    row,
                    in operations);
                _hooks.Replace(componentId, entity, ref value);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
            try
            {
                ref readonly ComponentOperations operations =
                    ref archetype.ColumnOperations[columnIndex];
                ref byte value = ref chunk.ComponentRowReference(
                    columnIndex,
                    row,
                    in operations);
                _hooks.Remove(componentId, entity, ref value);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
        }
        faults.ThrowIfAny();
    }

    private bool IsEnabled(Entity entity, int componentId, string name)
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int maskIndex = archetype.EnableMask(componentId);
        return record.Chunk!.IsEnabled(maskIndex, record.RowInChunk);
    }

    private void WriteEnabled(Entity entity, int componentId, bool enabled, string name)
    {
        EntityRecord record = _entities.ReadRow(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int maskIndex = archetype.EnableMask(componentId);
        record.Chunk!.WriteEnabled(maskIndex, record.RowInChunk, enabled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkAdd(Archetypes.Chunk chunk, int columnIndex, int row)
    {
        chunk.MarkAdd(columnIndex, row, _clock.Tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkWrite(Archetypes.Chunk chunk, int columnIndex, int row)
    {
        chunk.MarkWrite(columnIndex, row, _clock.Tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkWrite(
        Archetypes.Chunk chunk,
        int columnIndex,
        int row,
        uint version)
    {
        chunk.MarkWrite(columnIndex, row, version);
    }

    private void WriteRemoved<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (ComponentMetadata<T>.IsCleanup)
            return;

        var removed = new Removed<T>
        {
            Value = value,
            Version = _clock.Tick,
        };

        // Removed<T> is a retained, coalesced ECS fact. A component can be removed, re-added,
        // and removed again before consumers clear the earlier fact; refresh that fact instead
        // of trying to add a duplicate cleanup component.
        if (Has<Removed<T>>(entity))
            Replace(entity, in removed);
        else
            Add(entity, in removed);
    }
}


