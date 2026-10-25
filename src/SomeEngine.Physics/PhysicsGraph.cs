using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public enum PhysicsGraphOrder : byte { Islands, Colors }
public enum PhysicsGraphPhase : byte { Metadata, Initialize, Bodies, Duals }

public readonly record struct PhysicsGraphSettings(PhysicsGraphOrder Order, int SmallIslandBodyLimit,
    int BodiesPerBatch, int LargeIslandBodyLimit, int RowsPerBlock, bool UseSimd);

/// <summary>An affected body or an additional body whose current state is read by a constraint.</summary>
public readonly record struct PhysicsConstraintBody(Entity Body, bool Writes);

/// <summary>Collects current entity dependencies into the graph while original rows are borrowed.</summary>
public readonly ref struct PhysicsConstraintGraphBuilder
{
    private readonly PhysicsGraphData _data;
    internal PhysicsConstraintGraphBuilder(PhysicsGraphData data) => _data = data;

    public void Add(int query, Entity constraint, ReadOnlySpan<PhysicsConstraintBody> bodies, ReadOnlySpan<Entity> references) =>
        _data.Add(query, constraint, bodies, references);

    public void Add<TConstraint, TBodies>(int query, scoped QueryRow row, in PhysicsStepContext context)
        where TConstraint : struct, SomeEngine.ECS.IComponent where TBodies : struct, IPhysicsConstraintBodies<TConstraint> =>
        _data.Add<TConstraint, TBodies>(query, row, in context);
}

internal readonly record struct PhysicsConstraintIdentity(int Query, Entity Entity);

