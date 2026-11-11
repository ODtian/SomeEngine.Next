using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private static bool Contains(in AppliedEdge edge, Entity endpoint) => edge.Source == endpoint || edge.Target == endpoint;
    private static int Place(in AppliedEdge edge, Entity endpoint) => edge.Source == endpoint ? edge.SourcePlace : edge.TargetPlace;

    private bool UsesFutureOrder(Entity endpoint, RelationAdjacencyRole role, bool groupedOrder)
        => groupedOrder && _futureOrders.ContainsKey(new(endpoint, role));

    private void ReserveApplication(in AppliedEdge before, in RelationEndpoints<T> after, bool groupedOrder = false)
    {
        if (after.Source == Entity.Null) return;
        if (_directed)
        {
            if (before.Source != after.Source && !UsesFutureOrder(after.Source, RelationAdjacencyRole.Outgoing, groupedOrder))
                ReserveHead(after.Source, RelationAdjacencyRole.Outgoing, ReadHead(after.Source, RelationAdjacencyRole.Outgoing).Count + 1);
            if (before.Target != after.Target && !UsesFutureOrder(after.Target, RelationAdjacencyRole.Incoming, groupedOrder))
                ReserveHead(after.Target, RelationAdjacencyRole.Incoming, ReadHead(after.Target, RelationAdjacencyRole.Incoming).Count + 1);
        }
        else
        {
            if (!Contains(before, after.Source) && !UsesFutureOrder(after.Source, RelationAdjacencyRole.Incident, groupedOrder))
                ReserveHead(after.Source, RelationAdjacencyRole.Incident, ReadHead(after.Source, RelationAdjacencyRole.Incident).Count + 1);
            if (after.Target != after.Source && !Contains(before, after.Target)
                && !UsesFutureOrder(after.Target, RelationAdjacencyRole.Incident, groupedOrder))
                ReserveHead(after.Target, RelationAdjacencyRole.Incident, ReadHead(after.Target, RelationAdjacencyRole.Incident).Count + 1);
        }
    }

    private void Apply(Entity edge, in RelationEndpoints<T> after, bool reserve = true, bool groupedOrder = false)
    {
        AppliedEdge before = Applied(edge);
        if (before.Source == after.Source && before.Target == after.Target)
        {
            _sourceDifferences.Remove(edge);
            _targetDifferences.Remove(edge);
            return;
        }
        if (reserve) ReserveApplication(before, after, groupedOrder);
        AppliedEdge value = _directed ? ApplyDirected(edge, before, after, groupedOrder) : ApplyUndirected(edge, before, after, groupedOrder);
        WriteApplied(edge, value);
        _sourceDifferences.Remove(edge);
        _targetDifferences.Remove(edge);
    }

    private AppliedEdge ApplyDirected(Entity edge, in AppliedEdge before, in RelationEndpoints<T> after, bool groupedOrder)
    {
        int sourcePlace = before.SourcePlace, targetPlace = before.TargetPlace;
        bool sourceChanged = before.Source != after.Source, targetChanged = before.Target != after.Target;
        if (sourceChanged && before.Source != Entity.Null && !UsesFutureOrder(before.Source, RelationAdjacencyRole.Outgoing, groupedOrder))
            Drop(before.Source, sourcePlace, RelationAdjacencyRole.Outgoing);
        if (targetChanged && before.Target != Entity.Null && !UsesFutureOrder(before.Target, RelationAdjacencyRole.Incoming, groupedOrder))
            Drop(before.Target, targetPlace, RelationAdjacencyRole.Incoming);
        if (after.Source != Entity.Null)
        {
            if (!UsesFutureOrder(after.Source, RelationAdjacencyRole.Outgoing, groupedOrder))
            {
                if (sourceChanged) sourcePlace = Append(after.Source, after.Target, edge, RelationAdjacencyRole.Outgoing);
                else HeadRow(after.Source, RelationAdjacencyRole.Outgoing).Items![sourcePlace] = new(after.Target, edge);
            }
            if (!UsesFutureOrder(after.Target, RelationAdjacencyRole.Incoming, groupedOrder))
            {
                if (targetChanged) targetPlace = Append(after.Target, after.Source, edge, RelationAdjacencyRole.Incoming);
                else HeadRow(after.Target, RelationAdjacencyRole.Incoming).Items![targetPlace] = new(after.Source, edge);
            }
        }
        return new() { Source = after.Source, Target = after.Target, SourcePlace = sourcePlace, TargetPlace = targetPlace, Generation = edge.Generation };
    }

    private AppliedEdge ApplyUndirected(Entity edge, in AppliedEdge before, in RelationEndpoints<T> after, bool groupedOrder)
    {
        if (before.Source != Entity.Null && before.Source != after.Source && before.Source != after.Target
            && !UsesFutureOrder(before.Source, RelationAdjacencyRole.Incident, groupedOrder))
            Drop(before.Source, before.SourcePlace, RelationAdjacencyRole.Incident);
        if (before.Target != Entity.Null && before.Target != before.Source && before.Target != after.Source && before.Target != after.Target
            && !UsesFutureOrder(before.Target, RelationAdjacencyRole.Incident, groupedOrder))
            Drop(before.Target, before.TargetPlace, RelationAdjacencyRole.Incident);
        int sourcePlace = 0, targetPlace = 0;
        if (after.Source != Entity.Null)
        {
            if (!UsesFutureOrder(after.Source, RelationAdjacencyRole.Incident, groupedOrder))
            {
                sourcePlace = Contains(before, after.Source) ? Place(before, after.Source)
                    : Append(after.Source, after.Target, edge, RelationAdjacencyRole.Incident);
                HeadRow(after.Source, RelationAdjacencyRole.Incident).Items![sourcePlace] = new(after.Target, edge);
            }
            if (after.Target == after.Source) targetPlace = sourcePlace;
            else if (!UsesFutureOrder(after.Target, RelationAdjacencyRole.Incident, groupedOrder))
            {
                targetPlace = Contains(before, after.Target) ? Place(before, after.Target)
                    : Append(after.Target, after.Source, edge, RelationAdjacencyRole.Incident);
                HeadRow(after.Target, RelationAdjacencyRole.Incident).Items![targetPlace] = new(after.Source, edge);
            }
        }
        return new() { Source = after.Source, Target = after.Target, SourcePlace = sourcePlace, TargetPlace = targetPlace, Generation = edge.Generation };
    }
}
