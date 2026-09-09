using System.Collections.Immutable;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS;

public partial class World
{
    internal bool TryGetEntityQueryChunk(QueryState query, Entity entity,
        out QueryArchetypeMatch match, out Chunk chunk)
    {
        if (_entities.Store.IsAlive(entity))
        {
            EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
            if (record.Archetype is not null && query.TryGetMatch(record.Archetype, out match))
            {
                chunk = record.Chunk!;
                return true;
            }
        }
        match = null!;
        chunk = null!;
        return false;
    }
    /// <summary>
    /// Observes table membership at its mutation source. The first batch includes existing live
    /// entities; later batches contain only changed identities, including destroyed generations.
    /// Changes in an aborted structural transaction are discarded with that transaction.
    /// </summary>
    public EntityTableChanges ObserveEntityTables()
    {
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        var changes = new EntityTableChanges(this);
        _entities.Store.ObserveTables(changes);
        return changes;
    }

    internal EntityTableChangeBatch ReadEntityTableChanges(EntityTableChanges changes)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        ThrowIfStructuralTransactionActive();
        return new EntityTableChangeBatch(changes, _entities.Store.ReadTableChanges(changes));
    }

    internal void AcceptEntityTableChanges(EntityTableChanges changes, ImmutableDictionary<Entity, long> accepted)
    {
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        _entities.Store.AcceptTableChanges(changes, accepted);
    }

    internal void RemoveEntityTableChanges(EntityTableChanges changes)
    {
        if (Volatile.Read(ref _lifetimeState) == LifetimeClosed) return;
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        _entities.Store.RemoveTableObserver(changes);
    }
}
