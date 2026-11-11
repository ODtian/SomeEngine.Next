using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

[Friflo.Engine.ECS.GenericInstanceType("ReferenceRelationEndpoints", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveRelationEndpoints", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceRelationEndpoints<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity Source, Target;
    public ulong Added, Written;
}

[Friflo.Engine.ECS.GenericInstanceType("ReferenceOldRelationEndpoints", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveOldRelationEndpoints", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceOldRelationEndpoints<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity Source, Target;
    public ulong Version;
}

internal readonly record struct ReferenceRelationEntry<TEntity>(TEntity Other, TEntity Edge) where TEntity : struct;

internal interface IRelationValues<TEntity, TIdentity, TPayload> : IDisposable
    where TEntity : struct where TIdentity : struct, IEquatable<TIdentity> where TPayload : struct, IRelationPayload<TPayload>
{
    int Index(TEntity entity);
    TIdentity Identity(TEntity entity);
    bool Alive(TEntity entity);
    bool Has<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    T Read<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Set<T>(TEntity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Remove<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    TEntity CreateEndpoint(int ordinal);
    TEntity CreateEdge(in ReferenceRelationEndpoints<TEntity> endpoints, in TPayload payload);
    void Destroy(TEntity entity);
    void Observe(RelationObservation<TEntity> result, ulong since);
}

internal sealed partial class ReferenceRelations<TEntity, TIdentity, TValues, TPayload> : IRelationComparison<TEntity>
    where TEntity : struct where TIdentity : struct, IEquatable<TIdentity> where TValues : struct, IRelationValues<TEntity, TIdentity, TPayload>
    where TPayload : struct, IRelationPayload<TPayload>
{
    private struct Head
    {
        internal TIdentity Identity;
        internal ReferenceRelationEntry<TEntity>[]? Items;
        internal int CountFlags;
        internal int Count { readonly get => CountFlags & int.MaxValue; set => CountFlags = value | (CountFlags & int.MinValue); }
        internal bool Ordered { readonly get => CountFlags < 0; set => CountFlags = Count | (value ? int.MinValue : 0); }
    }
    private struct Applied
    {
        internal TEntity Source, Target;
        internal TIdentity Identity;
        internal int SourcePlace, TargetPlace;
    }
    private readonly record struct Pair(TEntity Source, TEntity Target);
    private readonly record struct HeadKey(TEntity Entity, bool Incoming);
    private readonly record struct EntityIdentity(int Id, TIdentity Identity);
    private readonly record struct HeadIdentity(int Id, TIdentity Identity, bool Incoming);
    private readonly record struct EndpointClaim(TIdentity Identity, TEntity Edge);
    private readonly TValues _values;
    private readonly bool _directed, _singleSource, _singleTarget, _uniquePair;
    private Head[] _outgoing, _incoming;
    private Applied[] _applied;
    private EndpointClaim[]? _sourceClaims, _targetClaims;
    private readonly Dictionary<Pair, TEntity>? _pairs;
    private readonly Dictionary<TEntity, RelationEdit<TEntity>> _edits = new();
    private readonly Dictionary<Pair, TEntity> _finalPairs = new();
    private readonly Dictionary<TEntity, TEntity> _finalSources = new(), _finalTargets = new();
    private readonly HashSet<TEntity> _pending = new(), _destroyed = new();
    private readonly Dictionary<HeadIdentity, Head> _retiredHeads = new();
    private readonly Dictionary<EntityIdentity, Applied> _retiredApplied = new();
    private readonly List<TEntity> _destroyedWork = new();
    private readonly Dictionary<TEntity, HashSet<TEntity>> _future = new();
    private readonly Stack<HashSet<TEntity>> _futureSets = new();
    private readonly List<TEntity> _removed = new();
    private readonly Dictionary<HeadKey, List<TEntity>> _futureOrders = new();
    private readonly HashSet<TEntity> _orderMembers = new();
    private bool _usesOrder;
    private readonly ReferenceVersionClock? _clock;
    private ulong _version;
    public ulong Version
    {
        get => _clock?.Value ?? _version;
        private set { if (_clock is { } clock) clock.Value = value; else _version = value; }
    }
    private static bool Equal(TEntity a, TEntity b) => EqualityComparer<TEntity>.Default.Equals(a, b);
    private static bool Empty(TEntity entity) => Equal(entity, default);

    internal ReferenceRelations(TValues values, int capacity, ReferenceVersionClock? clock = null)
    {
        _values = values; _clock = clock;
        RelationSchema schema = RelationSchema.For<TPayload>();
        _directed = schema.Direction == RelationDirection.Directed;
        _singleSource = schema.Cardinality is RelationCardinality.UniqueSource or RelationCardinality.OneToOne;
        _singleTarget = schema.Cardinality is RelationCardinality.UniqueTarget or RelationCardinality.OneToOne;
        _uniquePair = schema.Cardinality == RelationCardinality.UniquePair;
        _outgoing = new Head[capacity]; _incoming = _directed ? new Head[capacity] : [];
        _applied = new Applied[capacity];
        if (_singleSource) _sourceClaims = new EndpointClaim[capacity];
        if (_singleTarget && _directed) _targetClaims = new EndpointClaim[capacity];
        if (_uniquePair) _pairs = new();
    }

    private void Ensure(int id)
    {
        if (id < _applied.Length) return;
        int size = Math.Max(id + 1, _applied.Length * 2);
        Array.Resize(ref _applied, size); Array.Resize(ref _outgoing, size);
        if (_directed) Array.Resize(ref _incoming, size);
        if (_sourceClaims is not null) Array.Resize(ref _sourceClaims, size);
        if (_targetClaims is not null) Array.Resize(ref _targetClaims, size);
    }
    private Pair Key(TEntity source, TEntity target) => _directed || _values.Index(source) <= _values.Index(target)
        ? new(source, target) : new(target, source);
    public bool Active(TEntity entity) => _values.Alive(entity) && !_destroyed.Contains(entity);
    public TEntity CreateEndpoint(int ordinal)
    {
        TEntity entity = _values.CreateEndpoint(ordinal); Ensure(_values.Index(entity)); return entity;
    }

    private void ValidateEndpoints(TEntity source, TEntity target)
    {
        if (!Active(source) || !Active(target)) throw new InvalidOperationException("Relation endpoints must be active.");
        if (!RelationSchema.For<TPayload>().AllowSelfEdge && Equal(source, target)) throw new InvalidOperationException("Self edge is excluded by this relation schema.");
    }
    private void ValidateClaim(TEntity occupant, TEntity edge, bool batch)
    {
        if (!Empty(occupant) && !Equal(occupant, edge) && !(batch && _edits.ContainsKey(occupant)))
            throw new InvalidOperationException("Relation endpoint is occupied.");
    }
    private void ValidateClaims(TEntity edge, TEntity source, TEntity target, bool batch = false)
    {
        if (_singleSource) ValidateClaim(ClaimAt(_sourceClaims!, source), edge, batch);
        if (_singleTarget) ValidateClaim(ClaimAt(_directed ? _targetClaims! : _sourceClaims!, target), edge, batch);
        if (_pairs is not null && _pairs.TryGetValue(Key(source, target), out TEntity occupant)) ValidateClaim(occupant, edge, batch);
    }
    private TEntity ClaimAt(EndpointClaim[] claims, TEntity endpoint)
    {
        EndpointClaim claim = claims[_values.Index(endpoint)];
        return claim.Identity.Equals(_values.Identity(endpoint)) ? claim.Edge : default;
    }
    private void Claim(TEntity edge, TEntity source, TEntity target)
    {
        if (_singleSource) _sourceClaims![_values.Index(source)] = new(_values.Identity(source), edge);
        if (_singleTarget) (_directed ? _targetClaims! : _sourceClaims!)[_values.Index(target)] = new(_values.Identity(target), edge);
        if (_pairs is not null) _pairs[Key(source, target)] = edge;
    }
    private void Release(TEntity edge, TEntity source, TEntity target)
    {
        if (_singleSource && Equal(ClaimAt(_sourceClaims!, source), edge)) _sourceClaims![_values.Index(source)] = default;
        EndpointClaim[]? targetClaims = _directed ? _targetClaims : _sourceClaims;
        if (_singleTarget && Equal(ClaimAt(targetClaims!, target), edge)) targetClaims![_values.Index(target)] = default;
        if (_pairs is not null && _pairs.TryGetValue(Key(source, target), out var occupant) && Equal(occupant, edge)) _pairs.Remove(Key(source, target));
    }
    public TEntity CreateEdge(TEntity source, TEntity target, int ordinal)
    {
        ValidateEndpoints(source, target); ValidateClaims(default, source, target);
        ulong version = ++Version;
        var endpoints = new ReferenceRelationEndpoints<TEntity> { Source = source, Target = target, Added = version, Written = version };
        TEntity edge = _values.CreateEdge(endpoints, TPayload.Create(ordinal));
        Ensure(_values.Index(edge)); PrepareApplied(edge); UpdateFuture(edge, default, default, source, target, false);
        Claim(edge, source, target); Apply(edge, source, target); return edge;
    }

    private ref Head GetHead(TEntity endpoint, bool incoming)
    {
        int id = _values.Index(endpoint);
        TIdentity identity = _values.Identity(endpoint);
        ref Head head = ref (incoming ? ref _incoming[id] : ref _outgoing[id]);
        if (!head.Identity.Equals(identity))
        {
            if (head.Count != 0) _retiredHeads.Add(new(id, head.Identity, incoming), head);
            head = new Head { Identity = identity };
        }
        return ref head;
    }
    private Head ReadHead(TEntity endpoint, bool incoming)
    {
        int id = _values.Index(endpoint);
        TIdentity identity = _values.Identity(endpoint);
        Head head = (incoming ? _incoming : _outgoing)[id];
        return head.Identity.Equals(identity) ? head : _retiredHeads.GetValueOrDefault(new(id, identity, incoming));
    }
    private ref Head HeadRow(TEntity endpoint, bool incoming)
    {
        int id = _values.Index(endpoint);
        TIdentity identity = _values.Identity(endpoint);
        ref Head head = ref (incoming ? ref _incoming[id] : ref _outgoing[id]);
        if (head.Identity.Equals(identity)) return ref head;
        return ref CollectionsMarshal.GetValueRefOrNullRef(_retiredHeads, new(id, identity, incoming));
    }
    private void PrepareApplied(TEntity edge)
    {
        int id = _values.Index(edge);
        TIdentity identity = _values.Identity(edge);
        ref Applied applied = ref _applied[id];
        if (applied.Identity.Equals(identity)) return;
        if (!Empty(applied.Source)) _retiredApplied.Add(new(id, applied.Identity), applied);
        applied = new Applied { Identity = identity };
    }
    private Applied ReadApplied(TEntity edge)
    {
        int id = _values.Index(edge);
        TIdentity identity = _values.Identity(edge);
        Applied applied = _applied[id];
        return applied.Identity.Equals(identity) ? applied : _retiredApplied.GetValueOrDefault(new(id, identity));
    }
    private ref Applied AppliedRow(TEntity edge)
    {
        int id = _values.Index(edge);
        TIdentity identity = _values.Identity(edge);
        ref Applied applied = ref _applied[id];
        if (applied.Identity.Equals(identity)) return ref applied;
        return ref CollectionsMarshal.GetValueRefOrNullRef(_retiredApplied, new(id, identity));
    }
    private int Append(TEntity endpoint, TEntity other, TEntity edge, bool incoming)
    {
        ref Head head = ref GetHead(endpoint, incoming);
        if (head.Items is null) head.Items = ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Rent(4);
        else if (head.Count == head.Items.Length)
        {
            var larger = ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Rent(head.Count * 2);
            head.Items.AsSpan(0, head.Count).CopyTo(larger);
            ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(head.Items, RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>()); head.Items = larger;
        }
        int place = head.Count++; head.Items[place] = new(other, edge); return place;
    }
    private void Drop(TEntity endpoint, TEntity edge, int place, bool incoming)
    {
        ref Head head = ref HeadRow(endpoint, incoming);
        int last = --head.Count;
        if (head.Ordered)
        {
            head.Items!.AsSpan(place + 1, last - place).CopyTo(head.Items.AsSpan(place));
            for (int i = place; i < last; i++) SetPlace(head.Items![i].Edge, endpoint, i, incoming);
        }
        else if (place != last)
        {
            var moved = head.Items![last]; head.Items[place] = moved;
            SetPlace(moved.Edge, endpoint, place, incoming);
        }
        head.Items![last] = default;
        if (last == 0)
        {
            ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(head.Items, RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>()); head.Items = null;
            int id = _values.Index(endpoint);
            TIdentity identity = _values.Identity(endpoint);
            if (!(incoming ? _incoming : _outgoing)[id].Identity.Equals(identity)) _retiredHeads.Remove(new(id, identity, incoming));
        }
    }
    private void SetPlace(TEntity edge, TEntity endpoint, int place, bool incoming)
    {
        ref Applied applied = ref AppliedRow(edge);
        if (_directed)
        {
            if (incoming) applied.TargetPlace = place;
            else applied.SourcePlace = place;
        }
        else
        {
            if (Equal(applied.Source, endpoint)) applied.SourcePlace = place;
            if (Equal(applied.Target, endpoint)) applied.TargetPlace = place;
        }
    }
    public void SetOrdered(TEntity endpoint)
    {
        _usesOrder = true;
        GetHead(endpoint, false).Ordered = true;
        if (_directed) GetHead(endpoint, true).Ordered = true;
    }
    public void Reorder(TEntity endpoint, TEntity firstEdge, bool incoming)
    {
        ref Head head = ref GetHead(endpoint, incoming);
        if (!head.Ordered) throw new InvalidOperationException("Relation adjacency requires local order.");
        int place = 0;
        while (place < head.Count && !Equal(head.Items![place].Edge, firstEdge)) place++;
        if (place == head.Count) throw new InvalidOperationException("Relation edge must belong to this adjacency.");
        if (place == 0) return;
        var entry = head.Items![place]; head.Items.AsSpan(0, place).CopyTo(head.Items.AsSpan(1)); head.Items[0] = entry;
        for (int i = 0; i <= place; i++) SetPlace(head.Items[i].Edge, endpoint, i, incoming);
        if (_futureOrders.TryGetValue(new(endpoint, incoming), out var future) && future.Remove(firstEdge))
        {
            int insert = future.Count;
            for (int i = 1; i < head.Count; i++)
            {
                int next = future.IndexOf(head.Items[i].Edge);
                if (next >= 0) { insert = next; break; }
            }
            future.Insert(insert, firstEdge);
        }
    }

    private bool Contains(ReferenceRelationEndpoints<TEntity> value, HeadKey key)
        => _directed ? Equal(key.Incoming ? value.Target : value.Source, key.Entity)
            : Equal(value.Source, key.Entity) || Equal(value.Target, key.Entity);
    private List<TEntity> Future(HeadKey key)
    {
        if (_futureOrders.TryGetValue(key, out var found)) return found;
        var order = new List<TEntity>(); _orderMembers.Clear();
        Head head = ReadHead(key.Entity, key.Incoming);
        for (int i = 0; i < head.Count; i++) Add(head.Items![i].Edge);
        if (_future.TryGetValue(key.Entity, out var future)) foreach (TEntity edge in future) Add(edge);
        _futureOrders.Add(key, order); return order;
        void Add(TEntity edge)
        {
            if (Active(edge) && _values.Has<ReferenceRelationEndpoints<TEntity>>(edge)
                && Contains(_values.Read<ReferenceRelationEndpoints<TEntity>>(edge), key) && _orderMembers.Add(edge)) order.Add(edge);
        }
    }
    private void ChangeFuture(TEntity edge, TEntity endpoint, bool incoming, bool add, bool deferred)
    {
        if (Empty(endpoint)) return;
        var key = new HeadKey(endpoint, incoming);
        if (!_futureOrders.TryGetValue(key, out var order))
        {
            if (!deferred || !ReadHead(endpoint, incoming).Ordered) return;
            order = Future(key);
        }
        order.Remove(edge); if (add) order.Add(edge);
    }
    private void UpdateFuture(TEntity edge, TEntity oldSource, TEntity oldTarget, TEntity source, TEntity target, bool deferred)
    {
        if (!_usesOrder) return;
        if (_directed)
        {
            if (!Equal(oldSource, source)) { ChangeFuture(edge, oldSource, false, false, deferred); ChangeFuture(edge, source, false, true, deferred); }
            if (!Equal(oldTarget, target)) { ChangeFuture(edge, oldTarget, true, false, deferred); ChangeFuture(edge, target, true, true, deferred); }
        }
        else
        {
            if (!Equal(oldSource, source) && !Equal(oldSource, target)) ChangeFuture(edge, oldSource, false, false, deferred);
            if (!Equal(oldTarget, oldSource) && !Equal(oldTarget, source) && !Equal(oldTarget, target)) ChangeFuture(edge, oldTarget, false, false, deferred);
            if (!Equal(source, oldSource) && !Equal(source, oldTarget)) ChangeFuture(edge, source, false, true, deferred);
            if (!Equal(target, source) && !Equal(target, oldSource) && !Equal(target, oldTarget)) ChangeFuture(edge, target, false, true, deferred);
        }
    }
    private void PublishFutureOrders()
    {
        foreach (var pair in _futureOrders)
        {
            if (!Active(pair.Key.Entity)) continue;
            ref Head head = ref HeadRow(pair.Key.Entity, pair.Key.Incoming);
            for (int i = 0; i < pair.Value.Count; i++)
            {
                TEntity edge = pair.Value[i]; Applied applied = ReadApplied(edge);
                TEntity other = _directed ? pair.Key.Incoming ? applied.Source : applied.Target
                    : Equal(applied.Source, pair.Key.Entity) ? applied.Target : applied.Source;
                head.Items![i] = new(other, edge); SetPlace(edge, pair.Key.Entity, i, pair.Key.Incoming);
            }
            head.Count = pair.Value.Count;
            if (head.Count == 0 && head.Items is { } items)
            {
                ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(items,
                    RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>());
                head.Items = null;
            }
        }
        _futureOrders.Clear();
    }
    private void DropApplied(TEntity edge)
    {
        Applied applied = ReadApplied(edge);
        if (Empty(applied.Source)) return;
        TEntity source = applied.Source, target = applied.Target;
        Drop(source, edge, applied.SourcePlace, false);
        if (_directed || !Equal(source, target)) Drop(target, edge, applied.TargetPlace, _directed);
        int id = _values.Index(edge);
        TIdentity identity = _values.Identity(edge);
        if (_applied[id].Identity.Equals(identity)) _applied[id] = new Applied { Identity = identity };
        else _retiredApplied.Remove(new(id, identity));
    }
    private void Apply(TEntity edge, TEntity source, TEntity target, bool groupedOrder = false)
    {
        Applied before = ReadApplied(edge);
        if (Equal(before.Source, source) && Equal(before.Target, target)) return;
        bool Has(TEntity endpoint) => Equal(before.Source, endpoint) || Equal(before.Target, endpoint);
        int Place(TEntity endpoint) => Equal(before.Source, endpoint) ? before.SourcePlace : before.TargetPlace;
        int a, b;
        if (_directed)
        {
            if (!Empty(before.Source) && !Equal(before.Source, source) && !UsesFutureOrder(before.Source, false, groupedOrder))
                Drop(before.Source, edge, before.SourcePlace, false);
            if (!Empty(before.Target) && !Equal(before.Target, target) && !UsesFutureOrder(before.Target, true, groupedOrder))
                Drop(before.Target, edge, before.TargetPlace, true);
            a = UsesFutureOrder(source, false, groupedOrder) ? 0 : Equal(before.Source, source) ? before.SourcePlace : Append(source, target, edge, false);
            b = UsesFutureOrder(target, true, groupedOrder) ? 0 : Equal(before.Target, target) ? before.TargetPlace : Append(target, source, edge, true);
        }
        else
        {
            if (!Empty(before.Source) && !Equal(before.Source, source) && !Equal(before.Source, target)
                && !UsesFutureOrder(before.Source, false, groupedOrder)) Drop(before.Source, edge, before.SourcePlace, false);
            if (!Empty(before.Target) && !Equal(before.Target, before.Source) && !Equal(before.Target, source) && !Equal(before.Target, target)
                && !UsesFutureOrder(before.Target, false, groupedOrder)) Drop(before.Target, edge, before.TargetPlace, false);
            a = UsesFutureOrder(source, false, groupedOrder) ? 0 : Has(source) ? Place(source) : Append(source, target, edge, false);
            b = Equal(source, target) ? a : UsesFutureOrder(target, false, groupedOrder) ? 0 : Has(target) ? Place(target) : Append(target, source, edge, false);
        }
        if (!UsesFutureOrder(source, false, groupedOrder)) GetHead(source, false).Items![a] = new(target, edge);
        if (!UsesFutureOrder(target, _directed, groupedOrder)) GetHead(target, _directed).Items![b] = new(source, edge);
        AppliedRow(edge) = new Applied { Identity = _values.Identity(edge), Source = source, Target = target, SourcePlace = a, TargetPlace = b };
    }

    private void AddFuture(TEntity endpoint, TEntity edge)
    {
        if (!_future.TryGetValue(endpoint, out var members))
        {
            members = _futureSets.TryPop(out var reused) ? reused : new(); _future.Add(endpoint, members);
        }
        members.Add(edge);
    }
    private void RemoveFuture(TEntity endpoint, TEntity edge)
    {
        if (!_future.TryGetValue(endpoint, out var members)) return;
        members.Remove(edge);
        if (members.Count == 0) { _future.Remove(endpoint); _futureSets.Push(members); }
    }
    private void RemoveDifference(TEntity edge)
    {
        if (!_pending.Remove(edge)) return;
        var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edge);
        RemoveFuture(current.Source, edge); RemoveFuture(current.Target, edge);
    }
    private void UpdateDifference(TEntity edge, TEntity source, TEntity target)
    {
        Applied applied = ReadApplied(edge);
        if (Equal(applied.Source, source) && Equal(applied.Target, target)) return;
        _pending.Add(edge); AddFuture(source, edge); AddFuture(target, edge);
    }
    public void Retarget(ReadOnlySpan<RelationEdit<TEntity>> edits, bool deferred)
    {
        _edits.Clear(); _finalSources.Clear(); _finalTargets.Clear(); _finalPairs.Clear();
        foreach (var edit in edits)
        {
            if (!Active(edit.Edge) || !_values.Has<ReferenceRelationEndpoints<TEntity>>(edit.Edge)) throw new InvalidOperationException("Relation edge must be active.");
            ValidateEndpoints(edit.Source, edit.Target); _edits[edit.Edge] = edit;
        }
        foreach (var edit in _edits.Values)
        {
            ValidateClaims(edit.Edge, edit.Source, edit.Target, true);
            if (_singleSource && !_finalSources.TryAdd(edit.Source, edit.Edge)) throw new InvalidOperationException("Final source is occupied.");
            if (_singleTarget && !(_directed ? _finalTargets : _finalSources).TryAdd(edit.Target, edit.Edge)
                && !(!_directed && Equal(edit.Source, edit.Target))) throw new InvalidOperationException("Final target is occupied.");
            if (_uniquePair && !_finalPairs.TryAdd(Key(edit.Source, edit.Target), edit.Edge)) throw new InvalidOperationException("Final pair is occupied.");
        }
        if (_usesOrder) UpdateBatchFuture(deferred);
        ulong version = 0;
        foreach (var edit in _edits.Values)
        {
            var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edit.Edge);
            if (Equal(current.Source, edit.Source) && Equal(current.Target, edit.Target)) continue;
            Release(edit.Edge, current.Source, current.Target); RemoveDifference(edit.Edge);
        }
        foreach (var edit in _edits.Values)
        {
            var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edit.Edge);
            if (Equal(current.Source, edit.Source) && Equal(current.Target, edit.Target)) continue;
            if (version == 0) version = ++Version;
            current.Source = edit.Source; current.Target = edit.Target; current.Written = version;
            _values.Set(edit.Edge, current); Claim(edit.Edge, edit.Source, edit.Target);
            if (deferred) UpdateDifference(edit.Edge, edit.Source, edit.Target); else Apply(edit.Edge, edit.Source, edit.Target);
        }
        _edits.Clear();
    }
    public void Maintain()
    {
        foreach (TEntity entity in _destroyedWork) { DropApplied(entity); RemoveIncidents(entity); }
        _destroyedWork.Clear();
        bool groupedOrder = _futureOrders.Count != 0;
        if (groupedOrder) ReserveFutureOrders();
        foreach (TEntity edge in _pending)
        {
            var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edge); Apply(edge, current.Source, current.Target, groupedOrder);
        }
        PublishFutureOrders();
        _pending.Clear();
        foreach (var members in _future.Values) { members.Clear(); _futureSets.Push(members); }
        _future.Clear();
    }
    public void DestroyEdge(TEntity edge)
        => DestroyEdge(edge, false);
    private void DestroyEdge(TEntity edge, bool deferred)
    {
        if (!Active(edge)) return;
        var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edge);
        UpdateFuture(edge, current.Source, current.Target, default, default, deferred);
        RemoveDifference(edge); Release(edge, current.Source, current.Target);
        if (!deferred) DropApplied(edge);
        _values.Set(edge, new ReferenceOldRelationEndpoints<TEntity> { Source = current.Source, Target = current.Target, Version = ++Version });
        _values.Remove<ReferenceRelationEndpoints<TEntity>>(edge); _values.Remove<TPayload>(edge);
        _destroyed.Add(edge); _removed.Add(edge);
        if (deferred) _destroyedWork.Add(edge); else RemoveIncidents(edge);
    }
    private void RemoveIncidents(TEntity endpoint)
    {
        Head first = ReadHead(endpoint, false), second = _directed ? ReadHead(endpoint, true) : default;
        _future.TryGetValue(endpoint, out var future);
        int count = first.Count + second.Count + (future?.Count ?? 0);
        if (count == 0) return;
        TEntity[] candidates = ArrayPool<TEntity>.Shared.Rent(count);
        int used = 0;
        for (int i = 0; i < first.Count; i++) candidates[used++] = first.Items![i].Edge;
        for (int i = 0; i < second.Count; i++) candidates[used++] = second.Items![i].Edge;
        if (future is not null) foreach (TEntity edge in future) candidates[used++] = edge;
        foreach (TEntity edge in candidates.AsSpan(0, count))
        {
            if (!Active(edge)) continue;
            var current = _values.Read<ReferenceRelationEndpoints<TEntity>>(edge);
            if (Equal(current.Source, endpoint) || Equal(current.Target, endpoint) || !Active(current.Source) || !Active(current.Target)) DestroyEdge(edge);
            else { RemoveDifference(edge); Apply(edge, current.Source, current.Target); }
        }
        ArrayPool<TEntity>.Shared.Return(candidates, RuntimeHelpers.IsReferenceOrContainsReferences<TEntity>());
        if (_usesOrder) { _futureOrders.Remove(new(endpoint, false)); _futureOrders.Remove(new(endpoint, true)); }
    }
    public void DestroyEndpoint(TEntity endpoint)
    {
        if (_values.Has<ReferenceRelationEndpoints<TEntity>>(endpoint)) { DestroyEdge(endpoint); return; }
        RemoveIncidents(endpoint); _values.Destroy(endpoint); ++Version;
        if (_usesOrder) { _futureOrders.Remove(new(endpoint, false)); _futureOrders.Remove(new(endpoint, true)); }
    }
    public void DestroyDeferred(TEntity entity)
    {
        if (_values.Has<ReferenceRelationEndpoints<TEntity>>(entity)) DestroyEdge(entity, true);
        else { _values.Destroy(entity); _destroyedWork.Add(entity); ++Version; }
    }
    public void Observe(RelationObservation<TEntity> result, ulong since) => _values.Observe(result, since);
    public void ObserveAdjacency(TEntity endpoint, RelationObservation<TEntity> result)
    {
        Head outgoing = ReadHead(endpoint, false);
        for (int i = 0; i < outgoing.Count; i++)
        {
            var entry = outgoing.Items![i]; result.Adjacency(endpoint, entry.Other, entry.Edge, _directed ? 0 : 2);
        }
        if (!_directed) return;
        Head incoming = ReadHead(endpoint, true);
        for (int i = 0; i < incoming.Count; i++)
        {
            var entry = incoming.Items![i]; result.Adjacency(endpoint, entry.Other, entry.Edge, 1);
        }
    }
    public void ClearFacts()
    {
        foreach (TEntity edge in _removed) _values.Destroy(edge);
        _removed.Clear(); _destroyed.Clear();
    }
    public void Dispose()
    {
        foreach (Head head in _outgoing) if (head.Items is not null) ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(head.Items, RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>());
        foreach (Head head in _incoming) if (head.Items is not null) ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(head.Items, RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>());
        foreach (Head head in _retiredHeads.Values) if (head.Items is not null) ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(head.Items, RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>());
        _outgoing = []; _incoming = []; _applied = [];
        _sourceClaims = _targetClaims = null;
        _retiredHeads.Clear(); _retiredApplied.Clear(); _pending.Clear(); _destroyed.Clear(); _destroyedWork.Clear();
        _removed.Clear(); _future.Clear(); _futureSets.Clear(); _futureOrders.Clear(); _orderMembers.Clear();
        _edits.Clear(); _finalPairs.Clear(); _finalSources.Clear(); _finalTargets.Clear(); _pairs?.Clear(); _batchOrderHeads.Clear();
        _values.Dispose();
    }
}
