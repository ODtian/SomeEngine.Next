using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal readonly record struct RelationEdit<TEntity>(TEntity Edge, TEntity Source, TEntity Target) where TEntity : struct;

internal interface IRelationScenario : IDisposable
{
    int EndpointCount { get; }
    int EdgeCount { get; }
    void Cycle();
    void Grow();
    bool Check();
    bool CheckClaims();
    bool CheckLifetimes();
}

internal interface IRelationComparison<TEntity> : IDisposable where TEntity : struct
{
    ulong Version { get; }
    bool Active(TEntity entity);
    TEntity CreateEndpoint(int ordinal);
    TEntity CreateEdge(TEntity source, TEntity target, int ordinal);
    void Retarget(ReadOnlySpan<RelationEdit<TEntity>> edits, bool deferred);
    void Maintain();
    void DestroyEdge(TEntity edge);
    void DestroyEndpoint(TEntity endpoint);
    void DestroyDeferred(TEntity entity);
    void Observe(RelationObservation<TEntity> result, ulong since);
    void ObserveAdjacency(TEntity endpoint, RelationObservation<TEntity> result);
    void SetOrdered(TEntity endpoint);
    void Reorder(TEntity endpoint, TEntity firstEdge, bool incoming);
    void ClearFacts();
}

internal sealed class RelationObservation<TEntity> where TEntity : struct
{
    internal readonly Dictionary<TEntity, int> Endpoints, Edges;
    internal readonly int[] Sources, Targets, OutSources, OutTargets, InSources, InTargets, OldSources, OldTargets, PayloadOrdinals, IncidentCounts;
    internal readonly long[] PayloadValues;
    internal readonly byte[] Facts;
    internal readonly int[] HeadCounts, HeadFirst, HeadLast;
    internal int ActiveEndpoints, ActiveEdges, MainCount;
    internal bool Unique;
    internal RelationObservation(int edges, int endpointCapacity, Dictionary<TEntity, int> endpoints, Dictionary<TEntity, int> edgeOrdinals)
    {
        Endpoints = endpoints; Edges = edgeOrdinals;
        Sources = new int[edges]; Targets = new int[edges];
        OutSources = new int[edges]; OutTargets = new int[edges]; InSources = new int[edges]; InTargets = new int[edges];
        OldSources = new int[edges]; OldTargets = new int[edges]; PayloadOrdinals = new int[edges];
        PayloadValues = new long[edges]; Facts = new byte[edges]; IncidentCounts = new int[edges];
        HeadCounts = new int[endpointCapacity * 2]; HeadFirst = new int[endpointCapacity * 2]; HeadLast = new int[endpointCapacity * 2];
    }
    internal void Reset()
    {
        Array.Fill(Sources, -2); Array.Fill(Targets, -2);
        Array.Fill(OutSources, -1); Array.Fill(OutTargets, -1); Array.Fill(InSources, -1); Array.Fill(InTargets, -1);
        Array.Fill(OldSources, -1); Array.Fill(OldTargets, -1); Array.Fill(PayloadOrdinals, -1);
        Array.Clear(Facts); Array.Clear(IncidentCounts); Array.Clear(PayloadValues);
        Array.Clear(HeadCounts); Array.Fill(HeadFirst, -1); Array.Fill(HeadLast, -1);
        ActiveEndpoints = ActiveEdges = MainCount = 0; Unique = true;
    }
    internal void Main(TEntity edge, TEntity source, TEntity target, int ordinal, long value)
    {
        int i = Edges[edge]; Unique &= Sources[i] == -2; MainCount++;
        Sources[i] = Endpoints[source]; Targets[i] = Endpoints[target]; PayloadOrdinals[i] = ordinal; PayloadValues[i] = value;
    }
    internal void Fact(TEntity edge, byte flag) => Facts[Edges[edge]] |= flag;
    internal void Removed(TEntity edge, TEntity source, TEntity target)
    {
        int i = Edges[edge]; Facts[i] |= 4; OldSources[i] = Endpoints[source]; OldTargets[i] = Endpoints[target];
    }
    internal void Adjacency(TEntity endpoint, TEntity other, TEntity edge, int role)
    {
        int i = Edges[edge], a = Endpoints[endpoint], b = Endpoints[other];
        int head = a * 2 + (role == 1 ? 1 : 0);
        if (HeadCounts[head]++ == 0) HeadFirst[head] = i;
        HeadLast[head] = i;
        if (role == 0) { Unique &= OutSources[i] == -1; OutSources[i] = a; OutTargets[i] = b; }
        else if (role == 1) { Unique &= InSources[i] == -1; InSources[i] = b; InTargets[i] = a; }
        else
        {
            int count = IncidentCounts[i]++;
            if (count == 0) { OutSources[i] = Math.Min(a, b); OutTargets[i] = Math.Max(a, b); }
            else { Unique &= count == 1; InSources[i] = Math.Min(a, b); InTargets[i] = Math.Max(a, b); }
        }
    }
}

