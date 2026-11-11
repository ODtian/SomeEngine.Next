using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Relations;

public static partial class Relations<T> where T : struct
{
    public static void Edit(World world, ReadOnlySpan<EndpointEdit> edits, Span<RelationEdge<T>> created, bool deferred = false)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).Edit(edits, MemoryMarshal.Cast<RelationEdge<T>, Entity>(created), deferred);
    }
}

internal sealed partial class RelationData<T>
{
    private static Entity ResolveBatchEntity(Entity entity, ReadOnlySpan<Entity> created)
    {
        if (entity.Id >= 0) return entity;
        int index = -entity.Id - 1;
        if (entity.Generation != 0 || (uint)index >= (uint)created.Length)
            throw new ArgumentException("The batch entity index must name a creation in this edit batch.");
        return created[index];
    }

    private bool IsNewBatchEntity(Entity entity)
        => _batchBefore.TryGetValue(entity, out var before) && before.Source == Entity.Null;

    private void RequireFinalEndpoint(Entity entity)
    {
        if (!IsNewBatchEntity(entity)) RequireActive(entity);
        if (_batchEdits.TryGetValue(entity, out var after) && after.Source == Entity.Null)
            throw new ArgumentException("The final endpoints require a surviving entity.");
    }

    private void ValidateEditClaims()
    {
        _batchClaims.Clear(); _batchPairClaims.Clear();
        foreach (var edit in _batchEdits)
        {
            var after = edit.Value;
            if (after.Source == Entity.Null) continue;
            if (_uniquePair)
            {
                var pair = EndpointPair.From(after.Source, after.Target, _directed);
                if (_pairs.TryGetValue(pair, out var current) && Conflicts(current, edit.Key, _batchEdits)
                    || _batchPairClaims.TryGetValue(pair, out var other) && other != edit.Key)
                    throw new InvalidOperationException("The final relation batch repeats an endpoint pair.");
                _batchPairClaims[pair] = edit.Key;
            }
            if (_uniqueSource)
            {
                if (!IsNewBatchEntity(after.Source)) ValidateClaim(_sourceClaims, after.Source, edit.Key, _batchEdits);
                AddFinalClaim(edit.Key, after.Source, _directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident);
            }
            if (_uniqueTarget)
            {
                if (!IsNewBatchEntity(after.Target)) ValidateClaim(_directed ? _targetClaims : _sourceClaims, after.Target, edit.Key, _batchEdits);
                AddFinalClaim(edit.Key, after.Target, _directed ? RelationAdjacencyRole.Incoming : RelationAdjacencyRole.Incident);
            }
        }
    }

