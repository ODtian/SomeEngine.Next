using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public enum PhysicsGraphOrder : byte { Islands, Colors }
public enum PhysicsGraphPhase : byte { Metadata, Initialize, Bodies, Duals }
public readonly record struct PhysicsGraphSettings(PhysicsGraphOrder Order, int SmallIslandBodyLimit,
    int BodiesPerBatch, int LargeIslandBodyLimit, int RowsPerBlock, bool UseSimd);
public readonly record struct PhysicsConstraintBody(Entity Body, bool Writes);

/// <summary>Collects current entity dependencies while original constraint rows are borrowed.</summary>
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

/// <summary>Entity connectivity and reusable execution ranges; all numerical state remains in components.</summary>
internal sealed partial class PhysicsGraphData : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private readonly PhysicsGraphSettings _settings;
    private readonly bool _bodyUpdates;
    private readonly int _queryCount;
    private readonly Dictionary<Entity, BodyInput> _bodies = [];
    private readonly Dictionary<PhysicsConstraintIdentity, ConstraintInput> _constraints = [];
    private readonly List<PhysicsConstraintIdentity> _removedConstraints = [];
    private readonly List<Entity> _removedBodies = [];
    private readonly Dictionary<Entity, bool> _bodyUses = [];
    private readonly HashSet<Entity> _references = [];
    private readonly HashSet<Entity> _dirtyBodies = [], _affectedBodies = [];
    private readonly HashSet<Entity> _activityChanges = [];
    private readonly HashSet<ConstraintInput> _dirtyConstraints = [], _affectedConstraints = [];
    private readonly HashSet<Island> _dirtyIslands = [];
    private readonly HashSet<Island> _changedReferences = [];
    private readonly HashSet<PhysicsGraphBatch> _dirtyBatches = [];
    private readonly Dictionary<Entity, int> _dynamic = [];
    private readonly Dictionary<int, Island> _newRoots = [];
    private int[] _parents = [];
    private readonly List<ConstraintInput> _ordered = [];
    private readonly List<Island> _islands = [];
    private readonly Stack<Island> _unusedIslands = [];
    private readonly Stack<PhysicsGraphBatch> _unusedBatches = [];
    private readonly int[] _orders;
    private long _read;
    private int _bodyOrder;

    internal PhysicsGraphData(Scheduler scheduler, int queryCount, PhysicsGraphSettings settings, bool bodyUpdates = false)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Physics constraint graph");
        _settings = settings;
        _queryCount = queryCount;
        _bodyUpdates = bodyUpdates;
        _orders = new int[queryCount];
    }

    internal readonly List<PhysicsGraphBatch> Batches = [], ScalarBatches = [];
    internal readonly List<List<PhysicsGraphBatch>> ProjectionGroups = [], BodyGroups = [];
    internal readonly List<Entity> Writes = [], References = [];
    internal JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    internal int ProjectionWorkCount => ScalarBatches.Count + ProjectionGroups.Sum(static group => group.Count);
    internal int BodyWorkCount => ScalarBatches.Count + BodyGroups.Sum(static group => group.Count);
    internal PhysicsGraphBatch ProjectionWork(int index) => GetWork(index, ProjectionGroups);
    internal PhysicsGraphBatch BodyWork(int index) => GetWork(index, BodyGroups);

    private PhysicsGraphBatch GetWork(int index, List<List<PhysicsGraphBatch>> groups)
    {
        if (index < ScalarBatches.Count) return ScalarBatches[index];
        index -= ScalarBatches.Count;
        foreach (List<PhysicsGraphBatch> group in groups)
        { if (index < group.Count) return group[index]; index -= group.Count; }
        throw new IndexOutOfRangeException();
    }

    private bool IsColored(Island island) => _settings.Order == PhysicsGraphOrder.Colors && island.DynamicBodies.Count >= _settings.LargeIslandBodyLimit;
    private PhysicsGraphBatch TakeBatch() => _unusedBatches.TryPop(out PhysicsGraphBatch? batch) ? batch : new(_queryCount + 3);
    private void ReturnBatch(PhysicsGraphBatch batch) { batch.Clear(); _unusedBatches.Push(batch); }
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

    private void RepairBatches()
    {
        PhysicsGraphBatch? small = null;
        foreach (Island island in _islands)
        {
            if (!IsActive(island)) continue;
            if (island.Batch is not null) continue;
            bool isSmall = !IsColored(island) && island.DynamicBodies.Count <= _settings.SmallIslandBodyLimit;
            if (!isSmall || small is null || small.DynamicCount + island.DynamicBodies.Count > _settings.BodiesPerBatch)
            {
                PhysicsGraphBatch batch = TakeBatch();
                Batches.Add(batch); small = isSmall ? batch : null;
                batch.AddIsland(island);
            }
            else small.AddIsland(island);
        }
        foreach (PhysicsGraphBatch batch in Batches) batch.OrderBodies(_bodies);
        Batches.Sort((a, b) => FirstOrder(a.Islands[0]).CompareTo(FirstOrder(b.Islands[0])));
    }

    private void BuildProjectionColors(Island island)
    {
        for (int query = 0; query < _queryCount; query++)
        {
            var colors = new List<List<ConstraintInput>>();
            foreach (ConstraintInput constraint in island.Records.Where(row => row.Identity.Query == query))
            {
                int color = 0;
                for (; color < colors.Count; color++)
                    if (colors[color].All(other => !Conflicts(constraint, other))) break;
                if (color == colors.Count) colors.Add([]);
                colors[color].Add(constraint);
            }
            for (int color = 0; color < colors.Count; color++)
            {
                var group = new ProjectionColor(query, color);
                for (int first = 0; first < colors[color].Count; first += _settings.RowsPerBlock)
                {
                    PhysicsGraphBatch batch = TakeBatch();
                    foreach (ConstraintInput constraint in colors[color].Skip(first).Take(_settings.RowsPerBlock))
                        batch.AddConstraint(constraint.Bodies, constraint.References, constraint.Identity, _bodies);
                    batch.OrderBodies(_bodies); group.Batches.Add(batch);
                }
                island.ProjectionColors.Add(group);
            }
        }
    }

    private bool Conflicts(ConstraintInput first, ConstraintInput second)
    {
        foreach (var use in first.Bodies)
            if (_bodies[use.Key].Motion == BodyMotion.Dynamic && second.Bodies.TryGetValue(use.Key, out bool writes) && (use.Value || writes)) return true;
        return false;
    }

    private void BuildBodyColors(Island island)
    {
        var assigned = new Dictionary<Entity, int>();
        var colors = new List<List<Entity>>();
        var used = new HashSet<int>();
        foreach (Entity body in island.BodyOrder)
        {
            used.Clear();
            foreach (ConstraintInput constraint in _bodies[body].Constraints)
                foreach (Entity neighbor in constraint.Bodies.Keys)
                    if (assigned.TryGetValue(neighbor, out int previous)) used.Add(previous);
            int color = 0; while (used.Contains(color)) color++;
            assigned.Add(body, color);
            while (colors.Count <= color) colors.Add([]);
            colors[color].Add(body);
        }
        foreach (List<Entity> color in colors)
        {
            var blocks = new List<PhysicsGraphBatch>();
            for (int first = 0; first < color.Count; first += _settings.RowsPerBlock)
            {
                PhysicsGraphBatch batch = TakeBatch();
                foreach (Entity body in color.Skip(first).Take(_settings.RowsPerBlock))
                {
                    batch.AddBody(body);
                    foreach (ConstraintInput constraint in _bodies[body].Constraints)
                        batch.AddConstraint(constraint.Bodies, constraint.References, constraint.Identity, _bodies, writes: false);
                }
                batch.OrderBodies(_bodies); blocks.Add(batch);
            }
            island.BodyColors.Add(blocks);
        }
    }

    private void CollectWork()
    {
        ScalarBatches.Clear(); ProjectionGroups.Clear(); BodyGroups.Clear(); Writes.Clear(); References.Clear();
        var writes = new HashSet<Entity>();
        foreach (PhysicsGraphBatch batch in Batches)
            if (!IsColored(batch.Islands[0])) ScalarBatches.Add(batch);
        var projections = new SortedDictionary<(int Query, int Color), List<PhysicsGraphBatch>>();
        foreach (Island island in _islands)
        {
            if (!IsActive(island)) continue;
            writes.UnionWith(island.Writes);
            foreach (ProjectionColor color in island.ProjectionColors)
            {
                var key = (color.Query, color.Color);
                if (!projections.TryGetValue(key, out List<PhysicsGraphBatch>? group)) projections.Add(key, group = []);
                group.AddRange(color.Batches);
            }
            for (int color = 0; color < island.BodyColors.Count; color++)
            {
                while (BodyGroups.Count <= color) BodyGroups.Add([]);
                BodyGroups[color].AddRange(island.BodyColors[color]);
            }
        }
        ProjectionGroups.AddRange(projections.Values);
        Writes.AddRange(writes.OrderBy(body => _bodies[body].Order));
        CollectReferences();
    }

    private void CollectReferences()
    {
        _references.Clear();
        foreach (Island island in _islands) _references.UnionWith(island.References);
        References.Clear(); References.AddRange(_references);
    }

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
    private bool IsActive(Island island) => island.DynamicBodies.Count == 0 || island.DynamicBodies.Any(body => _bodies[body].Awake);
    internal bool IsConstraintActive(int query, Entity entity) =>
        _constraints.TryGetValue(new(query, entity), out ConstraintInput? constraint) && constraint.Island?.Batch is not null;
    internal IReadOnlyList<Island> ActivityIslands => _islands;
    internal bool HasActivityChange(Entity body) => _activityChanges.Contains(body);
    internal void ClearActivityChanges() => _activityChanges.Clear();
    internal sealed class BodyInput
    {
        internal BodyMotion Motion;
        internal bool Awake = true;
        internal long Read;
        internal int Order;
        internal Island? Island;
        internal readonly HashSet<ConstraintInput> Constraints = [];
    }
    internal sealed class ConstraintInput(PhysicsConstraintIdentity identity)
    {
        internal readonly PhysicsConstraintIdentity Identity = identity;
        internal readonly Dictionary<Entity, bool> Bodies = [];
        internal readonly HashSet<Entity> References = [];
        internal long Read;
        internal int Order;
        internal Island? Island;
    }
    internal sealed class Island(int count)
    {
        internal readonly HashSet<Entity> DynamicBodies = [], Reads = [], Writes = [], References = [];
        internal readonly List<Entity> BodyOrder = [];
        internal readonly List<ConstraintInput> Records = [];
        internal readonly List<Entity>[] Constraints = Enumerable.Range(0, count).Select(static _ => new List<Entity>()).ToArray();
        internal readonly List<ProjectionColor> ProjectionColors = [];
        internal readonly List<List<PhysicsGraphBatch>> BodyColors = [];
        internal PhysicsGraphBatch? Batch;
        internal void Clear()
        {
            DynamicBodies.Clear(); Reads.Clear(); Writes.Clear(); References.Clear(); BodyOrder.Clear(); Records.Clear();
            foreach (List<Entity> rows in Constraints) rows.Clear();
            ProjectionColors.Clear(); BodyColors.Clear(); Batch = null;
        }
    }
    internal sealed class ProjectionColor(int query, int color)
    {
        internal readonly int Query = query, Color = color;
        internal readonly List<PhysicsGraphBatch> Batches = [];
    }
}

