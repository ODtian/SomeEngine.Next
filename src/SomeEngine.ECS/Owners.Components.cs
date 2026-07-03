using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Components
{
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Indices _indices = null!;
    private Hooks _hooks = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;

    internal void Bind(
        Entities entities,
        Tables tables,
        Indices indices,
        Hooks hooks,
        Journal journal,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _entities = entities;
        _tables = tables;
        _indices = indices;
        _hooks = hooks;
        _journal = journal;
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
        ref var record = ref _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has tag {name}.");

        var edge = _tables.Registry.AddEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, ref record, edge);
        Write(SerializationChangeKind.TagAdded, entity, componentId);
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
        ref var record = ref _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (!sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have tag {name}.");

        var edge = _tables.Registry.RemoveEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, ref record, edge);
        _entities.FinishCleanup(entity, ref record, sourceArchetype);
        Write(SerializationChangeKind.TagRemoved, entity, componentId);
    }

    internal ref T Get<T>(Entity entity)
        where T : struct, IComponent
    {
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        _hierarchy.TrackParent<T>(entity);
        MarkWrite(record.Chunk!, columnIndex, record.RowInChunk);
        _indices.Dirty<T>();
        if (!_hierarchy.IsEditing)
            Write(SerializationChangeKind.ComponentChanged, entity, componentId);

        return ref record.Chunk!.GetComponentRef<T>(columnIndex, record.RowInChunk);
    }

    internal T Read<T>(Entity entity)
        where T : struct, IComponent
    {
        ref var record = ref _entities.Row(entity);
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
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        return ref record.Chunk!.GetComponentRef<T>(columnIndex, record.RowInChunk);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        int componentId = ComponentMetadata<T>.Id;

        if (!archetype.TryColumn(componentId, out int columnIndex))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");

        var chunk = record.Chunk!;
        var currentValue = chunk.ReadComponent<T>(columnIndex, record.RowInChunk);
        WriteExisting(entity, chunk, record.RowInChunk, columnIndex, in currentValue, in value);
    }

    internal bool Has<T>(Entity entity)
        where T : struct
    {
        if (!_entities.Store.IsAlive(entity))
            return false;

        ref var record = ref _entities.Store.GetRecord(entity);
        return record.Archetype is not null && record.Archetype.HasComponent(ComponentMetadata<T>.Id);
    }

    private void Add<T>(Entity entity, int componentId, string name, in T value)
        where T : struct, IComponent
    {
        _iteration.Throw();
        ref var record = ref _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has component {name}.");

        _hierarchy.TrackParent<T>(entity);

        if (_hierarchy.TryParent(entity, in value))
            return;

        MoveAndWriteAdded(entity, ref record, sourceArchetype, componentId, in value);
    }

    private void MoveAndWriteAdded<T>(
        Entity entity,
        ref EntityRecord record,
        Archetypes.Archetype sourceArchetype,
        int componentId,
        in T value)
        where T : struct, IComponent
    {
        var edge = _tables.Registry.AddEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, ref record, edge);

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
        ref var record = ref _entities.Row(entity);
        var sourceArchetype = record.Archetype!;

        if (!sourceArchetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int columnIndex = sourceArchetype.Column(componentId);
        var removedValue = record.Chunk!.ReadComponent<T>(columnIndex, record.RowInChunk);
        _hierarchy.TrackParent<T>(entity);
        _indices.Drop(entity, in removedValue);
        MoveAndCommitRemoved(entity, ref record, sourceArchetype, componentId, in removedValue);
    }

    private void MoveAndCommitRemoved<T>(
        Entity entity,
        ref EntityRecord record,
        Archetypes.Archetype sourceArchetype,
        int componentId,
        in T removedValue)
        where T : struct, IComponent
    {
        var edge = _tables.Registry.RemoveEdge(sourceArchetype, componentId);
        _tables.MoveEntity(entity, ref record, edge);
        CommitRemove(entity, in removedValue);
        WriteRemoved(entity, in removedValue);
        _entities.FinishCleanup(entity, ref record, sourceArchetype);
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
        var column = (Array)chunk.Columns[columnIndex];
        if (ComponentMetadata<T>.IsEnableable)
        {
            int maskIndex = archetype.EnableMask(componentId);
            chunk.WriteEnabled(maskIndex, row, true);
        }

        _indices.Fix(entity, componentId, column, row);
        Write(SerializationChangeKind.ComponentAdded, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Add(componentId, entity, column, row);
            _hooks.Insert(componentId, entity, column, row);
        }
    }

    internal void CommitAdd(Entity entity, int componentId, Array column, int row)
    {
        _indices.Fix(entity, componentId, column, row);
        Write(SerializationChangeKind.ComponentAdded, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Add(componentId, entity, column, row);
            _hooks.Insert(componentId, entity, column, row);
        }
    }

    internal void CommitReplace<T>(Entity entity, in T oldValue, in T newValue)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        _indices.Fix(entity, in oldValue, in newValue);
        Write(SerializationChangeKind.ComponentChanged, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Replace(entity, in oldValue);
            _hooks.Insert(entity, in newValue);
        }
    }

    internal void CommitReplace(int componentId, Entity entity, Array oldColumn, Array column, int row)
    {
        _indices.Fix(entity, componentId, column, row);
        Write(SerializationChangeKind.ComponentChanged, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Replace(componentId, entity, oldColumn, 0);
            _hooks.Insert(componentId, entity, column, row);
        }
    }

    internal void CommitRemove<T>(Entity entity, in T oldValue)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        Write(SerializationChangeKind.ComponentRemoved, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Replace(entity, in oldValue);
            _hooks.Remove(entity, in oldValue);
        }
    }

    internal void CommitRemove(int componentId, Entity entity, Array oldColumn)
    {
        Write(SerializationChangeKind.ComponentRemoved, entity, componentId);
        if (_hooks.Any)
        {
            _hooks.Replace(componentId, entity, oldColumn, 0);
            _hooks.Remove(componentId, entity, oldColumn, 0);
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
        _hierarchy.TrackParent<T>(entity);
        chunk.WriteComponent(columnIndex, row, newValue);
        MarkWrite(chunk, columnIndex, row);
        CommitReplace(entity, in oldValue, in newValue);
    }

    internal ref T WriteRef<T>(
        Entity entity,
        Archetypes.Chunk chunk,
        int row,
        int columnIndex)
        where T : struct
    {
        MarkWrite(chunk, columnIndex, row);
        _hierarchy.TrackParent<T>(entity);
        _indices.Dirty<T>();
        Write(SerializationChangeKind.ComponentChanged, entity, ComponentMetadata<T>.Id);
        return ref chunk.GetComponentRef<T>(columnIndex, row);
    }

    internal Span<T> WriteChunk<T>(
        Archetypes.Chunk chunk,
        int columnIndex)
        where T : struct
    {
        WriteChunk(chunk, columnIndex, ComponentMetadata<T>.Id);
        return Unsafe.As<T[]>(chunk.Columns[columnIndex]).AsSpan(0, chunk.Count);
    }

    internal void WriteChunk(
        Archetypes.Chunk chunk,
        int columnIndex,
        int componentId)
    {
        chunk.MarkWriteRange(columnIndex, 0, chunk.Count, _clock.Tick);
        _hierarchy.RequireScan(componentId);
        _indices.Dirty(componentId);
        if (_journal.Suppressed)
            return;

        for (int row = 0; row < chunk.Count; row++)
        {
            Write(
                SerializationChangeKind.ComponentChanged,
                chunk.Entities[row],
                componentId);
        }
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
        for (int columnIndex = 0; columnIndex < archetype.ColumnMetas.Length; columnIndex++)
        {
            int componentId = archetype.ColumnMetas[columnIndex].ComponentId;
            var column = (Array)chunk.Columns[columnIndex];
            _indices.Drop(entity, componentId, column, row);
            if (_hooks.Any)
            {
                _hooks.Replace(componentId, entity, column, row);
                _hooks.Remove(componentId, entity, column, row);
                _hooks.Despawn(componentId, entity, column, row);
            }
        }
    }

    internal void RemoveLive(
        Entity entity,
        Archetypes.Archetype archetype,
        Archetypes.Chunk chunk,
        int row)
    {
        for (int columnIndex = 0; columnIndex < archetype.ColumnMetas.Length; columnIndex++)
        {
            int componentId = archetype.ColumnMetas[columnIndex].ComponentId;
            if (Array.BinarySearch(archetype.CleanupComponentIds, componentId) >= 0)
                continue;

            var column = (Array)chunk.Columns[columnIndex];
            _indices.Drop(entity, componentId, column, row);
            if (_hooks.Any)
            {
                _hooks.Replace(componentId, entity, column, row);
                _hooks.Remove(componentId, entity, column, row);
            }
        }
    }

    private bool IsEnabled(Entity entity, int componentId, string name)
    {
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int maskIndex = archetype.EnableMask(componentId);
        return record.Chunk!.IsEnabled(maskIndex, record.RowInChunk);
    }

    private void WriteEnabled(Entity entity, int componentId, bool enabled, string name)
    {
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {name}.");

        int maskIndex = archetype.EnableMask(componentId);
        record.Chunk!.WriteEnabled(maskIndex, record.RowInChunk, enabled);
        Write(SerializationChangeKind.EnabledChanged, entity, componentId);
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
    private void MarkChunk(Archetypes.Chunk chunk, int columnIndex)
    {
        chunk.MarkChunk(columnIndex, _clock.Tick);
    }

    private void Write(
        SerializationChangeKind kind,
        Entity entity,
        int componentId,
        Entity target = default)
    {
        _journal.Write(kind, entity, componentId, target, _clock.Tick);
    }

    private void WriteRemoved<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (ComponentMetadata<T>.IsCleanup)
            return;

        Add(entity, new Removed<T>
        {
            Value = value,
            Version = _clock.Tick,
        });
    }
}