internal sealed class RelationScenario<TEntity, TApi> : IRelationScenario
    where TEntity : struct where TApi : IRelationComparison<TEntity>
{
    private readonly TApi _api;
    private readonly bool _directed, _matching, _ordered;
    private readonly int[] _initialOrder, _movedOrder, _deletedOrder;
    private readonly TEntity[] _baseEndpoints, _endpoints, _edges, _growingEndpoints, _growingEdges;
    private readonly Dictionary<TEntity, int> _endpointOrdinals, _edgeOrdinals;
    private readonly RelationEdit<TEntity>[] _edits;
    private readonly RelationObservation<TEntity> _deferred, _moved, _deleted, _restored, _grown;
    private bool _growthValid = true;
    public int EndpointCount => _endpoints.Length;
    public int EdgeCount => _edges.Length;

    internal RelationScenario(TApi api, TEntity[] initial, bool directed, bool matching, bool ordered)
    {
        _api = api; _baseEndpoints = initial; _directed = directed; _matching = matching; _ordered = ordered;
        _endpoints = new TEntity[initial.Length * (matching ? 2 : 1)];
        initial.CopyTo(_endpoints, 0);
        for (int i = initial.Length; i < _endpoints.Length; i++) _endpoints[i] = api.CreateEndpoint(i);
        _edges = new TEntity[initial.Length];
        _growingEndpoints = new TEntity[_endpoints.Length]; _growingEdges = new TEntity[_edges.Length];
        _endpointOrdinals = new(_endpoints.Length * 2); _edgeOrdinals = new(_edges.Length * 2);
        for (int i = 0; i < _endpoints.Length; i++) _endpointOrdinals.Add(_endpoints[i], i);
        for (int i = 0; i < _edges.Length; i++)
        {
            _edges[i] = api.CreateEdge(_endpoints[Source(i)], _endpoints[Target(i)], i); _edgeOrdinals.Add(_edges[i], i);
        }
        if (ordered) foreach (TEntity endpoint in _endpoints) api.SetOrdered(endpoint);
        _edits = new RelationEdit<TEntity>[_edges.Length / 4];
        _deferred = new(_edges.Length * 2, _endpoints.Length * 2, _endpointOrdinals, _edgeOrdinals);
        _moved = new(_edges.Length * 2, _endpoints.Length * 2, _endpointOrdinals, _edgeOrdinals);
        _deleted = new(_edges.Length * 2, _endpoints.Length * 2, _endpointOrdinals, _edgeOrdinals);
        _restored = new(_edges.Length * 2, _endpoints.Length * 2, _endpointOrdinals, _edgeOrdinals);
        _grown = new(_edges.Length * 2, _endpoints.Length * 2, _endpointOrdinals, _edgeOrdinals);
        _initialOrder = new int[_endpoints.Length * 8];
        Array.Fill(_initialOrder, -1);
        for (int head = 0; head < _endpoints.Length * 2; head++) _initialOrder[head * 4] = 0;
        for (int i = 0; i < _edges.Length; i++) AddOrder(_initialOrder, i, i);
        _movedOrder = (int[])_initialOrder.Clone();
        for (int i = 0; i < _edges.Length; i += 4) { RemoveOrder(_movedOrder, i, i); AddOrder(_movedOrder, i, MovedPair(i)); }
        _deletedOrder = (int[])_movedOrder.Clone();
        for (int i = 0; i < _edges.Length; i++) if (Deleted(i)) RemoveOrder(_deletedOrder, i, MovedPair(i));
        api.Maintain(); api.ClearFacts();
    }
    private void AddOrder(int[] order, int edge, int pair)
    {
        Add(Source(pair) * 2); Add(Target(pair) * 2 + (_directed ? 1 : 0));
        void Add(int head)
        {
            int p = head * 4;
            order[p + ++order[p]] = edge;
        }
    }
    private void RemoveOrder(int[] order, int edge, int pair)
    {
        Remove(Source(pair) * 2); Remove(Target(pair) * 2 + (_directed ? 1 : 0));
        void Remove(int head)
        {
            int p = head * 4, count = order[p], place = 1;
            while (order[p + place] != edge) place++;
            order.AsSpan(p + place + 1, count - place).CopyTo(order.AsSpan(p + place));
            order[p + count] = -1; order[p]--;
        }
    }
    private bool CheckOrder(RelationObservation<TEntity> result, int[] order)
    {
        bool correct = true;
        for (int head = 0; head < _endpoints.Length * 2; head++)
        {
            int p = head * 4, count = order[p];
            correct &= result.HeadCounts[head] == count
                && (!_ordered || result.HeadFirst[head] == order[p + 1] && result.HeadLast[head] == (count == 0 ? -1 : order[p + count]));
        }
        return correct;
    }
    private int Source(int edge) => _matching ? edge * 2 : edge;
    private int Target(int edge) => _matching ? edge * 2 + 1 : (edge + 1) % _edges.Length;
    private int MovedPair(int edge) => edge % 4 == 0 ? (edge + 4) % _edges.Length : edge;
    private bool Deleted(int edge) => edge % 4 == 0 || !_matching && edge == _edges.Length - 1;

    private void Observe(RelationObservation<TEntity> result, ulong since, bool growth = false)
    {
        result.Reset();
        foreach (TEntity endpoint in _endpoints)
        {
            if (!_api.Active(endpoint)) continue;
            result.ActiveEndpoints++; _api.ObserveAdjacency(endpoint, result);
        }
        foreach (TEntity edge in _edges) if (_api.Active(edge)) result.ActiveEdges++;
        if (growth)
        {
            foreach (TEntity endpoint in _growingEndpoints)
            {
                if (!_api.Active(endpoint)) continue;
                result.ActiveEndpoints++; _api.ObserveAdjacency(endpoint, result);
            }
            foreach (TEntity edge in _growingEdges) if (_api.Active(edge)) result.ActiveEdges++;
        }
        _api.Observe(result, since);
    }
    public void Cycle()
    {
        ulong before = _api.Version;
        for (int i = 0; i < _edits.Length; i++)
        {
            int edge = i * 4, pair = (edge + 4) % _edges.Length;
            _edits[i] = new(_edges[edge], _endpoints[Source(pair)], _endpoints[Target(pair)]);
        }
        _api.Retarget(_edits, true);
        Observe(_deferred, before);
        _api.Maintain();
        Observe(_moved, before); _api.ClearFacts();
        before = _api.Version;
        for (int i = 0; i < _edges.Length; i += 4) _api.DestroyEdge(_edges[i]);
        _api.DestroyEndpoint(_endpoints[0]);
        Observe(_deleted, before);
        _api.ClearFacts();
        _endpointOrdinals.Remove(_endpoints[0]); _endpoints[0] = _api.CreateEndpoint(0); _endpointOrdinals.Add(_endpoints[0], 0);
        _baseEndpoints[0] = _endpoints[0];
        if (_ordered) _api.SetOrdered(_endpoints[0]);
        for (int i = 0; i < _edges.Length; i++)
        {
            if (!Deleted(i)) continue;
            _edgeOrdinals.Remove(_edges[i]); _edges[i] = _api.CreateEdge(_endpoints[Source(i)], _endpoints[Target(i)], i); _edgeOrdinals.Add(_edges[i], i);
        }
        RestoreOrder();
        _api.Maintain(); _api.ClearFacts();
    }
    private void RestoreOrder()
    {
        if (_ordered && !_directed)
            for (int i = 0; i < _endpoints.Length; i++) _api.Reorder(_endpoints[i], _edges[_initialOrder[i * 8 + 1]], false);
    }
    public bool CheckClaims()
    {
        ulong before = _api.Version;
        RelationEdit<TEntity>[] conflict = [new(_edges[0], _endpoints[Source(0)], _endpoints[Target(0)]), new(_edges[4], _endpoints[Source(0)], _endpoints[Target(0)])];
        bool rejected = false;
        try { _api.Retarget(conflict, false); } catch (InvalidOperationException) { rejected = true; }
        Observe(_restored, before);
        bool correct = rejected && _api.Version == before && _restored.Unique && CheckOrder(_restored, _initialOrder);
        for (int i = 0; i < _edges.Length; i++) correct &= CheckPair(_restored, i, i, true) && _restored.Facts[i] == 0;
        RelationEdit<TEntity>[] swap = [new(_edges[0], _endpoints[Source(4)], _endpoints[Target(4)]), new(_edges[4], _endpoints[Source(0)], _endpoints[Target(0)])];
        _api.Retarget(swap, false);
        Observe(_restored, before);
        correct &= _api.Version == before + 1 && CheckPair(_restored, 0, 4, true) && CheckPair(_restored, 4, 0, true)
            && _restored.Facts[0] == 2 && _restored.Facts[4] == 2;
        RelationEdit<TEntity>[] restore = [new(_edges[0], _endpoints[Source(0)], _endpoints[Target(0)]), new(_edges[4], _endpoints[Source(4)], _endpoints[Target(4)])];
        _api.Retarget(restore, false); RestoreOrder(); _api.Maintain(); _api.ClearFacts();
        return correct;
    }
    public bool CheckLifetimes()
    {
        TEntity oldEdge = _edges[0];
        _api.DestroyDeferred(oldEdge); _api.ClearFacts();
        _edges[0] = _api.CreateEdge(_endpoints[Source(0)], _endpoints[Target(0)], 0);
        _edgeOrdinals.Add(_edges[0], 0);
        _api.Maintain();
        Observe(_restored, _api.Version);
        bool correct = !_api.Active(oldEdge) && _api.Active(_edges[0]) && _restored.Unique && CheckPair(_restored, 0, 0, true);
        _edgeOrdinals.Remove(oldEdge);
        TEntity oldEndpoint = _endpoints[0];
        _api.DestroyDeferred(oldEndpoint);
        _endpoints[0] = _api.CreateEndpoint(0); _endpointOrdinals.Add(_endpoints[0], 0);
        _baseEndpoints[0] = _endpoints[0];
        if (_ordered) _api.SetOrdered(_endpoints[0]);
        TEntity extraTarget = _api.CreateEndpoint(_endpoints.Length), extraEdge = _api.CreateEdge(_endpoints[0], extraTarget, _edges.Length);
        _endpointOrdinals.Add(extraTarget, _endpoints.Length); _edgeOrdinals.Add(extraEdge, _edges.Length);
        _api.Maintain();
        correct &= !_api.Active(oldEndpoint) && _api.Active(_endpoints[0]) && _api.Active(extraEdge) && !_api.Active(_edges[0]);
        _api.DestroyEdge(extraEdge); _api.DestroyEndpoint(extraTarget); _api.ClearFacts();
        _edgeOrdinals.Remove(extraEdge); _endpointOrdinals.Remove(extraTarget); _endpointOrdinals.Remove(oldEndpoint);
        for (int i = 0; i < _edges.Length; i++)
            if (!_api.Active(_edges[i]))
            {
                _edgeOrdinals.Remove(_edges[i]); _edges[i] = _api.CreateEdge(_endpoints[Source(i)], _endpoints[Target(i)], i); _edgeOrdinals.Add(_edges[i], i);
            }
        RestoreOrder(); _api.Maintain(); _api.ClearFacts();
        Observe(_restored, _api.Version);
        correct &= _restored.Unique && _restored.MainCount == _edges.Length && CheckOrder(_restored, _initialOrder);
        for (int i = 0; i < _edges.Length; i++) correct &= CheckPair(_restored, i, i, true);
        return correct;
    }
    private bool CheckPair(RelationObservation<TEntity> result, int edge, int pair, bool main, int endpointOffset = 0)
    {
        int source = Source(pair) + endpointOffset, target = Target(pair) + endpointOffset;
        bool correct = true;
        if (main) correct &= result.Sources[edge] == source && result.Targets[edge] == target
            && result.PayloadOrdinals[edge] == edge && result.PayloadValues[edge] == edge * 3L + 7;
        if (!_directed) (source, target) = (Math.Min(source, target), Math.Max(source, target));
        return correct && result.OutSources[edge] == source && result.OutTargets[edge] == target
            && result.InSources[edge] == source && result.InTargets[edge] == target;
    }
    public void Grow()
    {
        for (int i = 0; i < _growingEndpoints.Length; i++)
        {
            _growingEndpoints[i] = _api.CreateEndpoint(_endpoints.Length + i);
            _endpointOrdinals.Add(_growingEndpoints[i], _endpoints.Length + i);
            if (_ordered) _api.SetOrdered(_growingEndpoints[i]);
        }
        for (int i = 0; i < _growingEdges.Length; i++)
        {
            _growingEdges[i] = _api.CreateEdge(_growingEndpoints[Source(i)], _growingEndpoints[Target(i)], _edges.Length + i);
            _edgeOrdinals.Add(_growingEdges[i], _edges.Length + i);
        }
        Observe(_grown, _api.Version, true);
        bool valid = _grown.Unique && _grown.MainCount == _edges.Length * 2
            && _grown.ActiveEdges == _edges.Length * 2 && _grown.ActiveEndpoints == _endpoints.Length * 2;
        for (int i = 0; i < _growingEdges.Length; i++) valid &= CheckPair(_grown, _edges.Length + i, i, true, _endpoints.Length);
        foreach (TEntity endpoint in _growingEndpoints) _api.DestroyEndpoint(endpoint);
        foreach (TEntity edge in _growingEdges) valid &= !_api.Active(edge);
        _api.ClearFacts(); _api.Maintain();
        foreach (TEntity edge in _growingEdges) _edgeOrdinals.Remove(edge);
        foreach (TEntity endpoint in _growingEndpoints) _endpointOrdinals.Remove(endpoint);
        _growthValid = valid;
    }
    public bool Check()
    {
        Observe(_restored, _api.Version);
        int dead = _edges.Length / 4 + (_matching ? 0 : 1);
        bool correct = _growthValid && _deferred.Unique && _moved.Unique && _deleted.Unique && _restored.Unique
            && _deferred.ActiveEdges == _edges.Length && _moved.ActiveEdges == _edges.Length
            && _deleted.ActiveEdges == _edges.Length - dead && _deleted.ActiveEndpoints == _endpoints.Length - 1
            && _restored.ActiveEdges == _edges.Length && _restored.ActiveEndpoints == _endpoints.Length
            && _deferred.MainCount == _edges.Length && _moved.MainCount == _edges.Length
            && _deleted.MainCount == _edges.Length - dead && _restored.MainCount == _edges.Length
            && CheckOrder(_deferred, _initialOrder) && CheckOrder(_moved, _movedOrder)
            && CheckOrder(_deleted, _deletedOrder) && CheckOrder(_restored, _initialOrder);
        for (int i = 0; i < _edges.Length; i++)
        {
            int moved = MovedPair(i);
            byte fact = i % 4 == 0 ? (byte)2 : (byte)0;
            correct &= _deferred.Sources[i] == Source(moved) && _deferred.Targets[i] == Target(moved)
                && _deferred.PayloadOrdinals[i] == i && _deferred.PayloadValues[i] == i * 3L + 7
                && CheckPair(_deferred, i, i, false) && _deferred.Facts[i] == fact;
            correct &= CheckPair(_moved, i, moved, true) && _moved.Facts[i] == fact && CheckPair(_restored, i, i, true);
            if (Deleted(i)) correct &= _deleted.Sources[i] == -2 && _deleted.OutSources[i] == -1 && _deleted.InSources[i] == -1
                && _deleted.Facts[i] == 4 && _deleted.OldSources[i] == Source(moved) && _deleted.OldTargets[i] == Target(moved);
            else correct &= CheckPair(_deleted, i, i, true) && _deleted.Facts[i] == 0;
        }
        return correct;
    }
    public void Dispose() => _api.Dispose();
}