internal sealed class PhysicsGraphBatch(int queryCount)
{
    private readonly List<Entity>[] _entities = Enumerable.Range(0, queryCount).Select(static _ => new List<Entity>()).ToArray();
    private readonly HashSet<Entity>[] _unique = Enumerable.Range(0, queryCount).Select(static _ => new HashSet<Entity>()).ToArray();
    internal readonly List<PhysicsGraphData.Island> Islands = [];
    internal int DynamicCount => Islands.Sum(static island => island.DynamicBodies.Count);
    internal ReadOnlySpan<Entity> Entities(int query) => CollectionsMarshal.AsSpan(_entities[query]);
    internal void Clear()
    {
        foreach (List<Entity> entities in _entities) entities.Clear();
        foreach (HashSet<Entity> entities in _unique) entities.Clear();
        Islands.Clear();
    }
    internal void AddIsland(PhysicsGraphData.Island island)
    {
        Islands.Add(island); island.Batch = this;
        Add(0, island.Reads); Add(1, island.Writes); Add(2, island.References);
        for (int index = 0; index < island.Constraints.Length; index++) Add(index + 3, island.Constraints[index]);
    }
    internal void AddBody(Entity body) { Add(0, body); Add(1, body); }
    internal void AddConstraint(Dictionary<Entity, bool> bodies, HashSet<Entity> references,
        PhysicsConstraintIdentity identity, Dictionary<Entity, PhysicsGraphData.BodyInput> masses, bool writes = true)
    {
        foreach (var use in bodies)
        {
            Add(0, use.Key);
            if (writes && use.Value && masses[use.Key].Motion == BodyMotion.Dynamic) Add(1, use.Key);
        }
        Add(2, references); Add(identity.Query + 3, identity.Entity);
    }
    private void Add(int query, Entity entity) { if (_unique[query].Add(entity)) _entities[query].Add(entity); }
    private void Add(int query, IEnumerable<Entity> entities) { foreach (Entity entity in entities) Add(query, entity); }
    internal void OrderBodies(Dictionary<Entity, PhysicsGraphData.BodyInput> bodies)
    {
        _entities[0].Sort((a, b) => bodies[a].Order.CompareTo(bodies[b].Order));
        _entities[1].Sort((a, b) => bodies[a].Order.CompareTo(bodies[b].Order));
    }

    internal void RefreshReferences(Dictionary<PhysicsConstraintIdentity, PhysicsGraphData.ConstraintInput> constraints)
    {
        _entities[2].Clear(); _unique[2].Clear();
        for (int query = 3; query < _entities.Length; query++)
            foreach (Entity entity in _entities[query])
                Add(2, constraints[new(query - 3, entity)].References);
    }
}