    internal void Edit(ReadOnlySpan<EndpointEdit> edits, Span<Entity> created, bool deferred)
    {
        _world.RequireStructure();
        int createCount = 0;
        foreach (var edit in edits) if (edit.Kind == EndpointEditKind.Create) createCount++;
        if (created.Length != createCount) throw new ArgumentException("Provide one result for each new edge.");
        var ids = _world.PreviewEntityIds(created);
        _batchEdits.Clear(); _batchBefore.Clear();
        foreach (Entity entity in created) _batchBefore.Add(entity, default);
        int next = 0;
        foreach (var edit in edits)
        {
            Entity edge = edit.Kind == EndpointEditKind.Create ? created[next++] : ResolveBatchEntity(edit.Edge, created);
            if (!IsNewBatchEntity(edge))
            {
                RequireActive(edge);
                if (!ReadMain(edge, out var before)) throw new ArgumentException("The entity requires this relation's endpoints.");
                _batchBefore.TryAdd(edge, before);
            }
            RelationEndpoints<T> after = edit.Kind switch
            {
                EndpointEditKind.Create or EndpointEditKind.Retarget => new(ResolveBatchEntity(edit.Source, created), ResolveBatchEntity(edit.Target, created)),
                EndpointEditKind.Destroy => default,
                _ => throw new ArgumentOutOfRangeException(nameof(edits)),
            };
            if (edit.Kind != EndpointEditKind.Destroy && after.Source == Entity.Null && after.Target == Entity.Null)
                throw new ArgumentException("Created and retargeted edges require active endpoints.");
            _batchEdits[edge] = after;
        }
        if (_batchEdits.Count == 0) return;
        foreach (var edit in _batchEdits)
        {
            var after = edit.Value;
            if (after.Source == Entity.Null && after.Target == Entity.Null) continue;
            RequireFinalEndpoint(after.Source); RequireFinalEndpoint(after.Target);
            if (!_schema.AllowSelfEdge && after.Source == after.Target)
                throw new ArgumentException("This relation requires two distinct endpoints.");
        }
        ValidateEditClaims();
        _world.ReserveEntities(ids.Used);
        EnsureCapacity();
        Archetype? table = null;
        if (createCount != 0)
        {
            table = _world.RestoreArchetype(TypeSet<RelationEndpoints<T>>.Value, []);
            int rows = 0;
            foreach (Chunk chunk in table.AllChunks) rows += chunk.Count;
            table.Reserve(rows + createCount);
        }
        if (_usesOrder) UpdateBatchFuture(deferred);
        if (!deferred)
        {
            _batchHeadCounts.Clear();
            foreach (var edit in _batchEdits)
            {
                AppliedEdge before = IsNewBatchEntity(edit.Key) ? default : Applied(edit.Key);
                if (edit.Value.Source != Entity.Null) CountChanges(before, edit.Value);
                else if (_directed)
                {
                    if (before.Source != Entity.Null) HeadCount(before.Source, RelationAdjacencyRole.Outgoing, -1);
                    if (before.Target != Entity.Null) HeadCount(before.Target, RelationAdjacencyRole.Incoming, -1);
                }
                else
                {
                    if (before.Source != Entity.Null) HeadCount(before.Source, RelationAdjacencyRole.Incident, -1);
                    if (before.Target != Entity.Null && before.Target != before.Source) HeadCount(before.Target, RelationAdjacencyRole.Incident, -1);
                }
            }
            foreach (var head in _batchHeadCounts)
                if (head.Value > 0) ReserveHead(head.Key.Entity, head.Key.Role, head.Value);
        }
        ulong version = _world.NextVersion();
        using var batchVersion = _world.BeginBatchVersion(version);
        if (table is not null) _world.CommitCreatedEntities(table, created, ids.FreeHead, ids.Used, version);
        foreach (Entity entity in created)
        {
            PrepareApplied(entity);
            ref EntityRecord record = ref _world.Resolve(entity);
            _world.WriteColumn(entity, ref record, _batchEdits[entity], version);
        }
        if (!deferred)
            foreach (var edit in _batchEdits)
                if (!IsNewBatchEntity(edit.Key)) DropChanged(edit.Key, edit.Value);
        foreach (var before in _batchBefore)
            if (before.Value.Source != Entity.Null) ReleaseClaims(before.Key, before.Value);
        foreach (var edit in _batchEdits)
        {
            if (edit.Value.Source == Entity.Null) continue;
            if (!IsNewBatchEntity(edit.Key))
            {
                ref EntityRecord record = ref _world.Resolve(edit.Key);
                _world.WriteColumn(edit.Key, ref record, edit.Value, version);
            }
            Claim(edit.Key, edit.Value);
            if (deferred)
            {
                AppliedEdge before = Applied(edit.Key);
                _sourceDifferences.Set(edit.Key, edit.Value.Source == before.Source ? Entity.Null : edit.Value.Source);
                _targetDifferences.Set(edit.Key, edit.Value.Target == before.Target ? Entity.Null : edit.Value.Target);
            }
            else AppendChanged(edit.Key, edit.Value);
        }
        foreach (var edit in _batchEdits)
            if (edit.Value.Source == Entity.Null)
            {
                if (deferred) _world.DestroyDeferred(edit.Key);
                else
                {
                    WriteApplied(edit.Key, new() { Generation = edit.Key.Generation });
                    _world.Destroy(edit.Key);
                }
            }
        if (deferred) _hasDeferredChanges = true;
        else ConfirmApplied();
    }
}
