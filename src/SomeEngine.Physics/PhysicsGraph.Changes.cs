using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

internal sealed partial class PhysicsGraphData
{
    internal void Begin()
    {
        _read++;
        _bodyOrder = 0;
        Array.Clear(_orders);
    }

    private void Dirty(Island? island) { if (island is not null) _dirtyIslands.Add(island); }

    internal void ReadBodies(scoped QueryCursor query)
    {
        foreach (QueryRow row in query.Rows)
        {
            BodyMotion motion = row.ReadRef<BodyMass>().Motion;
            bool awake = BodyActivities.IsAwake(row);
            if (!_bodies.TryGetValue(row.Entity, out BodyInput? body))
            {
                body = new();
                _bodies.Add(row.Entity, body);
                _dirtyBodies.Add(row.Entity);
            }
            if (body.Motion != motion || body.Awake != awake)
            {
                if (body.Read != 0 && body.Motion != motion) _activityChanges.Add(row.Entity);
                Dirty(body.Island);
                foreach (ConstraintInput constraint in body.Constraints) Dirty(constraint.Island);
                _dirtyBodies.Add(row.Entity);
            }
            body.Motion = motion;
            body.Awake = awake;
            body.Order = _bodyOrder++;
            body.Read = _read;
        }
    }

    internal void Add<TConstraint, TBodies>(int query, scoped QueryRow row, in PhysicsStepContext context)
        where TConstraint : struct, SomeEngine.ECS.IComponent where TBodies : struct, IPhysicsConstraintBodies<TConstraint>
    {
        _bodyUses.Clear(); _references.Clear();
        ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
        for (int index = 0; index < TBodies.GetBodyCount(in constraint, row, in context); index++)
            _bodyUses[TBodies.GetBody(in constraint, row, index, in context)] = true;
        for (int index = 0; index < TBodies.GetReadBodyCount(in constraint, row, in context); index++)
            _bodyUses.TryAdd(TBodies.GetReadBody(in constraint, row, index, in context), false);
        for (int index = 0; index < TBodies.GetReadEntityCount(in constraint, row, in context); index++)
            _references.Add(TBodies.GetReadEntity(in constraint, row, index, in context));
        Store(query, row.Entity);
    }

    internal void Add(int query, Entity entity, ReadOnlySpan<PhysicsConstraintBody> bodies, ReadOnlySpan<Entity> references)
    {
        _bodyUses.Clear(); _references.Clear();
        foreach (PhysicsConstraintBody body in bodies)
            _bodyUses[body.Body] = body.Writes || (_bodyUses.TryGetValue(body.Body, out bool writes) && writes);
        foreach (Entity reference in references) _references.Add(reference);
        Store(query, entity);
    }

    private void Store(int query, Entity entity)
    {
        var identity = new PhysicsConstraintIdentity(query, entity);
        if (!_constraints.TryGetValue(identity, out ConstraintInput? constraint))
        {
            constraint = new(identity);
            _constraints.Add(identity, constraint);
            _dirtyConstraints.Add(constraint);
        }
        constraint.Order = _orders[query]++;
        constraint.Read = _read;
        bool same = constraint.Bodies.Count == _bodyUses.Count && constraint.References.SetEquals(_references);
        if (same)
            foreach (var use in constraint.Bodies)
                if (!_bodyUses.TryGetValue(use.Key, out bool writes) || writes != use.Value) { same = false; break; }
        if (same) return;
        Dirty(constraint.Island);
        foreach (Entity body in constraint.Bodies.Keys)
        {
            if (_bodies[body].Motion == BodyMotion.Dynamic) _activityChanges.Add(body);
            _bodies[body].Constraints.Remove(constraint);
        }
        constraint.Bodies.Clear();
        foreach (var use in _bodyUses)
        {
            constraint.Bodies.Add(use.Key, use.Value);
            BodyInput body = _bodies[use.Key];
            if (body.Motion == BodyMotion.Dynamic) _activityChanges.Add(use.Key);
            Dirty(body.Island);
            body.Constraints.Add(constraint);
        }
        constraint.References.Clear(); constraint.References.UnionWith(_references);
        _dirtyConstraints.Add(constraint);
    }

    internal bool Finish()
    {
        _removedConstraints.Clear();
        foreach (var entry in _constraints) if (entry.Value.Read != _read) _removedConstraints.Add(entry.Key);
        foreach (PhysicsConstraintIdentity key in _removedConstraints)
        {
            ConstraintInput constraint = _constraints[key];
            Dirty(constraint.Island);
            foreach (Entity body in constraint.Bodies.Keys)
            {
                if (_bodies[body].Motion == BodyMotion.Dynamic) _activityChanges.Add(body);
                _bodies[body].Constraints.Remove(constraint);
            }
            _constraints.Remove(key);
        }
        _removedBodies.Clear();
        foreach (var entry in _bodies) if (entry.Value.Read != _read) _removedBodies.Add(entry.Key);
        foreach (Entity entity in _removedBodies)
        {
            BodyInput body = _bodies[entity];
            Dirty(body.Island);
            foreach (ConstraintInput constraint in body.Constraints) Dirty(constraint.Island);
            _bodies.Remove(entity);
            _activityChanges.Remove(entity);
        }
        foreach (Island island in _islands)
            if (OrderChanged(island)) _dirtyIslands.Add(island);
        if (_dirtyBodies.Count == 0 && _dirtyConstraints.Count == 0 && _dirtyIslands.Count == 0) return false;
        RepairIslands();
        RepairBatches();
        CollectWork();
        _dirtyBodies.Clear(); _dirtyConstraints.Clear(); _dirtyIslands.Clear(); _dirtyBatches.Clear();
        return true;
    }

