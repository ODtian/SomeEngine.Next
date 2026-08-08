using System.Collections.Concurrent;
using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Owners;

/// <summary>
/// Per-World registry for statically typed hierarchy domains.
/// </summary>
internal sealed partial class Hierarchy
{
    // GetChildren is a relaxed inverse read and deliberately does not acquire topology
    // admission. Domain discovery must therefore remain safe while a topology writer registers
    // another domain, but the read path must never create a domain as a side effect.
    private readonly ConcurrentDictionary<Type, IHierarchyDomainStore> _domains = new();
    private readonly Dictionary<int, IHierarchyDomainStore> _parentComponents = new();
    private readonly Dictionary<int, IHierarchyDomainStore> _childrenComponents = new();
    private readonly object _registrationLock = new();
    private readonly HashSet<Entity> _destroyingEntities = new();
    private HashSet<Entity>? _terminalDestroyEntities;
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Components _components = null!;
    private Clock _clock = null!;

    private int _editDepth;

    internal bool Any => _domains.Count != 0;

    /// <summary>
    /// Creates an exact hierarchy image whose typed domains retain their current backing
    /// generation. Each wrapper detaches its mutable registries, maintenance state and physical
    /// shards immediately before the first write; published child arrays remain immutable.
    /// </summary>
    internal Hierarchy CloneDetached()
    {
        if (_editDepth != 0 ||
            _destroyingEntities.Count != 0 ||
            _terminalDestroyEntities is not null)
        {
            throw new InvalidOperationException(
                "Cannot clone hierarchy state while an edit or terminal destroy is active.");
        }

        var clone = new Hierarchy();
        clone.Bind(_entities, _tables, _components, _clock);
        foreach (IHierarchyDomainStore store in _domains.Values)
            clone.Register(store.CloneDetached(clone));
        return clone;
    }

    internal void Bind(
        Entities entities,
        Tables tables,
        Components components,
        Clock clock)
    {
        _entities = entities;
        _tables = tables;
        _components = components;
        _clock = clock;
    }

    internal bool IsEditing => _editDepth != 0;

    internal HierarchyDomainStore<TDomain> Domain<TDomain>()
        where TDomain : IHierarchyDomain
    {
        Type domainType = typeof(TDomain);
        if (_domains.TryGetValue(domainType, out var existing))
            return (HierarchyDomainStore<TDomain>)existing;

        lock (_registrationLock)
        {
            if (_domains.TryGetValue(domainType, out existing))
                return (HierarchyDomainStore<TDomain>)existing;
            if (_editDepth != 0 || _terminalDestroyEntities is not null)
            {
                throw new InvalidOperationException(
                    $"Hierarchy domain {domainType.FullName} cannot be registered during hierarchy mutation.");
            }

            var created = new HierarchyDomainStore<TDomain>(this);
            Register(created);
            return created;
        }
    }

    internal bool TryDomain<TDomain>(out HierarchyDomainStore<TDomain> store)
        where TDomain : IHierarchyDomain
    {
        if (_domains.TryGetValue(typeof(TDomain), out var existing))
        {
            store = (HierarchyDomainStore<TDomain>)existing;
            return true;
        }

        store = null!;
        return false;
    }

    internal HierarchyChildrenSnapshot<TDomain> GetChildren<TDomain>(Entity parent)
        where TDomain : IHierarchyDomain
    {
        return TryDomain<TDomain>(out var store)
            ? store.GetChildren(parent)
            : new HierarchyChildrenSnapshot<TDomain>(Array.Empty<Entity>(), generation: 0);
    }

    internal bool Alive(Entity entity) => _entities.Alive(entity);

    internal bool Pending(Entity entity) => _entities.Pending(entity);

    internal int EntityCount => _entities.Count;

    internal int EntitySlotCount => _entities.Store.Count;

    internal bool TryGetLiveEntityAtSlot(int slotOffset, out Entity entity)
    {
        int index = checked(slotOffset + 1);
        if ((uint)slotOffset >= (uint)_entities.Store.Count ||
            !_entities.Store.IsAliveIndex(index))
        {
            entity = Entity.Null;
            return false;
        }

        entity = new Entity(index, _entities.Store.GetGeneration(index));
        return true;
    }

    internal int ArchetypeCount => _tables.Registry.Count;

    internal Archetype ArchetypeAt(int index) => _tables.Registry.At(index);

    internal uint Tick => _clock.Tick;

    internal void EnsureAlive(Entity entity, string role)
    {
        if (!Alive(entity))
            throw new InvalidOperationException($"Hierarchy {role} {entity} is not alive.");

        if (Pending(entity))
            throw new InvalidOperationException($"Hierarchy {role} {entity} is pending cleanup.");
    }

    internal bool Has<T>(Entity entity)
        where T : struct =>
        _components.Has<T>(entity);

    internal T Read<T>(Entity entity)
        where T : struct, IComponent =>
        _components.Read<T>(entity);

    internal void AddRelationshipComponent<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        EnsureRelationshipRole<T>();
        _components.Add(entity, in value);
    }

    internal void ReplaceRelationshipComponent<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        EnsureRelationshipRole<T>();
        _components.Replace(entity, in value);
    }

    internal void RemoveRelationshipComponent<T>(Entity entity)
        where T : struct, IComponent
    {
        EnsureRelationshipRole<T>();
        _components.Remove<T>(entity);
    }

    private static void EnsureRelationshipRole<T>()
        where T : struct
    {
        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            throw new InvalidOperationException(
                $"Hierarchy internal mutation requires a relationship component, got {typeof(T).Name}.");
        }
    }

    private void Register(IHierarchyDomainStore store)
    {
        if (!_domains.TryAdd(store.DomainType, store))
            throw new InvalidOperationException($"Hierarchy domain {store.DomainType} is already registered.");
        _parentComponents[store.ParentComponentId] = store;
        _childrenComponents[store.ChildrenComponentId] = store;
    }

    private bool TryResolve(
        int componentId,
        out IHierarchyDomainStore store,
        out bool source)
    {
        if (_parentComponents.TryGetValue(componentId, out store!))
        {
            source = true;
            return true;
        }

        if (_childrenComponents.TryGetValue(componentId, out store!))
        {
            source = false;
            return true;
        }

        ref readonly var info = ref ComponentRegistry.Get(componentId);
        return TryResolve(info.HierarchyRegistration, componentId, out store, out source);
    }

    private bool TryResolve(
        IHierarchyComponentRegistration? registration,
        int componentId,
        out IHierarchyDomainStore store,
        out bool source)
    {
        store = null!;
        source = false;
        if (registration is null)
            return false;

        store = registration.GetOrCreate(this);
        source = registration.IsSource;
        int expectedComponentId = source
            ? store.ParentComponentId
            : store.ChildrenComponentId;
        if (componentId != expectedComponentId)
        {
            throw new InvalidOperationException(
                $"Hierarchy registration for component ID {componentId} resolved to " +
                $"component ID {expectedComponentId} in domain {store.DomainType}.");
        }

        // The first access may arrive through a component id before the generic store properties
        // were observed by this caller. Keep both maps explicit and verify the registry identity.
        if (source)
            _parentComponents[componentId] = store;
        else
            _childrenComponents[componentId] = store;

        return true;
    }
}

internal interface IHierarchyComponentRegistration
{
    bool IsSource { get; }

    IHierarchyDomainStore GetOrCreate(Hierarchy owner);
}

internal interface IHierarchyDomainStore
{
    Type DomainType { get; }

    int ParentComponentId { get; }

    int ChildrenComponentId { get; }

    IHierarchyDomainStore CloneDetached(Hierarchy owner);

    void CaptureBeforeMutation(Entity entity);

    void RequireScan();

    void RequireChildrenNormalization(Entity parent);

    void ValidateDeferredWrites();

    void RollbackDeferredWrites();

    void CommitDeferredWrites();

    void OnEntityDestroying(Entity entity);

    void BeginTerminalDestroy(ReadOnlySpan<Entity> terminalEntities);

    void EndTerminalDestroy();

    void Reset();
}