internal sealed class SomeRelationComparison<TPayload> : IRelationComparison<Entity> where TPayload : struct, IRelationPayload<TPayload>
{
    private readonly World _world;
    private readonly RelationRetarget<TPayload>[] _edits;
    private readonly Query _main, _added, _changed, _removed;
    private readonly bool _directed = RelationSchema.For<TPayload>().Direction == RelationDirection.Directed;
    internal SomeRelationComparison(World world, int count)
    {
        _world = world; _edits = new RelationRetarget<TPayload>[count];
        _main = world.Query<RelationEndpoints<TPayload>, TPayload>();
        _added = world.Query(new QueryDescription().WithAdded<RelationEndpoints<TPayload>>(0));
        _changed = world.Query(new QueryDescription().WithChanged<RelationEndpoints<TPayload>>(0));
        _removed = world.Query<Removed<RelationEndpoints<TPayload>>>();
    }
    public ulong Version => _world.Version;
    public bool Active(Entity entity) => _world.IsAlive(entity) && !_world.IsPendingCleanup(entity);
    public Entity CreateEndpoint(int ordinal)
    {
        Body body = Body.Moving(new Position { X = ordinal });
        return ordinal % 4 == 0 ? _world.Spawn(new SleepingTeamBody { Body = body })
            : ordinal % 2 == 0 ? _world.Spawn(new TeamBody { Body = body }) : _world.Spawn(body);
    }
    public Entity CreateEdge(Entity source, Entity target, int ordinal) => Relations<TPayload>.CreateEdge(_world, source, target, TPayload.Create(ordinal)).Entity;
    public void Retarget(ReadOnlySpan<RelationEdit<Entity>> edits, bool deferred)
    {
        for (int i = 0; i < edits.Length; i++) _edits[i] = new(new(edits[i].Edge), edits[i].Source, edits[i].Target);
        Relations<TPayload>.Retarget(_world, _edits.AsSpan(0, edits.Length), deferred);
    }
    public void Maintain() => Relations<TPayload>.Maintain(_world);
    public void DestroyEdge(Entity edge) => Relations<TPayload>.DestroyEdge(_world, new(edge));
    public void DestroyEndpoint(Entity endpoint) => _world.Destroy(endpoint);
    public void DestroyDeferred(Entity entity) => _world.DestroyDeferred(entity);
    public void Observe(RelationObservation<Entity> result, ulong since)
    {
        using (var scope = _main.Open())
            foreach (var rows in scope)
            {
                var endpoints = rows.Read<RelationEndpoints<TPayload>>(); var payloads = rows.Read<TPayload>(); var entities = rows.Entities;
                for (int i = 0; i < endpoints.Length; i++) result.Main(entities[i], endpoints[i].Source, endpoints[i].Target, payloads[i].Ordinal, payloads[i].Value);
            }
        using (var scope = _added.Since(since).Open()) foreach (var rows in scope) foreach (Entity edge in rows.Entities) result.Fact(edge, 1);
        using (var scope = _changed.Since(since).Open()) foreach (var rows in scope) foreach (Entity edge in rows.Entities) result.Fact(edge, 2);
        using (var scope = _removed.Open())
            foreach (var rows in scope)
            {
                var endpoints = rows.Read<Removed<RelationEndpoints<TPayload>>>(); var entities = rows.Entities;
                for (int i = 0; i < endpoints.Length; i++) if (endpoints[i].Version > since) result.Removed(entities[i], endpoints[i].Value.Source, endpoints[i].Value.Target);
            }
    }
    public void ObserveAdjacency(Entity endpoint, RelationObservation<Entity> result)
    {
        if (_directed)
        {
            using (var entries = Relations<TPayload>.Outgoing(_world, endpoint)) foreach (var entry in entries) result.Adjacency(endpoint, entry.Other, entry.Edge, 0);
            using (var entries = Relations<TPayload>.Incoming(_world, endpoint)) foreach (var entry in entries) result.Adjacency(endpoint, entry.Other, entry.Edge, 1);
        }
        else using (var entries = Relations<TPayload>.Incident(_world, endpoint)) foreach (var entry in entries) result.Adjacency(endpoint, entry.Other, entry.Edge, 2);
    }
    public void SetOrdered(Entity endpoint)
    {
        if (_directed)
        {
            Relations<TPayload>.SetOrdered(_world, endpoint, RelationAdjacencyRole.Outgoing, true);
            Relations<TPayload>.SetOrdered(_world, endpoint, RelationAdjacencyRole.Incoming, true);
        }
        else Relations<TPayload>.SetOrdered(_world, endpoint, RelationAdjacencyRole.Incident, true);
    }
    public void Reorder(Entity endpoint, Entity firstEdge, bool incoming)
        => Relations<TPayload>.Reorder(_world, endpoint, _directed ? incoming ? RelationAdjacencyRole.Incoming : RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident, new(firstEdge), 0);
    public void ClearFacts() => _world.ClearRemoved<RelationEndpoints<TPayload>>();
    public void Dispose() { }
}

internal abstract partial class ComparisonWorld
{
    internal IRelationScenario? RelationScenario;
    internal void RelationCycle() => RelationScenario!.Cycle();
    internal int ExpectedEntityCount => RelationScenario is { } relations ? relations.EndpointCount + relations.EdgeCount
        : Count + (CopyScenario is not null ? 1 : 0);
}