internal sealed class PhysicsGraphData : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private readonly PhysicsGraphSettings _settings;
    private readonly int _queryCount;
    private readonly Dictionary<Entity, BodyInput> _bodies = [];
    private readonly Dictionary<PhysicsConstraintIdentity, ConstraintInput> _constraints = [];
    private readonly List<ConstraintInput> _ordered = [];
    private readonly List<PhysicsConstraintIdentity> _removedConstraints = [];
    private readonly List<Entity> _removedBodies = [];
    private readonly Dictionary<Entity, bool> _bodyUses = [];
    private readonly HashSet<Entity> _references = [];
    private readonly Dictionary<Entity, int> _dynamic = [];
    private int[] _parents = [];
    private readonly List<Island> _islands = [];
    private readonly Stack<Island> _unusedIslands = [];
    private readonly Stack<PhysicsGraphBatch> _unusedBatches = [];
    private readonly int[] _orders;
    private long _read;
    private int _bodyOrder;
    private bool _changed;

    internal PhysicsGraphData(Scheduler scheduler, int queryCount, PhysicsGraphSettings settings)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Physics constraint graph");
        _settings = settings;
        _queryCount = queryCount;
        _orders = new int[queryCount];
    }

    internal readonly List<PhysicsGraphBatch> Batches = [];
    internal JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);

    internal void Begin()
    {
        _read++;
        _changed = false;
        _bodyOrder = 0;
        Array.Clear(_orders);
    }

    internal void ReadBodies(scoped QueryCursor query)
    {
        foreach (QueryRow row in query.Rows)
        {
            BodyMotion motion = row.ReadRef<BodyMass>().Motion;
            if (!_bodies.TryGetValue(row.Entity, out BodyInput? body))
            {
                body = new();
                _bodies.Add(row.Entity, body);
                _changed = true;
            }
            _changed |= body.Motion != motion || body.Order != _bodyOrder;
            body.Motion = motion;
            body.Order = _bodyOrder++;
            body.Read = _read;
        }
    }

    internal void Add<TConstraint, TBodies>(int query, scoped QueryRow row, in PhysicsStepContext context)
        where TConstraint : struct, SomeEngine.ECS.IComponent where TBodies : struct, IPhysicsConstraintBodies<TConstraint>
    {
        _bodyUses.Clear();
        _references.Clear();
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
        _bodyUses.Clear();
        _references.Clear();
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
            _changed = true;
        }
        int order = _orders[query]++;
        _changed |= constraint.Order != order;
        constraint.Order = order;
        constraint.Read = _read;
        bool same = constraint.Bodies.Count == _bodyUses.Count && constraint.References.SetEquals(_references);
        if (same)
            foreach (var use in constraint.Bodies)
                if (!_bodyUses.TryGetValue(use.Key, out bool writes) || writes != use.Value) { same = false; break; }
        if (same) return;
        constraint.Bodies.Clear();
        foreach (var use in _bodyUses) constraint.Bodies.Add(use.Key, use.Value);
        constraint.References.Clear();
        constraint.References.UnionWith(_references);
        _changed = true;
    }

    internal void Finish()
    {
        _removedConstraints.Clear();
        foreach (var entry in _constraints) if (entry.Value.Read != _read) _removedConstraints.Add(entry.Key);
        foreach (PhysicsConstraintIdentity key in _removedConstraints) _constraints.Remove(key);
        _removedBodies.Clear();
        foreach (var entry in _bodies) if (entry.Value.Read != _read) _removedBodies.Add(entry.Key);
        foreach (Entity body in _removedBodies) _bodies.Remove(body);
        _changed |= _removedConstraints.Count != 0 || _removedBodies.Count != 0;
        if (!_changed) return;
        BuildIslands();
        BuildBatches();
    }

    private void BuildIslands()
    {
        foreach (Island island in _islands) { island.Clear(); _unusedIslands.Push(island); }
        _islands.Clear();
        _dynamic.Clear();
        _ordered.Clear();
        _ordered.AddRange(_constraints.Values);
        _ordered.Sort(static (a, b) => a.Identity.Query != b.Identity.Query
            ? a.Identity.Query.CompareTo(b.Identity.Query) : a.Order.CompareTo(b.Order));
        foreach (ConstraintInput constraint in _ordered)
            foreach (Entity body in constraint.Bodies.Keys)
                if (_bodies[body].Motion == BodyMotion.Dynamic && !_dynamic.ContainsKey(body)) _dynamic.Add(body, _dynamic.Count);
        if (_parents.Length < _dynamic.Count) Array.Resize(ref _parents, _dynamic.Count);
        for (int index = 0; index < _dynamic.Count; index++) _parents[index] = index;
        foreach (ConstraintInput constraint in _ordered)
        {
            int first = -1;
            foreach (Entity body in constraint.Bodies.Keys)
            {
                if (!_dynamic.TryGetValue(body, out int current)) continue;
                if (first < 0) first = current;
                else Join(first, current);
            }
        }
        var islands = new Dictionary<int, Island>();
        foreach (ConstraintInput constraint in _ordered)
        {
            int root = -1;
            foreach (Entity body in constraint.Bodies.Keys)
                if (_dynamic.TryGetValue(body, out int current)) { root = Root(current); break; }
            if (!islands.TryGetValue(root, out Island? island))
            {
                island = _unusedIslands.TryPop(out Island? reused) ? reused : new(_queryCount);
                islands.Add(root, island);
                _islands.Add(island);
            }
            foreach (var body in constraint.Bodies)
            {
                island.Reads.Add(body.Key);
                if (body.Value && _bodies[body.Key].Motion == BodyMotion.Dynamic) island.Writes.Add(body.Key);
            }
            island.References.UnionWith(constraint.References);
            island.Constraints[constraint.Identity.Query].Add(constraint.Identity.Entity);
        }
    }

    private int Root(int index)
    {
        while (_parents[index] != index) { _parents[index] = _parents[_parents[index]]; index = _parents[index]; }
        return index;
    }
    private void Join(int first, int second)
    {
        first = Root(first); second = Root(second);
        if (first != second) _parents[Math.Max(first, second)] = Math.Min(first, second);
    }

    private void BuildBatches()
    {
        foreach (PhysicsGraphBatch batch in Batches) { batch.Clear(); _unusedBatches.Push(batch); }
        Batches.Clear();
        PhysicsGraphBatch? small = null;
        foreach (Island island in _islands)
        {
            bool isSmall = island.Writes.Count <= _settings.SmallIslandBodyLimit;
            if (!isSmall || small is null || small.WriteCount + island.Writes.Count > _settings.BodiesPerBatch)
            {
                PhysicsGraphBatch batch = _unusedBatches.TryPop(out PhysicsGraphBatch? reused) ? reused : new(_queryCount + 3);
                Batches.Add(batch);
                small = isSmall ? batch : null;
                batch.Add(island.Reads, island.Writes, island.References, island.Constraints);
            }
            else small.Add(island.Reads, island.Writes, island.References, island.Constraints);
        }
        foreach (PhysicsGraphBatch batch in Batches) batch.OrderBodies(_bodies);
    }

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);

    internal sealed class BodyInput
    {
        internal BodyMotion Motion;
        internal long Read;
        internal int Order;
    }
    private sealed class ConstraintInput(PhysicsConstraintIdentity identity)
    {
        internal readonly PhysicsConstraintIdentity Identity = identity;
        internal readonly Dictionary<Entity, bool> Bodies = [];
        internal readonly HashSet<Entity> References = [];
        internal long Read;
        internal int Order;
    }
    private sealed class Island(int count)
    {
        internal readonly HashSet<Entity> Reads = [], Writes = [], References = [];
        internal readonly List<Entity>[] Constraints = Enumerable.Range(0, count).Select(static _ => new List<Entity>()).ToArray();
        internal void Clear()
        {
            Reads.Clear(); Writes.Clear(); References.Clear();
            foreach (List<Entity> rows in Constraints) rows.Clear();
        }
    }
}

internal sealed class PhysicsGraphBatch(int queryCount)
{
    private readonly List<Entity>[] _entities = Enumerable.Range(0, queryCount).Select(static _ => new List<Entity>()).ToArray();
    private readonly HashSet<Entity>[] _unique = Enumerable.Range(0, 3).Select(static _ => new HashSet<Entity>()).ToArray();
    internal int WriteCount => _entities[1].Count;
    internal ReadOnlySpan<Entity> Entities(int query) => CollectionsMarshal.AsSpan(_entities[query]);
    internal void Clear()
    {
        foreach (List<Entity> entities in _entities) entities.Clear();
        foreach (HashSet<Entity> entities in _unique) entities.Clear();
    }
    internal void Add(IEnumerable<Entity> reads, IEnumerable<Entity> writes, IEnumerable<Entity> references, List<Entity>[] constraints)
    {
        Add(0, reads); Add(1, writes); Add(2, references);
        for (int index = 0; index < constraints.Length; index++) _entities[index + 3].AddRange(constraints[index]);
    }
    private void Add(int query, IEnumerable<Entity> entities)
    {
        foreach (Entity entity in entities) if (_unique[query].Add(entity)) _entities[query].Add(entity);
    }
    internal void OrderBodies(Dictionary<Entity, PhysicsGraphData.BodyInput> bodies)
    {
        _entities[0].Sort((a, b) => bodies[a].Order.CompareTo(bodies[b].Order));
        _entities[1].Sort((a, b) => bodies[a].Order.CompareTo(bodies[b].Order));
    }
}