/// <summary>
/// One coherent parent-local publication. The array is immutable after construction, and the
/// generation travels with it through one ConcurrentDictionary entry so a relaxed reader cannot
/// assemble a view from two different publications.
/// </summary>
internal readonly struct PublishedChildren
{
    private readonly Entity[] _items;

    internal PublishedChildren(Entity[] ownedItems, ulong generation)
    {
        _items = ownedItems ?? throw new ArgumentNullException(nameof(ownedItems));
        Generation = generation;
    }

    internal ulong Generation { get; }

    internal ReadOnlyMemory<Entity> Memory => _items;

    internal bool SharesBacking(object backing) =>
        ReferenceEquals(_items, backing);
}

/// <summary>
/// Canonical dirty/preimage state, applied-parent state and physical child shards for one domain.
/// </summary>
internal sealed partial class HierarchyDomainStore<TDomain> : IHierarchyDomainStore
    where TDomain : IHierarchyDomain
{
    private static long s_nextInverseRevision;

    private readonly Hierarchy _owner;
    private Dictionary<Entity, Entity> _appliedParents = new();
    private Dictionary<Entity, ParentPreimage> _preimages = new();
    private HashSet<Entity> _dirty = new();
    private Dictionary<Entity, Entity> _dirtyIndexedParentByChild = new();
    private Dictionary<Entity, HashSet<Entity>> _dirtyChildrenByParent = new();
    private Dictionary<Entity, PendingChildPlacement> _pendingPlacements = new();
    private Dictionary<Entity, ChildOrderPolicy> _policies = new();
    private Dictionary<Entity, OrderedChildShard> _ordered = new();
    private Dictionary<Entity, UnorderedChildShard> _unordered = new();
    private ConcurrentDictionary<Entity, PublishedChildren> _publishedChildren = new();
    private HashSet<Entity> _normalizationParents = new();
    private Dictionary<Entity, Entity[]>? _terminalDirectChildren;
    private TopologyOrderDiagnosticCounter _orderDiagnostics;
    private HierarchyDomainGeneration _backing = new();
    private int _detachCount;

    private bool _scanNeeded;
    private bool _normalizeAllChildren;
    private bool _dirtyParentIndexValid = true;
    private ulong _generation = 1;
    private long _deferredSequence;
    private long _inverseRevision = NextInverseRevision();
    private long _canonicalParentFullScanCount;
    private long _dirtyParentIndexRebuildEntityVisits;
    private long _dirtyParentLookupCount;
    private long _dirtyParentLookupEntityVisits;

    internal HierarchyDomainStore(Hierarchy owner)
    {
        _owner = owner;
        _orderDiagnostics = new TopologyOrderDiagnosticCounter();
        ParentComponentId = ComponentMetadata<Parent<TDomain>>.Id;
        ChildrenComponentId = ComponentMetadata<Children<TDomain>>.Id;
    }

    private HierarchyDomainStore(
        Hierarchy owner,
        HierarchyDomainStore<TDomain> source)
    {
        _owner = owner;
        ParentComponentId = source.ParentComponentId;
        ChildrenComponentId = source.ChildrenComponentId;

        source._backing.MarkShared();
        _backing = source._backing;
        _appliedParents = source._appliedParents;
        _preimages = source._preimages;
        _dirty = source._dirty;
        _dirtyIndexedParentByChild = source._dirtyIndexedParentByChild;
        _dirtyChildrenByParent = source._dirtyChildrenByParent;
        _pendingPlacements = source._pendingPlacements;
        _policies = source._policies;
        _ordered = source._ordered;
        _unordered = source._unordered;
        _publishedChildren = source._publishedChildren;
        _normalizationParents = source._normalizationParents;
        _orderDiagnostics = source._orderDiagnostics;

        _scanNeeded = source._scanNeeded;
        _normalizeAllChildren = source._normalizeAllChildren;
        _dirtyParentIndexValid = source._dirtyParentIndexValid;
        _generation = source._generation;
        _deferredSequence = source._deferredSequence;
        _inverseRevision = source._inverseRevision;
        _canonicalParentFullScanCount = source._canonicalParentFullScanCount;
        _dirtyParentIndexRebuildEntityVisits = source._dirtyParentIndexRebuildEntityVisits;
        _dirtyParentLookupCount = source._dirtyParentLookupCount;
        _dirtyParentLookupEntityVisits = source._dirtyParentLookupEntityVisits;
    }

    public Type DomainType => typeof(TDomain);

    public int ParentComponentId { get; }

    public int ChildrenComponentId { get; }

    public IHierarchyDomainStore CloneDetached(Hierarchy owner) =>
        new HierarchyDomainStore<TDomain>(owner, this);

    /// <summary>The shared-or-exclusive physical state generation behind this typed domain.</summary>
    internal object BackingIdentity => _backing;

    /// <summary>Number of shared domain generations detached by this wrapper.</summary>
    internal int DetachCount => _detachCount;

    internal TopologyOrderDiagnostics OrderDiagnostics =>
        _orderDiagnostics.Snapshot(
            _pendingPlacements.Count,
            System.Runtime.CompilerServices.Unsafe.SizeOf<PendingChildPlacement>());

    internal long InverseRevision => _inverseRevision;

    /// <summary>
    /// Monotonic count of intentional complete Parent-column passes. The historical property
    /// name is retained for diagnostics compatibility; it does not count all-live-entity scans.
    /// </summary>
    internal long CanonicalParentFullScanCount => _canonicalParentFullScanCount;

    internal long DirtyParentIndexRebuildEntityVisits =>
        _dirtyParentIndexRebuildEntityVisits;

    internal long DirtyParentLookupCount => _dirtyParentLookupCount;

    internal long DirtyParentLookupEntityVisits =>
        _dirtyParentLookupEntityVisits;

    internal bool IsInverseFresh =>
        _dirty.Count == 0 &&
        _preimages.Count == 0 &&
        _pendingPlacements.Count == 0 &&
        _normalizationParents.Count == 0 &&
        !_scanNeeded &&
        !_normalizeAllChildren;

    private void EnsureWritable()
    {
        if (!_backing.IsShared)
            return;

        var diagnostics = _orderDiagnostics.CloneDetached();
        var ordered = new Dictionary<Entity, OrderedChildShard>(_ordered.Count);
        foreach (var pair in _ordered)
        {
            ordered.Add(
                pair.Key,
                new OrderedChildShard(pair.Value, diagnostics, recordCloneWork: false));
        }

        var unordered = new Dictionary<Entity, UnorderedChildShard>(_unordered.Count);
        foreach (var pair in _unordered)
            unordered.Add(pair.Key, new UnorderedChildShard(pair.Value));

        var publishedChildren = new ConcurrentDictionary<Entity, PublishedChildren>();
        foreach (var pair in _publishedChildren)
            publishedChildren.TryAdd(pair.Key, pair.Value);

        _appliedParents = new Dictionary<Entity, Entity>(_appliedParents);
        _preimages = new Dictionary<Entity, ParentPreimage>(_preimages);
        _dirty = new HashSet<Entity>(_dirty);
        _dirtyIndexedParentByChild = new Dictionary<Entity, Entity>(_dirtyIndexedParentByChild);
        var dirtyChildrenByParent = new Dictionary<Entity, HashSet<Entity>>(
            _dirtyChildrenByParent.Count);
        foreach (var pair in _dirtyChildrenByParent)
            dirtyChildrenByParent.Add(pair.Key, new HashSet<Entity>(pair.Value));
        _dirtyChildrenByParent = dirtyChildrenByParent;
        _pendingPlacements = new Dictionary<Entity, PendingChildPlacement>(_pendingPlacements);
        _policies = new Dictionary<Entity, ChildOrderPolicy>(_policies);
        _ordered = ordered;
        _unordered = unordered;
        _publishedChildren = publishedChildren;
        _normalizationParents = new HashSet<Entity>(_normalizationParents);
        _orderDiagnostics = diagnostics;
        _backing = new HierarchyDomainGeneration();
        _detachCount++;
    }

    internal void SetParent(
        Entity child,
        Entity parent,
        int? insertIndex,
        bool immediate)
    {
        ValidateParent(child, parent);
        ValidatePlacement(child, parent, insertIndex, immediate);

        var before = ReadCanonical(child);
        EnsureWritable();
        if (before.HasParent && before.Parent == parent)
        {
            if (immediate)
            {
                _owner.BeginEdit();
                try
                {
                    ApplyCurrent(child, insertIndex);
                    CommitApplied(child);
                }
                finally
                {
                    _owner.EndEdit();
                }
            }
            else
            {
                CaptureBeforeMutation(child);
                MarkDeferred(child, insertIndex);
                _preimages.Remove(child);
            }

            return;
        }

        if (!immediate)
            CaptureBeforeMutation(child);

        _owner.BeginEdit();
        try
        {
            WriteCanonical(child, new CanonicalParent(true, parent));
            if (immediate)
            {
                ApplyCurrent(child, insertIndex);
                CommitApplied(child);
            }
            else
            {
                MarkDeferred(child, insertIndex);
                _preimages.Remove(child);
            }
        }
        catch
        {
            // Invalid endpoints/cycles/placement are rejected before this point. This rollback
            // covers failures in the component/derived publication path as well.
            WriteCanonical(child, before);
            throw;
        }
        finally
        {
            _owner.EndEdit();
        }
    }

    internal void Detach(Entity child, bool immediate)
    {
        _owner.EnsureAlive(child, "child");
        var before = ReadCanonical(child);
        if (!before.HasParent)
        {
            if (immediate && _appliedParents.ContainsKey(child))
            {
                EnsureWritable();
                _owner.BeginEdit();
                try
                {
                    ApplyCurrent(child, insertIndex: null);
                    CommitApplied(child);
                }
                finally
                {
                    _owner.EndEdit();
                }
            }

            return;
        }

        EnsureWritable();
        if (!immediate)
            CaptureBeforeMutation(child);

        _owner.BeginEdit();
        try
        {
            WriteCanonical(child, CanonicalParent.None);
            if (immediate)
            {
                ApplyCurrent(child, insertIndex: null);
                CommitApplied(child);
            }
            else
            {
                MarkDeferred(child, insertIndex: null);
                _preimages.Remove(child);
            }
        }
        catch
        {
            WriteCanonical(child, before);
            throw;
        }
        finally
        {
            _owner.EndEdit();
        }
    }

    internal Entity GetParent(Entity child)
    {
        _owner.EnsureAlive(child, "child");
        return ReadCanonical(child).Parent;
    }

    internal HierarchyChildrenSnapshot<TDomain> GetChildren(Entity parent)
    {
        // Published child arrays are immutable generation roots. A relaxed inverse reader must
        // not touch entity/archetype liveness while a canonical writer owns those stores. A
        // parent absent from the currently published map (unknown, stale, or already retired)
        // therefore reads as an empty generation; previously captured views keep their array.
        return _publishedChildren.TryGetValue(parent, out var published)
            ? new HierarchyChildrenSnapshot<TDomain>(published.Memory, published.Generation)
            : new HierarchyChildrenSnapshot<TDomain>(ReadOnlyMemory<Entity>.Empty, generation: 0);
    }

    internal ChildOrderPolicy GetOrderPolicy(Entity parent)
    {
        _owner.EnsureAlive(parent, "parent");
        return Policy(parent);
    }

    internal void PrepareSerializationWrite(
        out int parentCount,
        out int orderedSequenceCount,
        out long recordCount)
    {
        if (!IsInverseFresh)
        {
            throw new InvalidOperationException(
                $"Hierarchy domain {typeof(TDomain).FullName} has deferred or dirty inverse state and cannot be serialized without materializing a second topology backing.");
        }

        parentCount = 0;
        orderedSequenceCount = 0;
        recordCount = 0;
        for (int slot = 0; slot < _owner.EntitySlotCount; slot++)
        {
            if (!_owner.TryGetLiveEntityAtSlot(slot, out Entity entity))
                continue;

            CanonicalParent canonical = ReadCanonical(entity);
            if (canonical.HasParent)
            {
                parentCount = checked(parentCount + 1);
                recordCount = checked(recordCount + 1);
            }

            if (Policy(entity) != ChildOrderPolicy.Ordered)
                continue;

            orderedSequenceCount = checked(orderedSequenceCount + 1);
            int childCount = _ordered.TryGetValue(entity, out OrderedChildShard? shard)
                ? shard.Count
                : 0;
            recordCount = checked(recordCount + 1L + childCount);
        }
    }

    internal void ValidateSerializationWrite()
    {
        if (!IsInverseFresh)
        {
            throw new InvalidOperationException(
                $"Hierarchy domain {typeof(TDomain).FullName} has deferred or dirty inverse state and cannot be serialized without materializing a second topology backing.");
        }

        for (int slot = 0; slot < _owner.EntitySlotCount; slot++)
        {
            _ = TryGetSerializationOrderedChildrenAt(
                slot,
                out _,
                out _);
        }
    }

    internal int SerializationSlotCount => _owner.EntitySlotCount;

    internal bool TryGetSerializationParentAt(
        int slotOffset,
        out Entity child,
        out Entity parent)
    {
        if (!_owner.TryGetLiveEntityAtSlot(slotOffset, out child))
        {
            parent = Entity.Null;
            return false;
        }

        CanonicalParent canonical = ReadCanonical(child);
        parent = canonical.Parent;
        return canonical.HasParent;
    }

    internal bool TryGetSerializationOrderedChildrenAt(
        int slotOffset,
        out Entity parent,
        out ReadOnlyMemory<Entity> children)
    {
        if (!_owner.TryGetLiveEntityAtSlot(slotOffset, out parent) ||
            Policy(parent) != ChildOrderPolicy.Ordered)
        {
            children = ReadOnlyMemory<Entity>.Empty;
            return false;
        }

        if (!_ordered.TryGetValue(parent, out OrderedChildShard? shard))
        {
            children = ReadOnlyMemory<Entity>.Empty;
            return true;
        }

        if (!_publishedChildren.TryGetValue(parent, out PublishedChildren published) ||
            !published.SharesBacking(shard.PublishedBacking))
        {
            throw new InvalidOperationException(
                $"Hierarchy domain {typeof(TDomain).FullName} ordered inverse for {parent} is not a single published backing.");
        }

        children = published.Memory;
        return true;
    }

    internal TopologyImport BeginTopologyImport(int parentCount) =>
        new(this, parentCount);

    internal sealed partial class TopologyImport
    {
        private readonly HierarchyDomainStore<TDomain> _store;
        private readonly int _expectedParentCount;
        private int _expectedOrderedSequenceCount = -1;
        private Dictionary<Entity, int>? _childrenPerParent = new();
        private int _parentCount;
        private int _orderedSequenceCount;
        private bool _completed;

        internal TopologyImport(
            HierarchyDomainStore<TDomain> store,
            int parentCount)
        {
            _store = store;
            _expectedParentCount = parentCount;
            _parentImportOrdinals = new Dictionary<Entity, int>(parentCount);

            if (!store.IsInverseFresh ||
                store._backing.IsShared ||
                store._owner.IsEditing ||
                store._appliedParents.Count != 0 ||
                store._policies.Count != 0 ||
                store._ordered.Count != 0 ||
                store._unordered.Count != 0 ||
                store._publishedChildren.Count != 0)
            {
                throw new InvalidDataException(
                    $"Hierarchy domain {typeof(TDomain).FullName} topology import requires a new, empty World backing.");
            }

            foreach ((_, _) in store.EnumerateCanonicalParents())
            {
                throw new InvalidDataException(
                    $"Hierarchy domain {typeof(TDomain).FullName} already contains Parent components before topology import.");
            }

            foreach (Entity _ in store.EnumerateComponentEntities<Children<TDomain>>())
            {
                throw new InvalidDataException(
                    $"Hierarchy domain {typeof(TDomain).FullName} already contains Children components before topology import.");
            }
        }

        internal void AddParent(Entity child, Entity parent)
        {
            RequireOpen();
            RequireParentsOpen();
            if (_parentCount >= _expectedParentCount)
                throw new InvalidDataException("Hierarchy Parent payload contains more entries than declared.");
            if (_store._appliedParents.ContainsKey(child))
                throw new InvalidDataException($"Duplicate canonical Parent entry for child {child}.");

            try
            {
                _store.ValidateParentEndpoints(child, parent);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException(
                    $"Invalid serialized hierarchy Parent {child} -> {parent}.",
                    exception);
            }

            if (_store._owner.Has<Parent<TDomain>>(child))
                throw new InvalidDataException($"Hierarchy child {child} already has Parent during import.");

            _store._owner.BeginEdit();
            try
            {
                _store.WriteCanonical(child, new CanonicalParent(true, parent));
                _store._appliedParents.Add(child, parent);
            }
            finally
            {
                _store._owner.EndEdit();
            }

            Dictionary<Entity, int> childrenPerParent = _childrenPerParent
                ?? throw new InvalidOperationException(
                    "Hierarchy topology import allocation metadata was already released.");
            childrenPerParent.TryGetValue(parent, out int count);
            childrenPerParent[parent] = checked(count + 1);
            _parentImportOrdinals!.Add(child, _parentCount);
            _parentCount++;
        }

        internal void AddOrderedSequence(Entity parent, Entity[] ownedChildren)
        {
            RequireOpen();
            RequireParentsSealed();
            if (_expectedOrderedSequenceCount < 0)
                throw new InvalidOperationException("Hierarchy ordered sequence count has not been read.");
            ArgumentNullException.ThrowIfNull(ownedChildren);
            if (_orderedSequenceCount >= _expectedOrderedSequenceCount)
                throw new InvalidDataException("Hierarchy ordered payload contains more sequences than declared.");
            if (!_store._owner.Alive(parent) || _store._owner.Pending(parent))
                throw new InvalidDataException($"Serialized ordered hierarchy parent {parent} is not live.");
            if (_store._policies.ContainsKey(parent))
                throw new InvalidDataException($"Duplicate ordered hierarchy policy for parent {parent}.");

            Dictionary<Entity, int> childrenPerParent = _childrenPerParent
                ?? throw new InvalidOperationException(
                    "Hierarchy topology import allocation metadata was already released.");
            childrenPerParent.TryGetValue(parent, out int expectedChildren);
            if (ownedChildren.Length != expectedChildren)
            {
                throw new InvalidDataException(
                    $"Ordered hierarchy sequence for {parent} has {ownedChildren.Length} entries but canonical Parent values produce {expectedChildren} children.");
            }

            for (int i = 0; i < ownedChildren.Length; i++)
            {
                Entity child = ownedChildren[i];
                CanonicalParent canonical = _store.ReadCanonical(child);
                if (!canonical.HasParent || canonical.Parent != parent)
                {
                    throw new InvalidDataException(
                        $"Ordered hierarchy sequence for {parent} contains {child}, whose canonical Parent differs.");
                }
            }

            OrderedChildShard? ownedShard = null;
            if (ownedChildren.Length != 0)
            {
                try
                {
                    ownedShard = OrderedChildShard.TakeOwnership(
                        ownedChildren,
                        _store._orderDiagnostics);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"Ordered hierarchy sequence for {parent} repeats a child.",
                        exception);
                }
            }

            _store._policies.Add(parent, ChildOrderPolicy.Ordered);
            if (ownedShard is not null)
                _store._ordered.Add(parent, ownedShard);

            _store._owner.BeginEdit();
            try
            {
                _store.PublishChildren(parent);
            }
            finally
            {
                _store._owner.EndEdit();
            }

            _orderedSequenceCount++;
        }

        internal void Complete()
        {
            RequireOpen();
            RequireParentsSealed();
            if (_parentCount != _expectedParentCount)
            {
                throw new InvalidDataException(
                    $"Hierarchy Parent payload declared {_expectedParentCount} entries but supplied {_parentCount}.");
            }
            if (_expectedOrderedSequenceCount < 0)
                throw new InvalidDataException("Hierarchy ordered sequence count is missing.");
            if (_orderedSequenceCount != _expectedOrderedSequenceCount)
            {
                throw new InvalidDataException(
                    $"Hierarchy ordered payload declared {_expectedOrderedSequenceCount} sequences but supplied {_orderedSequenceCount}.");
            }

            Dictionary<Entity, int> childrenPerParent = _childrenPerParent
                ?? throw new InvalidOperationException(
                    "Hierarchy topology import allocation metadata was already released.");
            foreach (KeyValuePair<Entity, int> pair in childrenPerParent)
            {
                if (!_store._policies.ContainsKey(pair.Key))
                {
                    _store._unordered.Add(
                        pair.Key,
                        UnorderedChildShard.CreateForImport(pair.Value));
                }
            }

            foreach (KeyValuePair<Entity, Entity> pair in _store._appliedParents)
            {
                Entity child = pair.Key;
                Entity parent = pair.Value;
                if (!_store._policies.ContainsKey(parent))
                    _store._unordered[parent].AddImported(child);
            }

            foreach (KeyValuePair<Entity, UnorderedChildShard> pair in _store._unordered)
            {
                if (!pair.Value.IsImportComplete)
                {
                    throw new InvalidDataException(
                        $"Hierarchy inverse for {pair.Key} did not receive its declared canonical children.");
                }

                _store._owner.BeginEdit();
                try
                {
                    _store.PublishChildren(pair.Key);
                }
                finally
                {
                    _store._owner.EndEdit();
                }
            }

            _childrenPerParent = null;
            _completed = true;
        }

    }

    internal void SetOrderPolicy(Entity parent, ChildOrderPolicy policy) =>
        SetOrderPolicy(parent, policy, ReadOnlySpan<Entity>.Empty, hasPermutation: false);

    internal void SetOrderPolicy(
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation) =>
        SetOrderPolicy(parent, policy, permutation, hasPermutation: true);

    private void SetOrderPolicy(
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation,
        bool hasPermutation)
    {
        _owner.EnsureAlive(parent, "parent");
        Maintain();

        ChildOrderPolicy currentPolicy = Policy(parent);
        ReadOnlySpan<Entity> current = TryGetShard(parent, out var currentShard)
            ? currentShard.Items
            : ReadOnlySpan<Entity>.Empty;

        if (hasPermutation)
            ValidatePermutation(current, permutation);

        if (policy == ChildOrderPolicy.Unordered && permutation.Length > 0)
        {
            throw new InvalidOperationException(
                "An unordered child shard does not accept a semantic permutation.");
        }

        if (currentPolicy == policy && !hasPermutation)
            return;

        EnsureWritable();
        _owner.BeginEdit();
        try
        {
            if (policy == ChildOrderPolicy.Ordered)
            {
                _orderDiagnostics.RecordOrderedPath();
                Entity[] ordered = hasPermutation
                    ? permutation.ToArray()
                    : StableEntities(current);
                _unordered.Remove(parent);
                if (ordered.Length == 0)
                    _ordered.Remove(parent);
                else
                    _ordered[parent] = new OrderedChildShard(ordered, _orderDiagnostics);
                _policies[parent] = ChildOrderPolicy.Ordered;
            }
            else
            {
                _ordered.Remove(parent);
                if (current.Length == 0)
                    _unordered.Remove(parent);
                else
                    _unordered[parent] = new UnorderedChildShard(current);
                _policies.Remove(parent);
            }

            PublishChildren(parent);
        }
        finally
        {
            _owner.EndEdit();
        }
    }

    internal void Reorder(Entity child, int insertIndex)
    {
        _owner.EnsureAlive(child, "child");
        Maintain();
        var canonical = ReadCanonical(child);
        if (!canonical.HasParent)
            throw new InvalidOperationException("Cannot reorder an entity without Parent.");

        Entity parent = canonical.Parent;
        if (Policy(parent) != ChildOrderPolicy.Ordered ||
            !_ordered.ContainsKey(parent))
        {
            throw new InvalidOperationException("Reorder requires an ordered parent shard.");
        }

        EnsureWritable();
        OrderedChildShard shard = _ordered[parent];
        ValidateReorderIndex(shard.Count, insertIndex);
        _owner.BeginEdit();
        try
        {
            shard.Reorder(child, insertIndex);
            PublishChildren(parent);
        }
        finally
        {
            _owner.EndEdit();
        }
    }

    internal void Maintain()
    {
        var candidates = CollectCandidates();
        if (candidates.Length == 0 &&
            _normalizationParents.Count == 0 &&
            !_normalizeAllChildren)
            return;

        EnsureWritable();
        ValidateOrRollback(candidates);
        PreparedMaintenance prepared;
        try
        {
            prepared = PrepareMaintenance(candidates);
        }
        catch
        {
            // Raw owner-bound writes still have preimages and must roll back on any final-image
            // validation failure. Typed deferred commands deliberately discard their preimage;
            // they remain canonical and can be corrected by a later command.
            RollbackPreimages();
            throw;
        }

        _owner.BeginEdit();
        try
        {
            // All allocations and semantic checks above operate on detached state. Publishing
            // starts with a single applied-root replacement, so a bad later candidate can never
            // leave a partially edited child shard behind.
            _appliedParents = prepared.AppliedParents;
            _ordered = prepared.Ordered;
            _unordered = prepared.Unordered;
            for (int i = 0; i < prepared.AffectedParents.Length; i++)
                PublishChildren(prepared.AffectedParents[i]);
        }
        finally
        {
            _owner.EndEdit();
        }

        ClearDirtyEntities();
        _preimages.Clear();
        _pendingPlacements.Clear();
        _normalizationParents.Clear();
        _scanNeeded = false;
        _normalizeAllChildren = false;
    }

    private PreparedMaintenance PrepareMaintenance(Entity[] candidates)
    {
        var appliedParents = new Dictionary<Entity, Entity>(_appliedParents);
        // Shard dictionaries are detached roots, while their values are copied only when the
        // corresponding parent is actually edited. In particular, an unordered-only maintenance
        // batch in a mixed domain never scans, clones or renumbers an ordered shard.
        var ordered = new Dictionary<Entity, OrderedChildShard>(_ordered);
        var unordered = new Dictionary<Entity, UnorderedChildShard>(_unordered);
        var writableOrdered = new HashSet<Entity>();
        var writableUnordered = new HashSet<Entity>();

        var affectedParents = new HashSet<Entity>();
        for (int i = 0; i < candidates.Length; i++)
        {
            Entity child = candidates[i];
            Entity currentParent = Entity.Null;
            if (_owner.Alive(child))
            {
                var canonical = ReadCanonical(child);
                currentParent = canonical.HasParent ? canonical.Parent : Entity.Null;
            }

            appliedParents.TryGetValue(child, out Entity appliedParent);
            int? insertIndex = _pendingPlacements.TryGetValue(child, out var placement)
                ? placement.InsertIndex
                : null;

            if (appliedParent == currentParent)
            {
                if (insertIndex is not null && currentParent != Entity.Null)
                {
                    if (!ordered.TryGetValue(currentParent, out var siblings))
                    {
                        throw new InvalidOperationException(
                            "Explicit placement requires an ordered parent shard.");
                    }

                    int oldIndex = siblings.IndexOf(child);
                    if (oldIndex < 0)
                        throw new InvalidOperationException("Applied child is missing from its ordered shard.");
                    ValidateReorderIndex(siblings.Count, insertIndex.Value);
                    if (oldIndex != insertIndex.Value)
                    {
                        WritableOrderedShard(currentParent, ordered, writableOrdered)
                            .Reorder(child, insertIndex.Value);
                        affectedParents.Add(currentParent);
                    }
                }
                continue;
            }

            if (appliedParent != Entity.Null)
            {
                RemoveProjectedChild(
                    appliedParent,
                    child,
                    ordered,
                    unordered,
                    writableOrdered,
                    writableUnordered);
                affectedParents.Add(appliedParent);
            }

            if (currentParent == Entity.Null)
            {
                appliedParents.Remove(child);
                continue;
            }

            AddProjectedChild(
                currentParent,
                child,
                insertIndex,
                ordered,
                unordered,
                writableOrdered,
                writableUnordered);
            appliedParents[child] = currentParent;
            affectedParents.Add(currentParent);
        }

        foreach (Entity parent in _normalizationParents)
            affectedParents.Add(parent);

        if (_normalizeAllChildren)
        {
            foreach (Entity parent in EnumerateComponentEntities<Children<TDomain>>())
                affectedParents.Add(parent);
            foreach (Entity parent in ordered.Keys)
                affectedParents.Add(parent);
            foreach (Entity parent in unordered.Keys)
                affectedParents.Add(parent);
        }

        Entity[] parents = affectedParents.ToArray();
        Array.Sort(parents, EntityComparer.Instance);
        return new PreparedMaintenance(appliedParents, ordered, unordered, parents);
    }

    private void AddProjectedChild(
        Entity parent,
        Entity child,
        int? insertIndex,
        Dictionary<Entity, OrderedChildShard> ordered,
        Dictionary<Entity, UnorderedChildShard> unordered,
        HashSet<Entity> writableOrdered,
        HashSet<Entity> writableUnordered)
    {
        if (Policy(parent) == ChildOrderPolicy.Ordered)
        {
            OrderedChildShard siblings = WritableOrderedShard(parent, ordered, writableOrdered);
            siblings.Add(child, insertIndex);
            return;
        }

        if (insertIndex is not null)
            throw new InvalidOperationException("Unordered parent shards do not accept an index.");
        if (!unordered.TryGetValue(parent, out var children))
        {
            children = new UnorderedChildShard();
            unordered.Add(parent, children);
            writableUnordered.Add(parent);
        }
        else if (writableUnordered.Add(parent))
        {
            children = new UnorderedChildShard(children);
            unordered[parent] = children;
        }
        children.Add(child, insertIndex: null);
    }

    private void RemoveProjectedChild(
        Entity parent,
        Entity child,
        Dictionary<Entity, OrderedChildShard> ordered,
        Dictionary<Entity, UnorderedChildShard> unordered,
        HashSet<Entity> writableOrdered,
        HashSet<Entity> writableUnordered)
    {
        if (Policy(parent) == ChildOrderPolicy.Ordered)
        {
            if (!ordered.ContainsKey(parent))
                throw new InvalidOperationException("Applied child is missing from its parent-local shard.");
            OrderedChildShard children = WritableOrderedShard(parent, ordered, writableOrdered);
            if (!children.Remove(child))
                throw new InvalidOperationException("Applied child is missing from its parent-local shard.");
            if (children.Count == 0)
                ordered.Remove(parent);
            return;
        }

        if (!unordered.TryGetValue(parent, out var unorderedChildren))
        {
            throw new InvalidOperationException("Applied child is missing from its parent-local shard.");
        }
        if (writableUnordered.Add(parent))
        {
            unorderedChildren = new UnorderedChildShard(unorderedChildren);
            unordered[parent] = unorderedChildren;
        }
        if (!unorderedChildren.Remove(child))
            throw new InvalidOperationException("Applied child is missing from its parent-local shard.");
        if (unorderedChildren.Count == 0)
            unordered.Remove(parent);
    }

    private OrderedChildShard WritableOrderedShard(
        Entity parent,
        Dictionary<Entity, OrderedChildShard> ordered,
        HashSet<Entity> writable)
    {
        if (!ordered.TryGetValue(parent, out var shard))
        {
            shard = new OrderedChildShard(_orderDiagnostics);
            ordered.Add(parent, shard);
            writable.Add(parent);
            return shard;
        }

        if (writable.Add(parent))
        {
            shard = new OrderedChildShard(shard, _orderDiagnostics);
            ordered[parent] = shard;
        }
        return shard;
    }

    private sealed record PreparedMaintenance(
        Dictionary<Entity, Entity> AppliedParents,
        Dictionary<Entity, OrderedChildShard> Ordered,
        Dictionary<Entity, UnorderedChildShard> Unordered,
        Entity[] AffectedParents);

    internal void DestroySubtree(World world, Entity root)
    {
        _owner.EnsureAlive(root, "subtree root");
        var children = BuildCanonicalChildren();
        var postorder = new List<Entity>();
        var active = new HashSet<Entity>();
        var complete = new HashSet<Entity>();
        BuildPostorder(root, children, active, complete, postorder);

        _owner.BeginTerminalDestroy(CollectionsMarshal.AsSpan(postorder));
        try
        {
            for (int i = 0; i < postorder.Count; i++)
            {
                Entity entity = postorder[i];
                if (!world.IsAlive(entity))
                    continue;

                world.DestroyEntity(entity);
            }
        }
        finally
        {
            _owner.EndTerminalDestroy();
        }
    }

    public void CaptureBeforeMutation(Entity entity)
    {
        if (_owner.IsEditing || !_owner.Alive(entity))
            return;

        EnsureWritable();
        // Maintenance dirtiness and owner-transaction capture have different lifetimes.
        // An entity may already be waiting for Maintain() when a later query owner writes it;
        // that later owner still needs its own canonical preimage for validation/fault rollback.
        if (!_preimages.ContainsKey(entity))
            _preimages[entity] = ToPreimage(ReadCanonical(entity));
        _dirty.Add(entity);
        // A ref/span owner can change Parent after Capture returns, so the previous parent-local
        // index is no longer authoritative. It is rebuilt once at validation or first ordinary
        // destroy, then maintained incrementally.
        _dirtyParentIndexValid = false;
    }

    public void RequireScan()
    {
        if (_owner.IsEditing || _scanNeeded)
            return;

        EnsureWritable();
        foreach (var (child, _) in EnumerateCanonicalParents())
            CaptureBeforeMutation(child);
        foreach (Entity child in _appliedParents.Keys)
            CaptureBeforeMutation(child);

        _scanNeeded = true;
    }

    public void RequireChildrenNormalization(Entity parent)
    {
        if (_owner.IsEditing)
            return;

        EnsureWritable();
        _normalizationParents.Add(parent);
    }

    public void ValidateDeferredWrites()
    {
        EnsureDirtyParentIndex();
        var candidates = CollectCandidates();
        if (candidates.Length == 0)
            return;

        ValidateOrRollback(candidates);
    }

    public void RollbackDeferredWrites()
    {
        RollbackPreimages();
    }

    public void CommitDeferredWrites()
    {
        if (_preimages.Count == 0 && !_scanNeeded)
            return;

        EnsureWritable();
        _preimages.Clear();
        _scanNeeded = false;
    }

    public void BeginTerminalDestroy(ReadOnlySpan<Entity> terminalEntities)
    {
        if (_terminalDirectChildren is not null)
            throw new InvalidOperationException($"A {typeof(TDomain).Name} terminal-destroy plan is already active.");

        var directChildren = new Dictionary<Entity, HashSet<Entity>>();
        foreach (var (child, parent) in EnumerateCanonicalParents())
        {
            if (!_owner.IsTerminallyDestroying(parent))
                continue;

            if (!directChildren.TryGetValue(parent, out HashSet<Entity>? children))
            {
                children = new HashSet<Entity>();
                directChildren.Add(parent, children);
            }
            children.Add(child);
        }

        // A deferred canonical write can make the applied inverse differ from Parent. Preserve
        // both images in the terminal plan so external children are detached even when their
        // canonical parent already moved elsewhere.
        foreach (Entity parent in terminalEntities)
        {
            if (!TryGetShard(parent, out ChildShard shard))
                continue;

            if (!directChildren.TryGetValue(parent, out HashSet<Entity>? children))
            {
                children = new HashSet<Entity>();
                directChildren.Add(parent, children);
            }
            ReadOnlySpan<Entity> applied = shard.Items;
            for (int i = 0; i < applied.Length; i++)
                children.Add(applied[i]);
        }

        var plan = new Dictionary<Entity, Entity[]>(directChildren.Count);
        foreach (var pair in directChildren)
        {
            Entity[] children = pair.Value.ToArray();
            Array.Sort(children, EntityComparer.Instance);
            plan.Add(pair.Key, children);
        }
        _terminalDirectChildren = plan;
    }

    public void EndTerminalDestroy()
    {
        _terminalDirectChildren = null;
    }

    public void OnEntityDestroying(Entity entity)
    {
        if (!_owner.Alive(entity))
            return;

        int directChildCapacity = DirectChildCapacity(entity);

        bool hasLocalState =
            directChildCapacity != 0 ||
            _appliedParents.ContainsKey(entity) ||
            _ordered.ContainsKey(entity) ||
            _unordered.ContainsKey(entity) ||
            _policies.ContainsKey(entity) ||
            _publishedChildren.ContainsKey(entity) ||
            _dirty.Contains(entity) ||
            _preimages.ContainsKey(entity) ||
            _pendingPlacements.ContainsKey(entity) ||
            _normalizationParents.Contains(entity);
        if (!hasLocalState)
            return;

        EnsureWritable();

        DetachDirectChildren(entity, directChildCapacity);

        // The entity itself is about to be removed by Owners.Entities. Do not structurally remove
        // its canonical Parent here: doing so would create Removed<Parent<TDomain>> cleanup state
        // and accidentally turn an ordinary hard destroy into a pending entity. Only detach the
        // already-applied inverse; RemoveAll will discard the canonical component moments later.
        if (_appliedParents.Remove(entity, out Entity appliedParent))
        {
            RemoveFromShard(appliedParent, entity);
            PublishChildren(appliedParent);
        }

        _ordered.Remove(entity);
        _unordered.Remove(entity);
        _policies.Remove(entity);
        if (_publishedChildren.TryRemove(entity, out _))
            _inverseRevision = NextInverseRevision();
        RemoveDirtyEntity(entity);
        _preimages.Remove(entity);
        _pendingPlacements.Remove(entity);
        _normalizationParents.Remove(entity);
    }

    private int DirectChildCapacity(Entity parent)
    {
        if (_terminalDirectChildren is not null)
        {
            // The terminal plan is a complete image. A missing key is a leaf, not permission to
            // fall back to the dirty set; doing that for every leaf recreates O(N^2) subtree
            // destruction when many canonical parents are deferred.
            return _terminalDirectChildren.TryGetValue(parent, out Entity[]? planned)
                ? planned.Length
                : 0;
        }

        int capacity = TryGetShard(parent, out ChildShard shard) ? shard.Count : 0;

        EnsureDirtyParentIndex();
        _dirtyParentLookupCount++;
        if (_dirtyChildrenByParent.TryGetValue(parent, out HashSet<Entity>? dirtyChildren))
        {
            _dirtyParentLookupEntityVisits += dirtyChildren.Count;
            capacity = checked(capacity + dirtyChildren.Count);
        }

        return capacity;
    }

    private void DetachDirectChildren(Entity parent, int capacity)
    {
        if (capacity == 0)
            return;

        Entity[]? rented = null;
        Span<Entity> children = capacity <= 128
            ? stackalloc Entity[capacity]
            : (rented = ArrayPool<Entity>.Shared.Rent(capacity)).AsSpan(0, capacity);
        try
        {
            int count = FillDirectChildren(parent, children);
            Span<Entity> directChildren = children[..count];
            directChildren.Sort(EntityComparer.Instance);
            for (int i = 0; i < directChildren.Length; i++)
            {
                Entity child = directChildren[i];
                if (!_owner.Alive(child))
                    continue;

                CanonicalParent current = ReadCanonical(child);
                if (current.HasParent && current.Parent == parent)
                    WriteCanonical(child, CanonicalParent.None);
                ApplyCurrent(child, insertIndex: null);
                CommitApplied(child);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Entity>.Shared.Return(rented);
        }
    }

    private int FillDirectChildren(Entity parent, Span<Entity> destination)
    {
        int count = 0;
        if (_terminalDirectChildren is not null)
        {
            if (_terminalDirectChildren.TryGetValue(parent, out Entity[]? planned))
                planned.CopyTo(destination);
            return planned?.Length ?? 0;
        }

        if (TryGetShard(parent, out ChildShard shard))
        {
            ReadOnlySpan<Entity> applied = shard.Items;
            for (int i = 0; i < applied.Length; i++)
                AddUnique(destination, ref count, applied[i]);
        }

        if (_dirtyChildrenByParent.TryGetValue(parent, out HashSet<Entity>? dirtyChildren))
        {
            foreach (Entity child in dirtyChildren)
                AddUnique(destination, ref count, child);
        }

        return count;
    }

    private static void AddUnique(Span<Entity> destination, ref int count, Entity entity)
    {
        for (int i = 0; i < count; i++)
        {
            if (destination[i] == entity)
                return;
        }

        destination[count++] = entity;
    }

    public void Reset()
    {
        EnsureWritable();
        _appliedParents.Clear();
        _preimages.Clear();
        ClearDirtyEntities();
        _pendingPlacements.Clear();
        _policies.Clear();
        _ordered.Clear();
        _unordered.Clear();
        _publishedChildren.Clear();
        _normalizationParents.Clear();
        _terminalDirectChildren = null;
        _dirtyParentIndexValid = true;
        _scanNeeded = true;
        _normalizeAllChildren = true;
        _generation = 1;
        _deferredSequence = 0;
        _inverseRevision = NextInverseRevision();
        _orderDiagnostics.Reset();
    }

    private void ValidateParent(Entity child, Entity parent)
    {
        ValidateParentEndpoints(child, parent);

        Entity current = parent;
        int remaining = _owner.EntityCount + 1;
        while (current != Entity.Null)
        {
            if (current == child)
                throw new InvalidOperationException("Parent would create a hierarchy cycle.");

            if (remaining-- <= 0)
                throw new InvalidOperationException("Existing Parent chain contains a cycle.");

            if (!_owner.Alive(current) || _owner.Pending(current))
                throw new InvalidOperationException("Parent chain contains a non-live entity.");

            var next = ReadCanonical(current);
            if (next.HasParent && next.Parent == Entity.Null)
                throw new InvalidOperationException("Parent component cannot contain Entity.Null.");
            current = next.Parent;
        }
    }

    private void ValidateCandidate(Entity child)
    {
        if (!_owner.Alive(child))
            return;

        var canonical = ReadCanonical(child);
        if (!canonical.HasParent)
            return;

        ValidateParent(child, canonical.Parent);
    }

    private void ValidatePlacement(
        Entity child,
        Entity parent,
        int? insertIndex,
        bool immediate)
    {
        if (insertIndex is null)
            return;

        if (Policy(parent) != ChildOrderPolicy.Ordered)
            throw new InvalidOperationException("Explicit placement requires an ordered parent shard.");

        if (insertIndex.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        // Deferred indices are interpreted against the stable FIFO projected image during
        // Maintain(). Checking against the current applied count here would reject valid streams
        // such as insert@0 followed by insert@1 into an initially empty ordered parent.
        if (!immediate)
            return;

        int count = _ordered.TryGetValue(parent, out var shard) ? shard.Count : 0;
        bool alreadyApplied = _appliedParents.TryGetValue(child, out var applied) &&
                              applied == parent &&
                              shard?.Contains(child) == true;
        int maximum = alreadyApplied ? Math.Max(0, count - 1) : count;
        if ((uint)insertIndex.Value > (uint)maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(insertIndex),
                $"Ordered insertion index must be in [0, {maximum}].");
        }
    }

    private void ApplyCurrent(Entity child, int? insertIndex)
    {
        EnsureWritable();
        var canonical = ReadCanonical(child);
        Entity currentParent = canonical.HasParent ? canonical.Parent : Entity.Null;
        _appliedParents.TryGetValue(child, out Entity appliedParent);

        if (appliedParent == currentParent)
        {
            if (insertIndex is not null && currentParent != Entity.Null)
            {
                if (!_ordered.TryGetValue(currentParent, out var ordered))
                    throw new InvalidOperationException("Explicit placement requires an ordered parent shard.");
                ordered.Reorder(child, insertIndex.Value);
                PublishChildren(currentParent);
            }
            return;
        }

        if (appliedParent != Entity.Null)
        {
            RemoveFromShard(appliedParent, child);
            PublishChildren(appliedParent);
        }

        if (currentParent != Entity.Null)
        {
            AddToShard(currentParent, child, insertIndex);
            _appliedParents[child] = currentParent;
            PublishChildren(currentParent);
        }
        else
        {
            _appliedParents.Remove(child);
        }
    }

    private void ApplyDestroyedChild(Entity child)
    {
        EnsureWritable();
        if (!_appliedParents.Remove(child, out Entity appliedParent))
            return;

        RemoveFromShard(appliedParent, child);
        PublishChildren(appliedParent);
    }

    private void AddToShard(Entity parent, Entity child, int? insertIndex)
    {
        EnsureWritable();
        if (Policy(parent) == ChildOrderPolicy.Ordered)
        {
            if (!_ordered.TryGetValue(parent, out var shard))
            {
                shard = new OrderedChildShard(_orderDiagnostics);
                _ordered.Add(parent, shard);
            }

            shard.Add(child, insertIndex);
            return;
        }

        if (insertIndex is not null)
            throw new InvalidOperationException("Unordered parent shards do not accept an index.");

        if (!_unordered.TryGetValue(parent, out var unordered))
        {
            unordered = new UnorderedChildShard();
            _unordered.Add(parent, unordered);
        }

        unordered.Add(child, insertIndex: null);
    }

    private void RemoveFromShard(Entity parent, Entity child)
    {
        EnsureWritable();
        if (Policy(parent) == ChildOrderPolicy.Ordered)
        {
            if (_ordered.TryGetValue(parent, out var shard) && shard.Remove(child) && shard.Count == 0)
                _ordered.Remove(parent);
            return;
        }

        if (_unordered.TryGetValue(parent, out var unordered) &&
            unordered.Remove(child) &&
            unordered.Count == 0)
        {
            _unordered.Remove(parent);
        }
    }

    private void PublishChildren(Entity parent)
    {
        if (parent == Entity.Null || !_owner.Alive(parent))
            return;

        EnsureWritable();
        ulong generation = NextGeneration();
        if (_owner.IsTerminallyDestroying(parent))
            return;

        bool hasShard = TryGetShard(parent, out var shard);
        int count = hasShard ? shard.Count : 0;
        if (count == 0)
        {
            if (_owner.Has<Children<TDomain>>(parent))
                _owner.RemoveRelationshipComponent<Children<TDomain>>(parent);
            _publishedChildren[parent] = new PublishedChildren(Array.Empty<Entity>(), generation);
            _inverseRevision = NextInverseRevision();
            return;
        }

        // Publish the shard's owned array itself. The shard switches to copy-on-write before any
        // later mutation, so import can hand one final array directly to both storage and readers.
        Entity[] published = shard.PublishSnapshot();

        var token = default(Children<TDomain>);
        if (_owner.Has<Children<TDomain>>(parent))
            _owner.ReplaceRelationshipComponent(parent, in token);
        else
            _owner.AddRelationshipComponent(parent, in token);
        _publishedChildren[parent] = new PublishedChildren(published, generation);
        _inverseRevision = NextInverseRevision();
    }

    private void NormalizeChildrenComponents()
    {
        EnsureWritable();
        var existing = new List<Entity>();
        foreach (Entity entity in EnumerateComponentEntities<Children<TDomain>>())
            existing.Add(entity);
        var existingSet = new HashSet<Entity>(existing);
        existing.Sort(EntityComparer.Instance);
        for (int i = 0; i < existing.Count; i++)
            PublishChildren(existing[i]);

        var parentSet = new HashSet<Entity>(_ordered.Keys);
        parentSet.UnionWith(_unordered.Keys);
        var parents = new List<Entity>(parentSet);
        parents.Sort(EntityComparer.Instance);
        for (int i = 0; i < parents.Count; i++)
        {
            if (!existingSet.Contains(parents[i]))
                PublishChildren(parents[i]);
        }
    }

    private void ValidateOrRollback(Entity[] candidates)
    {
        try
        {
            for (int i = 0; i < candidates.Length; i++)
                ValidateCandidate(candidates[i]);
        }
        catch
        {
            RollbackPreimages();
            throw;
        }
    }

    private void RollbackPreimages()
    {
        if (_preimages.Count == 0)
            return;

        EnsureWritable();
        var entries = _preimages.ToArray();
        Array.Sort(entries, static (left, right) => EntityComparer.Instance.Compare(left.Key, right.Key));

        _owner.BeginEdit();
        try
        {
            for (int i = 0; i < entries.Length; i++)
            {
                Entity child = entries[i].Key;
                if (!_owner.Alive(child))
                    continue;
                WriteCanonical(child, FromPreimage(entries[i].Value));
            }
        }
        finally
        {
            _owner.EndEdit();
        }

        foreach (var entry in entries)
        {
            RemoveDirtyEntity(entry.Key);
            if (_owner.Alive(entry.Key) &&
                ReadCanonical(entry.Key).Parent != AppliedParent(entry.Key))
            {
                MarkDirtyEntity(entry.Key);
            }
        }
        _preimages.Clear();
        _scanNeeded = false;
    }

    private Entity[] CollectCandidates()
    {
        var candidates = new HashSet<Entity>(_dirty);
        if (_scanNeeded)
        {
            foreach (var (child, _) in EnumerateCanonicalParents())
                candidates.Add(child);
            foreach (Entity child in _appliedParents.Keys)
                candidates.Add(child);
        }

        var result = candidates.ToArray();
        Array.Sort(result, CompareMaintenanceOrder);
        return result;
    }

    private Dictionary<Entity, List<Entity>> BuildCanonicalChildren()
    {
        var result = new Dictionary<Entity, List<Entity>>();
        foreach (var (child, parent) in EnumerateCanonicalParents())
        {
            if (!result.TryGetValue(parent, out var children))
            {
                children = new List<Entity>();
                result.Add(parent, children);
            }
            children.Add(child);
        }

        foreach (var children in result.Values)
            children.Sort(EntityComparer.Instance);
        return result;
    }

    private static void BuildPostorder(
        Entity entity,
        Dictionary<Entity, List<Entity>> children,
        HashSet<Entity> active,
        HashSet<Entity> complete,
        List<Entity> result)
    {
        if (complete.Contains(entity))
            return;
        if (!active.Add(entity))
            throw new InvalidOperationException("Cannot destroy a cyclic hierarchy subtree.");

        if (children.TryGetValue(entity, out var direct))
        {
            for (int i = 0; i < direct.Count; i++)
                BuildPostorder(direct[i], children, active, complete, result);
        }

        active.Remove(entity);
        complete.Add(entity);
        result.Add(entity);
    }

    private IEnumerable<(Entity Child, Entity Parent)> EnumerateCanonicalParents()
    {
        _canonicalParentFullScanCount++;
        int componentId = ParentComponentId;
        for (int archetypeIndex = 0; archetypeIndex < _owner.ArchetypeCount; archetypeIndex++)
        {
            var archetype = _owner.ArchetypeAt(archetypeIndex);
            if (!archetype.TryColumn(componentId, out int columnIndex))
                continue;

            int chunkCount = archetype.ChunkCount;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var chunk = archetype.ChunkAt(chunkIndex);
                for (int row = 0; row < chunk.Count; row++)
                {
                    Entity child = chunk.Entities[row];
                    if (_owner.Alive(child))
                    {
                        Entity parent = chunk.ComponentRows<Parent<TDomain>>(columnIndex)[row].Value;
                        yield return (child, parent);
                    }
                }
            }
        }
    }

    private IEnumerable<Entity> EnumerateComponentEntities<T>()
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        for (int archetypeIndex = 0; archetypeIndex < _owner.ArchetypeCount; archetypeIndex++)
        {
            var archetype = _owner.ArchetypeAt(archetypeIndex);
            if (!archetype.HasComponent(componentId))
                continue;

            int chunkCount = archetype.ChunkCount;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var chunk = archetype.ChunkAt(chunkIndex);
                for (int row = 0; row < chunk.Count; row++)
                {
                    Entity entity = chunk.Entities[row];
                    if (_owner.Alive(entity))
                        yield return entity;
                }
            }
        }
    }

    private CanonicalParent ReadCanonical(Entity child)
    {
        if (!_owner.Alive(child) || !_owner.Has<Parent<TDomain>>(child))
            return CanonicalParent.None;

        return new CanonicalParent(true, _owner.Read<Parent<TDomain>>(child).Value);
    }

    private void WriteCanonical(Entity child, CanonicalParent canonical)
    {
        bool hasParent = _owner.Has<Parent<TDomain>>(child);
        if (!canonical.HasParent)
        {
            if (hasParent)
                _owner.RemoveRelationshipComponent<Parent<TDomain>>(child);
        }
        else
        {
            var value = new Parent<TDomain>(canonical.Parent);
            if (hasParent)
                _owner.ReplaceRelationshipComponent(child, in value);
            else
                _owner.AddRelationshipComponent(child, in value);
        }

        if (_dirty.Contains(child) && _dirtyParentIndexValid)
            IndexDirtyCanonicalParent(child);
    }

    private void CommitApplied(Entity child)
    {
        EnsureWritable();
        RemoveDirtyEntity(child);
        _preimages.Remove(child);
        _pendingPlacements.Remove(child);
    }

    private void MarkDeferred(Entity child, int? insertIndex)
    {
        EnsureWritable();
        MarkDirtyEntity(child);
        var canonical = ReadCanonical(child);
        Entity currentParent = canonical.HasParent ? canonical.Parent : Entity.Null;
        Entity appliedParent = AppliedParent(child);
        bool touchesOrderedShard =
            (appliedParent != Entity.Null && Policy(appliedParent) == ChildOrderPolicy.Ordered) ||
            (currentParent != Entity.Null && Policy(currentParent) == ChildOrderPolicy.Ordered);
        if (touchesOrderedShard)
        {
            _pendingPlacements[child] = new PendingChildPlacement(
                insertIndex,
                checked(++_deferredSequence));
            _orderDiagnostics.RecordPlacementMetadataWrite(
                System.Runtime.CompilerServices.Unsafe.SizeOf<PendingChildPlacement>());
        }
        else
        {
            // Pure unordered transitions have no semantic command order and pay no placement or
            // sequence metadata. Their deterministic maintenance tie-break is the entity key.
            _pendingPlacements.Remove(child);
        }
    }

    private void MarkDirtyEntity(Entity child)
    {
        _dirty.Add(child);
        if (_dirtyParentIndexValid)
            IndexDirtyCanonicalParent(child);
    }

    private void RemoveDirtyEntity(Entity child)
    {
        _dirty.Remove(child);
        if (!_dirtyParentIndexValid ||
            !_dirtyIndexedParentByChild.Remove(child, out Entity indexedParent))
        {
            return;
        }

        if (_dirtyChildrenByParent.TryGetValue(indexedParent, out HashSet<Entity>? children) &&
            children.Remove(child) &&
            children.Count == 0)
        {
            _dirtyChildrenByParent.Remove(indexedParent);
        }
    }

    private void ClearDirtyEntities()
    {
        _dirty.Clear();
        _dirtyIndexedParentByChild.Clear();
        _dirtyChildrenByParent.Clear();
        _dirtyParentIndexValid = true;
    }

    private void EnsureDirtyParentIndex()
    {
        if (_dirtyParentIndexValid)
            return;

        EnsureWritable();
        _dirtyIndexedParentByChild.Clear();
        _dirtyChildrenByParent.Clear();
        foreach (Entity child in _dirty)
        {
            _dirtyParentIndexRebuildEntityVisits++;
            IndexDirtyCanonicalParent(child);
        }
        _dirtyParentIndexValid = true;
    }

    private void IndexDirtyCanonicalParent(Entity child)
    {
        if (_dirtyIndexedParentByChild.Remove(child, out Entity previousParent) &&
            _dirtyChildrenByParent.TryGetValue(previousParent, out HashSet<Entity>? previousChildren) &&
            previousChildren.Remove(child) &&
            previousChildren.Count == 0)
        {
            _dirtyChildrenByParent.Remove(previousParent);
        }

        if (!_dirty.Contains(child) || !_owner.Alive(child))
            return;
        CanonicalParent canonical = ReadCanonical(child);
        if (!canonical.HasParent)
            return;

        _dirtyIndexedParentByChild[child] = canonical.Parent;
        if (!_dirtyChildrenByParent.TryGetValue(canonical.Parent, out HashSet<Entity>? children))
        {
            children = new HashSet<Entity>();
            _dirtyChildrenByParent.Add(canonical.Parent, children);
        }
        children.Add(child);
    }

}
