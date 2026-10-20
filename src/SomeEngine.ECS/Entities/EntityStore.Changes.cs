using System.Collections.Immutable;

namespace SomeEngine.ECS.Entities;

internal sealed partial class EntityStore
{
    private readonly object _tableChangesLock = new();
    private ImmutableDictionary<EntityTableChanges, ImmutableDictionary<Entity, long>> _tableChanges =
        ImmutableDictionary<EntityTableChanges, ImmutableDictionary<Entity, long>>.Empty;
    private long _tableChangeVersion;

    internal void ObserveTables(EntityTableChanges changes)
    {
        lock (_tableChangesLock)
        {
            var entities = ImmutableDictionary.CreateBuilder<Entity, long>();
            for (int index = 1; index <= _count; index++)
                if (IsAliveIndex(index)) entities.Add(new Entity(index, GetGeneration(index)), _tableChangeVersion);
            _tableChanges = _tableChanges.Add(changes, entities.ToImmutable());
        }
    }

    internal ImmutableDictionary<Entity, long> ReadTableChanges(EntityTableChanges changes)
    {
        lock (_tableChangesLock)
            return _tableChanges[changes];
    }

    internal void RemoveTableObserver(EntityTableChanges changes)
    {
        lock (_tableChangesLock)
            _tableChanges = _tableChanges.Remove(changes);
    }

    internal void AcceptTableChanges(EntityTableChanges changes, ImmutableDictionary<Entity, long> accepted)
    {
        lock (_tableChangesLock)
        {
            var remaining = _tableChanges[changes];
            foreach ((Entity entity, long version) in accepted)
                if (remaining.TryGetValue(entity, out long current) && current == version)
                    remaining = remaining.Remove(entity);
            _tableChanges = _tableChanges.SetItem(changes, remaining);
        }
    }

    internal void ChangeTable(int index, long archetype)
    {
        ref PersistentEntityRecord record = ref MutableRecord(index);
        if (record.ArchetypeIdentity == archetype) return;
        RecordTableChange(new Entity(index, record.Generation));
        record.ArchetypeIdentity = archetype;
    }

    internal void RecordTableChange(Entity entity)
    {
        lock (_tableChangesLock)
        {
            if (_tableChanges.IsEmpty) return;
            long version = checked(_tableChangeVersion + 1);
            var changes = _tableChanges;
            foreach (var observer in _tableChanges)
                changes = changes.SetItem(observer.Key, observer.Value.SetItem(entity, version));
            _tableChanges = changes;
            _tableChangeVersion = version;
        }
    }
}
