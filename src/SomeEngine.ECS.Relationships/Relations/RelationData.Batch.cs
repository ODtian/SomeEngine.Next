using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private readonly Dictionary<Entity, RelationEndpoints<T>> _batchEdits = new(), _batchBefore = new();
    private readonly Dictionary<EndpointPair, Entity> _batchPairClaims = new();
    private readonly Dictionary<AdjacencyKey, Entity> _batchClaims = new();
    private readonly Dictionary<AdjacencyKey, int> _batchHeadCounts = new();

    private void AddFinalClaim(Entity edge, Entity endpoint, RelationAdjacencyRole role)
    {
        var key = new AdjacencyKey(endpoint, role);
        if (_batchClaims.TryGetValue(key, out Entity occupant) && occupant != edge)
            throw new InvalidOperationException("The final relation batch repeats an endpoint claim.");
        _batchClaims[key] = edge;
    }

    private void ValidateFinalClaims(Dictionary<Entity, RelationEndpoints<T>> final)
    {
        _batchClaims.Clear(); _batchPairClaims.Clear();
        foreach (var edit in final)
        {
            RelationEndpoints<T> after = edit.Value;
            ValidateClaims(edit.Key, after, final);
            if (_uniquePair)
            {
                var pair = EndpointPair.From(after.Source, after.Target, _directed);
                if (_batchPairClaims.TryGetValue(pair, out Entity occupant) && occupant != edit.Key)
                    throw new InvalidOperationException("The final relation batch repeats an endpoint pair.");
                _batchPairClaims[pair] = edit.Key;
            }
            if (_uniqueSource) AddFinalClaim(edit.Key, after.Source, _directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident);
            if (_uniqueTarget) AddFinalClaim(edit.Key, after.Target, _directed ? RelationAdjacencyRole.Incoming : RelationAdjacencyRole.Incident);
        }
    }

    private void HeadCount(Entity endpoint, RelationAdjacencyRole role, int delta)
    {
        var key = new AdjacencyKey(endpoint, role);
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_batchHeadCounts, key, out bool found);
        if (!found) count = ReadHead(endpoint, role).Count;
        count += delta;
    }

    private void CountChanges(in AppliedEdge before, in RelationEndpoints<T> after)
    {
        if (_directed)
        {
            if (before.Source != after.Source)
            {
                if (before.Source != Entity.Null) HeadCount(before.Source, RelationAdjacencyRole.Outgoing, -1);
                HeadCount(after.Source, RelationAdjacencyRole.Outgoing, 1);
            }
            if (before.Target != after.Target)
            {
                if (before.Target != Entity.Null) HeadCount(before.Target, RelationAdjacencyRole.Incoming, -1);
                HeadCount(after.Target, RelationAdjacencyRole.Incoming, 1);
            }
        }
        else
        {
            if (before.Source != Entity.Null && before.Source != after.Source && before.Source != after.Target)
                HeadCount(before.Source, RelationAdjacencyRole.Incident, -1);
            if (before.Target != Entity.Null && before.Target != before.Source && before.Target != after.Source && before.Target != after.Target)
                HeadCount(before.Target, RelationAdjacencyRole.Incident, -1);
            if (!Contains(before, after.Source)) HeadCount(after.Source, RelationAdjacencyRole.Incident, 1);
            if (after.Target != after.Source && !Contains(before, after.Target)) HeadCount(after.Target, RelationAdjacencyRole.Incident, 1);
        }
    }

    private void DropChanged(Entity edge, in RelationEndpoints<T> after)
    {
        AppliedEdge before = Applied(edge);
        void Remove(Entity endpoint, int place, RelationAdjacencyRole role)
            => Drop(endpoint, place, role, _batchHeadCounts[new(endpoint, role)] > 0);
        if (_directed)
        {
            if (before.Source != Entity.Null && before.Source != after.Source) Remove(before.Source, before.SourcePlace, RelationAdjacencyRole.Outgoing);
            if (before.Target != Entity.Null && before.Target != after.Target) Remove(before.Target, before.TargetPlace, RelationAdjacencyRole.Incoming);
        }
        else
        {
            if (before.Source != Entity.Null && before.Source != after.Source && before.Source != after.Target)
                Remove(before.Source, before.SourcePlace, RelationAdjacencyRole.Incident);
            if (before.Target != Entity.Null && before.Target != before.Source && before.Target != after.Source && before.Target != after.Target)
                Remove(before.Target, before.TargetPlace, RelationAdjacencyRole.Incident);
        }
    }

    private void AppendChanged(Entity edge, in RelationEndpoints<T> after)
    {
        AppliedEdge before = Applied(edge);
        int sourcePlace, targetPlace;
        if (_directed)
        {
            sourcePlace = before.Source == after.Source ? before.SourcePlace : Append(after.Source, after.Target, edge, RelationAdjacencyRole.Outgoing);
            targetPlace = before.Target == after.Target ? before.TargetPlace : Append(after.Target, after.Source, edge, RelationAdjacencyRole.Incoming);
            HeadRow(after.Source, RelationAdjacencyRole.Outgoing).Items![sourcePlace] = new(after.Target, edge);
            HeadRow(after.Target, RelationAdjacencyRole.Incoming).Items![targetPlace] = new(after.Source, edge);
        }
        else
        {
            sourcePlace = Contains(before, after.Source) ? Place(before, after.Source) : Append(after.Source, after.Target, edge, RelationAdjacencyRole.Incident);
            targetPlace = after.Source == after.Target ? sourcePlace : Contains(before, after.Target) ? Place(before, after.Target)
                : Append(after.Target, after.Source, edge, RelationAdjacencyRole.Incident);
            HeadRow(after.Source, RelationAdjacencyRole.Incident).Items![sourcePlace] = new(after.Target, edge);
            if (after.Target != after.Source) HeadRow(after.Target, RelationAdjacencyRole.Incident).Items![targetPlace] = new(after.Source, edge);
        }
        WriteApplied(edge, new() { Source = after.Source, Target = after.Target, SourcePlace = sourcePlace, TargetPlace = targetPlace, Generation = edge.Generation });
        _sourceDifferences.Remove(edge); _targetDifferences.Remove(edge);
    }

    internal void Retarget(ReadOnlySpan<RelationRetarget<T>> edits, bool deferred)
    {
        _world.RequireStructure();
        EnsureCapacity();
        var final = _batchEdits;
        bool hasClaims = _uniqueSource || _uniqueTarget || _uniquePair;
        bool keepBefore = hasClaims || _usesOrder;
        final.Clear(); _batchBefore.Clear();
        foreach (var edit in edits)
        {
            RequireActive(edit.Edge.Entity);
            if (!ReadMain(edit.Edge.Entity, out var before)) throw new ArgumentException("The entity requires this relation's endpoints.");
            var after = new RelationEndpoints<T>(edit.Source, edit.Target);
            ValidateEndpoints(after);
            if (keepBefore) _batchBefore.TryAdd(edit.Edge.Entity, before);
            final[edit.Edge.Entity] = after;
        }
        if (final.Count == 0) return;
        if (hasClaims) ValidateFinalClaims(final);
        if (_usesOrder) UpdateBatchFuture(deferred);
        if (!deferred)
        {
            _batchHeadCounts.Clear();
            foreach (var edit in final) CountChanges(Applied(edit.Key), edit.Value);
            foreach (var head in _batchHeadCounts)
                if (head.Value > 0) ReserveHead(head.Key.Entity, head.Key.Role, head.Value);
            foreach (var edit in final) DropChanged(edit.Key, edit.Value);
        }
        if (hasClaims) foreach (var edit in _batchBefore) ReleaseClaims(edit.Key, edit.Value);
        ulong version = _world.NextVersion();
        foreach (var edit in final)
        {
            ref EntityRecord record = ref _world.Resolve(edit.Key);
            _world.WriteColumn(edit.Key, ref record, edit.Value, version);
            Claim(edit.Key, edit.Value);
        }
        if (deferred)
        {
            _hasDeferredChanges = true;
            foreach (var edit in final)
            {
                AppliedEdge before = Applied(edit.Key);
                _sourceDifferences.Set(edit.Key, edit.Value.Source == before.Source ? Entity.Null : edit.Value.Source);
                _targetDifferences.Set(edit.Key, edit.Value.Target == before.Target ? Entity.Null : edit.Value.Target);
            }
        }
        else
        {
            foreach (var edit in final) AppendChanged(edit.Key, edit.Value);
            ConfirmApplied();
        }
    }

    private void ClearBatch()
    {
        _batchEdits.Clear(); _batchBefore.Clear(); _batchPairClaims.Clear(); _batchClaims.Clear(); _batchHeadCounts.Clear();
    }
}
