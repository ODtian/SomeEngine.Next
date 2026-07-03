using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Relations
{
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    internal object?[] Stores = new object?[8];
    internal readonly List<IRelationStore> All = new();
    private readonly List<RelationDrop> _drops = new();

    internal bool Any => All.Count != 0;

    internal void Bind(
        Entities entities,
        Tables tables,
        Journal journal,
        Clock clock)
    {
        _entities = entities;
        _tables = tables;
        _journal = journal;
        _clock = clock;
    }

    internal void Add<T>(Entity source, Entity target, in T value)
        where T : struct, IRelation
    {
        _entities.ThrowDead(source);
        _entities.ThrowDead(target);

        var store = Store<T>();
        bool isFirstRelation = store.GetRelations(source).Length == 0;
        store.Add(source, target, value, _clock.Tick);
        Write(
            SerializationChangeKind.RelationAdded,
            source,
            ComponentMetadata<T>.Id,
            target);

        if (isFirstRelation)
            AddTag<T>(source);
    }

    internal void Replace<T>(Entity source, Entity target, in T value)
        where T : struct, IRelation
    {
        _entities.ThrowDead(source);
        _entities.ThrowDead(target);
        if (!Try<T>(out var store))
            throw new InvalidOperationException(
                $"Entity {source} does not have relation {typeof(T).Name}.");

        store.Replace(source, target, value, _clock.Tick);
        Write(
            SerializationChangeKind.RelationChanged,
            source,
            ComponentMetadata<T>.Id,
            target);
    }

    internal void Remove<T>(Entity source, Entity target)
        where T : struct, IRelation
    {
        _entities.ThrowDead(source);
        _entities.ThrowDead(target);
        if (!Try<T>(out var store) ||
            !store.Remove(source, target, _clock.Tick))
            throw new InvalidOperationException(
                $"Entity {source} does not have relation {typeof(T).Name} to {target}.");

        Write(
            SerializationChangeKind.RelationRemoved,
            source,
            ComponentMetadata<T>.Id,
            target);

        if (store.GetRelations(source).Length == 0)
            DropTag<T>(source);
    }

    internal bool Has<T>(Entity source, Entity target)
        where T : struct, IRelation
    {
        if (!_entities.Store.IsAlive(source) || !_entities.Store.IsAlive(target))
            return false;

        if (_entities.Store.GetRecord(source).Archetype is null ||
            _entities.Store.GetRecord(target).Archetype is null)
        {
            return false;
        }

        return Try<T>(out var store) && store.Has(source, target);
    }

    internal ReadOnlySpan<RelationEntry<T>> Get<T>(Entity source)
        where T : struct, IRelation
    {
        _entities.ThrowDead(source);
        return Try<T>(out var store)
            ? store.GetRelations(source)
            : ReadOnlySpan<RelationEntry<T>>.Empty;
    }

    internal ReadOnlySpan<Entity> Sources<T>(Entity target)
        where T : struct, IRelation
    {
        _entities.ThrowDead(target);
        return Try<T>(out var store)
            ? store.GetSources(target)
            : ReadOnlySpan<Entity>.Empty;
    }

    internal void RemoveAll<T>(Entity source)
        where T : struct, IRelation
    {
        _entities.ThrowDead(source);
        if (!Try<T>(out var store))
            return;

        var removed = store.GetRelations(source).ToArray();
        if (!store.RemoveAll(source, _clock.Tick))
            return;

        for (int i = 0; i < removed.Length; i++)
        {
            Write(
                SerializationChangeKind.RelationRemoved,
                source,
                ComponentMetadata<T>.Id,
                removed[i].Target);
        }

        DropTag<T>(source);
    }

    internal ReadOnlySpan<RelationChange<T>> Changes<T>(uint lastVersion)
        where T : struct, IRelation
    {
        return Try<T>(out var store)
            ? store.Changes(lastVersion)
            : ReadOnlySpan<RelationChange<T>>.Empty;
    }

    internal void RemoveOutgoing(Entity source)
    {
        for (int i = 0; i < All.Count; i++)
            All[i].RemoveAllOutgoing(this, source);
    }

    internal void CopyOutgoing(Entity source, Entity target)
    {
        for (int i = 0; i < All.Count; i++)
            All[i].AddOutgoingCopy(this, source, target);
    }

    internal RelationStore<T> Store<T>()
        where T : struct, IRelation
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref Stores, componentId + 1, 8);
        if (Stores[componentId] is RelationStore<T> existing)
            return existing;

        var store = new RelationStore<T>();
        Stores[componentId] = store;
        All.Add(store);
        return store;
    }

    internal bool Try<T>(out RelationStore<T> store)
        where T : struct, IRelation
    {
        int componentId = ComponentMetadata<T>.Id;
        if (componentId < Stores.Length && Stores[componentId] is RelationStore<T> existing)
        {
            store = existing;
            return true;
        }

        store = null!;
        return false;
    }

    internal void Cleanup(Entity entity)
    {
        if (All.Count == 0)
            return;

        _drops.Clear();
        foreach (var store in All)
            store.OnEntityDestroyed(entity, _clock.Tick, _drops);

        for (int i = 0; i < _drops.Count; i++)
            DropTag(_drops[i].Source, _drops[i].TagId);

        _drops.Clear();
    }

    internal void DropTag(Entity source, int tagId)
    {
        if (!_entities.Alive(source))
            return;

        ref var record = ref _entities.Store.GetRecord(source);
        if (record.Archetype is null)
            return;

        if (!record.Archetype.HasComponent(tagId))
            return;

        var edge = _tables.Registry.RemoveEdge(record.Archetype, tagId);
        _tables.MoveEntity(source, ref record, edge);
    }

    internal void Reset()
    {
        Array.Clear(Stores);
        All.Clear();
        _drops.Clear();
    }

    private void AddTag<T>(Entity source)
        where T : struct, IRelation
    {
        int tagId = ComponentMetadata<RelationTag<T>>.Id;
        ref var record = ref _entities.Store.GetRecord(source);
        if (record.Archetype!.HasComponent(tagId))
            return;

        var edge = _tables.Registry.AddEdge(record.Archetype, tagId);
        _tables.MoveEntity(source, ref record, edge);
    }

    private void DropTag<T>(Entity source)
        where T : struct, IRelation
    {
        DropTag(source, ComponentMetadata<RelationTag<T>>.Id);
    }

    private void Write(
        SerializationChangeKind kind,
        Entity entity,
        int componentId,
        Entity target)
    {
        _journal.Write(kind, entity, componentId, target, _clock.Tick);
    }
}