    private bool OrderChanged(Island island)
    {
        int previous = -1;
        foreach (Entity body in island.BodyOrder)
            if (_bodies.TryGetValue(body, out BodyInput? input))
            { if (input.Order < previous) return true; previous = input.Order; }
        for (int query = 0; query < _queryCount; query++)
        {
            previous = -1;
            foreach (Entity entity in island.Constraints[query])
                if (_constraints.TryGetValue(new(query, entity), out ConstraintInput? input))
                { if (input.Order < previous) return true; previous = input.Order; }
        }
        return false;
    }

    private void RepairIslands()
    {
        _affectedBodies.Clear(); _affectedConstraints.Clear();
        _affectedBodies.UnionWith(_dirtyBodies);
        _affectedConstraints.UnionWith(_dirtyConstraints);
        foreach (Island island in _dirtyIslands)
        {
            _affectedBodies.UnionWith(island.DynamicBodies);
            _affectedConstraints.UnionWith(island.Records);
            if (island.Batch is not null) _dirtyBatches.Add(island.Batch);
            _islands.Remove(island);
        }
        foreach (PhysicsGraphBatch batch in _dirtyBatches)
        {
            Batches.Remove(batch);
            foreach (Island island in batch.Islands) island.Batch = null;
            batch.Clear(); _unusedBatches.Push(batch);
        }
        foreach (Island island in _dirtyIslands)
        {
            foreach (var color in island.ProjectionColors) foreach (PhysicsGraphBatch batch in color.Batches) ReturnBatch(batch);
            foreach (var color in island.BodyColors) foreach (PhysicsGraphBatch batch in color) ReturnBatch(batch);
            island.Clear(); _unusedIslands.Push(island);
        }
        foreach (ConstraintInput constraint in _dirtyConstraints)
            foreach (Entity body in constraint.Bodies.Keys)
                if (_bodies[body].Motion == BodyMotion.Dynamic) _affectedBodies.Add(body);
        _dynamic.Clear();
        foreach (Entity entity in _affectedBodies)
            if (_bodies.TryGetValue(entity, out BodyInput? body))
            {
                body.Island = null;
                if (body.Motion == BodyMotion.Dynamic) _dynamic.Add(entity, _dynamic.Count);
            }
        if (_parents.Length < _dynamic.Count) Array.Resize(ref _parents, _dynamic.Count);
        for (int index = 0; index < _dynamic.Count; index++) _parents[index] = index;
        _ordered.Clear();
        foreach (ConstraintInput constraint in _affectedConstraints)
            if (_constraints.ContainsKey(constraint.Identity)) _ordered.Add(constraint);
        _ordered.Sort(static (a, b) => a.Identity.Query != b.Identity.Query
            ? a.Identity.Query.CompareTo(b.Identity.Query) : a.Order.CompareTo(b.Order));
        foreach (ConstraintInput constraint in _ordered)
        {
            int first = -1;
            foreach (Entity body in constraint.Bodies.Keys)
                if (_dynamic.TryGetValue(body, out int current)) { if (first < 0) first = current; else Join(first, current); }
        }
        _newRoots.Clear();
        foreach (var entry in _dynamic.OrderBy(entry => _bodies[entry.Key].Order))
        {
            Island island = GetIsland(Root(entry.Value));
            island.DynamicBodies.Add(entry.Key);
            island.Reads.Add(entry.Key);
            if (_bodyUpdates) island.Writes.Add(entry.Key);
            _bodies[entry.Key].Island = island;
        }
        int staticRoot = -1;
        foreach (ConstraintInput constraint in _ordered)
        {
            int root = staticRoot--;
            foreach (Entity body in constraint.Bodies.Keys)
                if (_dynamic.TryGetValue(body, out int current)) { root = Root(current); break; }
            Island island = GetIsland(root);
            constraint.Island = island;
            island.Records.Add(constraint);
            foreach (var use in constraint.Bodies)
            {
                island.Reads.Add(use.Key);
                if (use.Value && _bodies[use.Key].Motion == BodyMotion.Dynamic) island.Writes.Add(use.Key);
            }
            island.References.UnionWith(constraint.References);
            island.Constraints[constraint.Identity.Query].Add(constraint.Identity.Entity);
        }
        foreach (Island island in _newRoots.Values)
        {
            island.BodyOrder.AddRange(island.DynamicBodies.OrderBy(body => _bodies[body].Order));
            if (IsColored(island))
            {
                BuildProjectionColors(island);
                if (_bodyUpdates) BuildBodyColors(island);
            }
        }
        _islands.Sort((a, b) => FirstOrder(a).CompareTo(FirstOrder(b)));
    }

    private Island GetIsland(int root)
    {
        if (_newRoots.TryGetValue(root, out Island? island)) return island;
        island = _unusedIslands.TryPop(out Island? reused) ? reused : new(_queryCount);
        _newRoots.Add(root, island); _islands.Add(island);
        return island;
    }
    private int FirstOrder(Island island) => island.BodyOrder.Count == 0 ? int.MaxValue : _bodies[island.BodyOrder[0]].Order;
}
