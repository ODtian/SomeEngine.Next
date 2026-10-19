using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS;

public partial class World
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool TryGetEntityQueryRow(QueryState query, Entity entity, uint last, uint current,
        out QueryRow row)
    {
        if (TryGetEntityQueryRecord(query, entity, last, out var record, out var match))
        {
            row = new QueryRow(this, match, record.Chunk!, record.RowInChunk, last, current);
            return true;
        }
        row = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ComponentReference<T> ReadEntityQueryRef<T>(scoped in QueryCursor query, Entity entity) where T : struct
    {
        if (query.Owner.TryGetEntityQueryRecord(query.State, entity, query.LastSystemVersion, out var record, out var match) &&
            QueryAccessGuards.TryRequireReadAccess<T>(match, out int column))
            return new(in record.Chunk!.ComponentRows<T>(column)[record.RowInChunk]);
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool ContainsEntityQuery(QueryState query, Entity entity, uint last) =>
        TryGetEntityQueryRecord(query, entity, last, out _, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetEntityQueryRecord(QueryState query, Entity entity, uint last,
        out EntityRecord record, out QueryArchetypeMatch match)
    {
        EntityStore entities = _entities.Store;
        if (entities.IsAlive(entity))
        {
            record = entities.GetRecordReadOnly(entity);
            if (record.Archetype is not null && query.TryGetMatch(record.Archetype, out match) &&
                match.MatchesChunkFilter(record.Chunk!, last) &&
                match.MatchesRow(record.Chunk!, record.RowInChunk, last))
                return true;
        }
        record = default;
        match = null!;
        return false;
    }

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
