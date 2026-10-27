using System.Collections;
using System.Buffers;
using System.Collections.Immutable;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Material assignments derived from the rendering world.</summary>
public sealed class ClusterMaterialAssignments
{
    private ClusterMaterialAssignmentSet _current = ClusterMaterialAssignmentSet.Empty;
    public ClusterMaterialAssignmentSet Current => Volatile.Read(ref _current);
    public ClusterMaterialOffsetProducer CreateProducer() => new(this);

    internal void Publish(ClusterMaterialAssignmentSet next) => Volatile.Write(ref _current, next);

    internal void AcceptMaterialChanges(ClusterMaterialAssignmentSet consumed)
    {
        if (ReferenceEquals(Current, consumed))
            Volatile.Write(ref _current, consumed.WithoutChanges());
    }
}

/// <summary>
/// Shared material references with persistent mesh-local offsets. Removing a sequence does not
/// renumber another entity; unused ranges can be reused by later sequences of the same length.
/// </summary>
public sealed class ClusterMaterialAssignmentSet
{
    internal static ClusterMaterialAssignmentSet Empty { get; } = new();
    private ImmutableDictionary<Entity, ClusterMaterialSequence> _entities = ImmutableDictionary<Entity, ClusterMaterialSequence>.Empty;
    private ImmutableDictionary<ClusterMaterialSequence, (uint Offset, int References)> _sequences = ImmutableDictionary<ClusterMaterialSequence, (uint, int)>.Empty;
    private ImmutableDictionary<int, ImmutableStack<uint>> _free = ImmutableDictionary<int, ImmutableStack<uint>>.Empty;
    private ImmutableDictionary<int, (long Material, string Encoding)> _entries = ImmutableDictionary<int, (long, string)>.Empty;
    private ImmutableDictionary<long, (Material Resource, ulong Revision, int PropertyCount, bool Ordered)> _values =
        ImmutableDictionary<long, (Material Resource, ulong Revision, int PropertyCount, bool Ordered)>.Empty;
    // Captured revisions belong to this published set; the index map is shared only
    // while material membership is fixed. Ordered and property count remain captured values.
    private Dictionary<long, int>? _revisionIndices;
    private ulong[]? _denseRevisions;
    private ImmutableDictionary<long, ulong> _sparseRevisions = ImmutableDictionary<long, ulong>.Empty;
    private ImmutableDictionary<long, ImmutableHashSet<int>> _materialOffsets = ImmutableDictionary<long, ImmutableHashSet<int>>.Empty;
    private ImmutableHashSet<long> _changes = ImmutableHashSet<long>.Empty;
    private ImmutableHashSet<int> _changedOffsets = ImmutableHashSet<int>.Empty;
    private ImmutableHashSet<Entity> _xrayEntities = ImmutableHashSet<Entity>.Empty;
    private ImmutableHashSet<long> _xrayMaterials = ImmutableHashSet<long>.Empty;
    private uint _capacity;
    private ulong _contentVersion;
    private ulong _changesFromVersion;

    private ClusterMaterialAssignmentSet() { }
    private ClusterMaterialAssignmentSet(ClusterMaterialAssignmentSet source)
    {
        _entities = source._entities;
        _sequences = source._sequences;
        _free = source._free;
        _entries = source._entries;
        _values = source._values;
        _revisionIndices = source._revisionIndices;
        _denseRevisions = source._denseRevisions;
        _sparseRevisions = source._sparseRevisions;
        _materialOffsets = source._materialOffsets;
        _changes = source._changes;
        _changedOffsets = source._changedOffsets;
        _capacity = source._capacity;
        _contentVersion = source._contentVersion;
        _changesFromVersion = source._changesFromVersion;
        _xrayEntities = source._xrayEntities;
        _xrayMaterials = source._xrayMaterials;
        BlendOcclusionChanged = source.BlendOcclusionChanged;
        TopologyVersion = source.TopologyVersion;
    }

    public ulong TopologyVersion { get; private set; }
    public uint SlotCount => checked((uint)_entries.Count);
    public uint SlotCapacity => Math.Max(1u, _capacity);
    public uint MaterialCount => checked((uint)_values.Count);
    public IEnumerable<Material> DistinctMaterials => _values.Values.Select(value => value.Resource);
    internal bool HasXray => !_xrayMaterials.IsEmpty;
    internal bool UsesXray(Material material) => _xrayMaterials.Contains(material.MaterialId);
    internal bool HasOrdered => _values.Values.Any(value => value.Ordered);
    internal bool BlendOcclusionChanged { get; private set; }
    internal IReadOnlyCollection<long> ChangedMaterialIds => _changes;
    internal IEnumerable<Material> ChangedMaterials => _changes.Where(_values.ContainsKey).Select(id => _values[id].Resource);
    internal IEnumerable<int> ActiveOffsets => _entries.Keys;
    internal IEnumerable<(int Offset, int Count)> MaterialRanges =>
        _sequences.Select(pair => (checked((int)pair.Value.Offset), pair.Key.Count));
    internal IReadOnlyCollection<int> ChangedOffsets => _changedOffsets;
    internal IEnumerable<Entity> Entities => _entities.Keys;
    internal IEnumerable<Entity> TranslucentEntities
    {
        get
        {
            foreach ((Entity entity, ClusterMaterialSequence sequence) in _entities)
                for (int index = 0; index < sequence.Count; index++)
                    if (_values[sequence.MaterialAt(index)].Resource.State.Surface == SurfaceMode.Translucent)
                    {
                        yield return entity;
                        break;
                    }
        }
    }
    internal void CollectChangedEntities(HashSet<Entity> entities, HashSet<long> changedMaterials)
    {
        foreach ((Entity entity, ClusterMaterialSequence sequence) in _entities)
        {
            uint offset = _sequences[sequence].Offset;
            for (int index = 0; index < sequence.Count; index++)
                if (_changedOffsets.Contains(checked((int)offset + index)) ||
                    changedMaterials.Contains(sequence.MaterialAt(index)))
                {
                    entities.Add(entity);
                    break;
                }
        }
    }

    internal void CollectMaterialEntities(HashSet<long> materials, HashSet<Entity> entities)
    {
        foreach ((Entity entity, ClusterMaterialSequence sequence) in _entities)
            for (int index = 0; index < sequence.Count; index++)
                if (materials.Contains(sequence.MaterialAt(index)))
                {
                    entities.Add(entity);
                    break;
                }
    }
    internal IEnumerable<int> GetMaterialOffsets(long id) =>
        _materialOffsets.TryGetValue(id, out var offsets) ? offsets : [];
    internal bool TryGetMaterial(long id, out Material material)
    {
        bool found = _values.TryGetValue(id, out var value);
        material = value.Resource!;
        return found;
    }
    internal bool TryGetMaterialRevision(long id, out ulong revision, out int propertyCount)
    {
        if (_values.TryGetValue(id, out var value))
        {
            revision = CapturedRevision(id, value.Revision);
            propertyCount = value.PropertyCount;
            return true;
        }
        revision = 0;
        propertyCount = 0;
        return false;
    }

    internal bool TryGetEntry(int offset, out Material material, out string encoding)
    {
        if (_entries.TryGetValue(offset, out var entry))
        {
            material = _values[entry.Material].Resource;
            encoding = entry.Encoding;
            return true;
        }
        material = null!;
        encoding = null!;
        return false;
    }

    internal ClusterMaterialAssignmentSet WithoutChanges()
    {
        var result = new ClusterMaterialAssignmentSet(this);
        result._changes = result._changes.Clear();
        result._changedOffsets = result._changedOffsets.Clear();
        result._changesFromVersion = result._contentVersion;
        result.BlendOcclusionChanged = false;
        return result;
    }

    private ulong CapturedRevision(long id, ulong original) =>
        _denseRevisions is null ? original :
        _sparseRevisions.TryGetValue(id, out ulong revision) ? revision : _denseRevisions[_revisionIndices![id]];

    private bool SharesCapturedValues(ClusterMaterialAssignmentSet other) =>
        ReferenceEquals(_values, other._values) && ReferenceEquals(_denseRevisions, other._denseRevisions) &&
        ReferenceEquals(_sparseRevisions, other._sparseRevisions);

    // Revisions and ordered-render flags are captured values. Later edits never mutate an older published set.
    internal ClusterMaterialAssignmentSet ChangesSince(ClusterMaterialAssignmentSet? previous)
    {
        // The accumulated changes already describe the last accepted set. Other
        // consumers retain the comparison path for their own earlier versions.
        if (previous is not null && previous._contentVersion == _changesFromVersion)
            return this;
        if (previous is not null && ReferenceEquals(_entries, previous._entries) && SharesCapturedValues(previous))
            return _changes.IsEmpty && _changedOffsets.IsEmpty ? this : WithoutChanges();

        var result = new ClusterMaterialAssignmentSet(this);
        var materials = ImmutableHashSet.CreateBuilder<long>();
        var offsets = ImmutableHashSet.CreateBuilder<int>();
        if (previous is null || !SharesCapturedValues(previous))
        {
            foreach (var pair in _values)
                if (previous is null || !previous._values.TryGetValue(pair.Key, out var before) ||
                    !ReferenceEquals(pair.Value.Resource, before.Resource) ||
                    pair.Value.PropertyCount != before.PropertyCount || pair.Value.Ordered != before.Ordered ||
                    CapturedRevision(pair.Key, pair.Value.Revision) != previous.CapturedRevision(pair.Key, before.Revision))
                    materials.Add(pair.Key);
            if (previous is not null)
                foreach (long id in previous._values.Keys)
                    if (!_values.ContainsKey(id)) materials.Add(id);
        }
        if (previous is null || !ReferenceEquals(_entries, previous._entries))
        {
            foreach (var pair in _entries)
                if (previous is null || !previous._entries.TryGetValue(pair.Key, out var before) || pair.Value != before)
                    offsets.Add(pair.Key);
            if (previous is not null)
                foreach (int offset in previous._entries.Keys)
                    if (!_entries.ContainsKey(offset)) offsets.Add(offset);
        }
        result._changes = materials.ToImmutable();
        result._changedOffsets = offsets.ToImmutable();
        if (previous is not null) result._changesFromVersion = previous._contentVersion;
        return result;
    }

    internal void Resolve(ReadOnlySpan<Entity> entities, Span<uint> destination)
    {
        if (destination.Length != entities.Length)
            throw new ArgumentException("The material destination must match the entity count.", nameof(destination));
        for (int row = 0; row < entities.Length; row++)
        {
            if (!_entities.TryGetValue(entities[row], out var sequence))
                throw new InvalidOperationException($"Render entity {entities[row]} has no material assignment.");
            destination[row] = _sequences[sequence].Offset;
        }
    }

    internal void ResolveBlendOcclusion(ReadOnlySpan<Entity> entities, Span<uint> destination)
    {
        for (int row = 0; row < entities.Length; row++)
        {
            ClusterMaterialSequence sequence = _entities[entities[row]];
            uint keep = 0;
            for (int index = 0; index < sequence.Count; index++)
                if (_values[sequence.MaterialAt(index)].Ordered) { keep = 1; break; }
            destination[row] = keep;
        }
    }

    internal sealed class Update
    {
        private readonly ClusterMaterialAssignmentSet _source;
        private readonly ClusterMaterialAssignmentSet _result;
        private readonly RenderWorld _world;
        private readonly HashSet<long> _neededMaterials = [];
        private ImmutableDictionary<long, (Material Resource, ulong Revision, int PropertyCount, bool Ordered)>.Builder? _values;
        private ImmutableHashSet<long>.Builder? _changes;
        private ImmutableDictionary<long, ulong>.Builder? _sparseRevisions;
        private ulong[]? _denseRevisions;
        private bool _allowDenseRevisions;
        private int _revisionChanges;
        private bool _topologyChanged;
        private bool _xrayChanged;
        private bool _valuesChanged;
        internal Update(ClusterMaterialAssignmentSet source, RenderWorld world, bool denseRevisionSnapshots = false)
        {
            _source = source;
            _result = new(source);
            _world = world;
            _allowDenseRevisions = denseRevisionSnapshots;
            if (!denseRevisionSnapshots) MaterializeValues();
        }

        internal void Set(Entity entity, ReadOnlySpan<RenderMaterialBinding> bindings, string encoding)
        {
            if (bindings.IsEmpty) throw new InvalidOperationException("A Cluster mesh instance has no material binding.");
            if (_result._entities.TryGetValue(entity, out var previous) && previous.Matches(bindings, encoding))
                return;
            Remove(entity);
            var sequence = new ClusterMaterialSequence(bindings, encoding);
            if (_result._sequences.TryGetKey(sequence, out var shared))
            {
                sequence = shared;
                var allocation = _result._sequences[sequence];
                _result._sequences = _result._sequences.SetItem(sequence, (allocation.Offset, allocation.References + 1));
            }
            else
            {
                uint offset;
                if (_result._free.TryGetValue(bindings.Length, out var free) && !free.IsEmpty)
                {
                    offset = free.Peek();
                    _result._free = _result._free.SetItem(bindings.Length, free.Pop());
                }
                else
                {
                    offset = _result._capacity;
                    _result._capacity = checked(offset + (uint)bindings.Length);
                }
                _result._sequences = _result._sequences.Add(sequence, (offset, 1));
                for (int index = 0; index < bindings.Length; index++)
                {
                    int address = checked((int)offset + index);
                    long id = bindings[index].MaterialId;
                    _result._entries = _result._entries.Add(address, (id, encoding));
                    ImmutableHashSet<int> addresses = _result._materialOffsets.TryGetValue(id, out var existing)
                        ? existing : ImmutableHashSet<int>.Empty;
                    _result._materialOffsets = _result._materialOffsets.SetItem(id, addresses.Add(address));
                    _result._changedOffsets = _result._changedOffsets.Add(address);
                }
                _topologyChanged = true;
            }
            _result._entities = _result._entities.Add(entity, sequence);
            foreach (RenderMaterialBinding binding in bindings)
                if (!ContainsMaterial(binding.MaterialId))
                    _neededMaterials.Add(binding.MaterialId);
        }

        internal void SetValue(Material material)
        {
            long id = material.MaterialId;
            if (!_result._materialOffsets.ContainsKey(id)) return;
            bool found = _values is null
                ? _result._values.TryGetValue(id, out var value)
                : _values.TryGetValue(id, out value);
            ulong revision = material.Revision;
            int propertyCount = material.PropertyCount;
            bool ordered = material.State.Ordered;
            bool sameResource = found && ReferenceEquals(value.Resource, material);
            if (sameResource && ReadRevision(id, value.Revision) == revision &&
                value.PropertyCount == propertyCount && value.Ordered == ordered) return;
            _valuesChanged = true;
            if (found && value.Ordered != ordered) _result.BlendOcclusionChanged = true;
            (_changes ??= _result._changes.ToBuilder()).Add(id);

            if (_allowDenseRevisions && _result._revisionIndices is { } indices &&
                indices.TryGetValue(id, out int index) && sameResource && value.PropertyCount == propertyCount)
            {
                // Ordered changes still need a new captured flag when the revision is
                // written through the dense fast path. Older published flags stay unchanged.
                if (value.Ordered != ordered)
                    (_values ??= _result._values.ToBuilder())[id] = (material, revision, propertyCount, ordered);
                if (_denseRevisions is not null) _denseRevisions[index] = revision;
                else
                {
                    (_sparseRevisions ??= _result._sparseRevisions.ToBuilder())[id] = revision;
                    RecordRevisionChange();
                }
                return;
            }

            if (!sameResource || value.PropertyCount != propertyCount)
            {
                // Discovery or a changed concrete shape invalidates the shared dense index.
                MaterializeValues();
                _allowDenseRevisions = false;
            }
            (_values ??= _result._values.ToBuilder())[id] = (material, revision, propertyCount, ordered);
            if (_allowDenseRevisions) RecordRevisionChange();
        }

        private void RecordRevisionChange()
        {
            if (++_revisionChanges >= Math.Max(16, _result._values.Count / 4) &&
                _result._values.Count >= 64 && !_topologyChanged && _denseRevisions is null)
                PromoteDenseRevisions();
        }

        private ulong ReadDenseRevision(long id, int index)
        {
            if (_denseRevisions is not null) return _denseRevisions[index];
            if (_sparseRevisions is not null && _sparseRevisions.TryGetValue(id, out ulong revision)) return revision;
            return _result._sparseRevisions.TryGetValue(id, out revision) ? revision : _result._denseRevisions![index];
        }

        private ulong ReadRevision(long id, ulong original) =>
            _result._revisionIndices is { } indices && indices.TryGetValue(id, out int index)
                ? ReadDenseRevision(id, index) : original;

        private void PromoteDenseRevisions()
        {
            // Preserve metadata edits before releasing the builder. A dense revision array
            // cannot replace captured Ordered or PropertyCount.
            if (_values is not null)
            {
                _result._values = _values.ToImmutable();
                _values = null;
            }
            ulong[] revisions;
            if (_result._revisionIndices is { } indices)
            {
                revisions = (ulong[])_result._denseRevisions!.Clone();
                if (_sparseRevisions is not null)
                {
                    foreach (var pair in _sparseRevisions) revisions[indices[pair.Key]] = pair.Value;
                }
                else
                {
                    foreach (var pair in _result._sparseRevisions) revisions[indices[pair.Key]] = pair.Value;
                }
            }
            else
            {
                indices = new(_result._values.Count);
                revisions = new ulong[_result._values.Count];
                int index = 0;
                foreach (var pair in _result._values)
                {
                    indices.Add(pair.Key, index);
                    revisions[index++] = pair.Value.Revision;
                }
                _result._revisionIndices = indices;
            }
            _result._denseRevisions = _denseRevisions = revisions;
            _result._sparseRevisions = ImmutableDictionary<long, ulong>.Empty;
            _sparseRevisions = null;
        }

        private void MaterializeValues()
        {
            if (_result._denseRevisions is null) return;
            var values = _values ??= _result._values.ToBuilder();
            foreach (long id in _result._values.Keys)
            {
                var value = values[id];
                ulong revision = ReadRevision(id, value.Revision);
                if (revision != value.Revision)
                    values[id] = (value.Resource, revision, value.PropertyCount, value.Ordered);
            }
            _result._revisionIndices = null;
            _result._denseRevisions = null;
            _result._sparseRevisions = ImmutableDictionary<long, ulong>.Empty;
            _denseRevisions = null;
            _sparseRevisions = null;
        }

        internal void SetXray(Entity entity, bool enabled)
        {
            if (_result._xrayEntities.Contains(entity) == enabled) return;
            _result._xrayEntities = enabled ? _result._xrayEntities.Add(entity) : _result._xrayEntities.Remove(entity);
            _xrayChanged = true;
        }

        internal void Remove(Entity entity)
        {
            SetXray(entity, false);
            if (!_result._entities.TryGetValue(entity, out var sequence)) return;
            _result._entities = _result._entities.Remove(entity);
            var allocation = _result._sequences[sequence];
            if (allocation.References > 1)
            {
                _result._sequences = _result._sequences.SetItem(sequence, (allocation.Offset, allocation.References - 1));
                return;
            }
            _result._sequences = _result._sequences.Remove(sequence);
            ImmutableStack<uint> free = _result._free.TryGetValue(sequence.Count, out var ranges)
                ? ranges : ImmutableStack<uint>.Empty;
            _result._free = _result._free.SetItem(sequence.Count, free.Push(allocation.Offset));
            for (int index = 0; index < sequence.Count; index++)
            {
                int address = checked((int)allocation.Offset + index);
                long id = _result._entries[address].Material;
                _result._entries = _result._entries.Remove(address);
                _result._changedOffsets = _result._changedOffsets.Add(address);
                var addresses = _result._materialOffsets[id].Remove(address);
                if (addresses.IsEmpty)
                {
                    _result._materialOffsets = _result._materialOffsets.Remove(id);
                    MaterializeValues();
                    _allowDenseRevisions = false;
                    (_values ??= _result._values.ToBuilder()).Remove(id);
                    (_changes ??= _result._changes.ToBuilder()).Add(id);
                }
                else _result._materialOffsets = _result._materialOffsets.SetItem(id, addresses);
            }
            _topologyChanged = true;
        }

        private bool ContainsMaterial(long id) =>
            _values?.ContainsKey(id) ?? _result._values.ContainsKey(id);

        internal ClusterMaterialAssignmentSet Complete()
        {
            foreach (long id in _neededMaterials)
                if (_result._materialOffsets.ContainsKey(id) && !ContainsMaterial(id))
                    SetValue(_world.ReadMaterial(id));
            if (_values is not null) _result._values = _values.ToImmutable();
            if (_changes is not null) _result._changes = _changes.ToImmutable();
            if (_sparseRevisions is not null) _result._sparseRevisions = _sparseRevisions.ToImmutable();
            if (_xrayChanged || _topologyChanged)
            {
                var materials = ImmutableHashSet.CreateBuilder<long>();
                foreach (Entity entity in _result._xrayEntities)
                    if (_result._entities.TryGetValue(entity, out var sequence))
                        for (int index = 0; index < sequence.Count; index++) materials.Add(sequence.MaterialAt(index));
                if (!_result._xrayMaterials.SetEquals(materials))
                {
                    _result._xrayMaterials = materials.ToImmutable();
                    _topologyChanged = true;
                }
            }
            if (_topologyChanged) _result.TopologyVersion = checked(_source.TopologyVersion + 1);
            if (_topologyChanged || _xrayChanged || _valuesChanged)
                _result._contentVersion = checked(_source._contentVersion + 1);
            if (_allowDenseRevisions && _result._revisionIndices is null && _result._values.Count >= 64)
                PromoteDenseRevisions();
            return _result;
        }
    }

}

internal sealed class ClusterMaterialSequence : IEquatable<ClusterMaterialSequence>
{
    private readonly long[] _materials;
    private readonly string _encoding;
    private readonly int _hash;
    internal ClusterMaterialSequence(ReadOnlySpan<RenderMaterialBinding> bindings, string encoding)
    {
        _encoding = encoding;
        _materials = new long[bindings.Length];
        var hash = new HashCode();
        hash.Add(encoding, StringComparer.Ordinal);
        for (int index = 0; index < bindings.Length; index++)
        {
            _materials[index] = bindings[index].MaterialId;
            hash.Add(_materials[index]);
        }
        _hash = hash.ToHashCode();
    }
    internal int Count => _materials.Length;
    internal long MaterialAt(int index) => _materials[index];
    internal bool Matches(ReadOnlySpan<RenderMaterialBinding> bindings, string encoding)
    {
        if (_encoding != encoding || bindings.Length != _materials.Length) return false;
        for (int index = 0; index < bindings.Length; index++)
            if (bindings[index].MaterialId != _materials[index]) return false;
        return true;
    }
    public bool Equals(ClusterMaterialSequence? other) => ReferenceEquals(this, other) ||
        other is not null && _hash == other._hash && _encoding == other._encoding && _materials.AsSpan().SequenceEqual(other._materials);
    public override bool Equals(object? other) => other is ClusterMaterialSequence sequence && Equals(sequence);
    public override int GetHashCode() => _hash;
}

/// <summary>Applies changed render-world rows before instance producers consume their offsets.</summary>
public sealed class ClusterMaterialAssignmentSystem : ISystem<RenderPrepareSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace =
        new("render", "Prepare.ClusterMaterials");
    private static readonly SomeEngine.Diagnostics.TraceSite AssignmentQueryTrace =
        new("materials", "AssignmentQuery", detail: false);
    private static readonly SomeEngine.Diagnostics.TraceSite DenseRevisionSnapshotsTrace =
        new("materials", "DenseRevisionSnapshots", detail: false);
    private static readonly SomeEngine.Diagnostics.TraceSite MembershipEntitiesTrace =
        new("materials", "MembershipEntities", detail: false);
    private readonly ClusterMaterialAssignments _assignments;
    private readonly bool _denseRevisionSnapshots;
    private QueryHandle _query;
    private QueryHandle _materialQuery;
    private EntityTableChanges? _tableChanges;
    private bool _created;
    public ClusterMaterialAssignmentSystem(ClusterMaterialAssignments assignments)
        : this(assignments, Environment.GetEnvironmentVariable("SOMEENGINE_DENSE_MATERIAL_SNAPSHOTS") != "0") { }

    public ClusterMaterialAssignmentSystem(ClusterMaterialAssignments assignments, bool denseRevisionSnapshots)
    {
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        _denseRevisionSnapshots = denseRevisionSnapshots;
    }
    public static QueryDefinition EntityQuery() => new QueryDefinitionBuilder()
        .Read<RenderInstance>().Read<RenderTransform>().Read<RenderPreviousTransform>()
        .Read<RenderClusterGeometry>().Optional<RenderMesh>(QueryAccess.Read).ReadBuffer<RenderMaterialBinding>().Optional<RenderXray>(QueryAccess.Read).Build();
    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        if (_created) throw new InvalidOperationException("The material assignment system is already created.");
        _query = context.World.Query(EntityQuery());
        try
        {
            _materialQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderMaterialReference>());
            _tableChanges = context.World.ObserveEntityTables();
        }
        catch
        {
            if (_materialQuery.IsValid) context.World.ReleaseQuery(_materialQuery);
            context.World.ReleaseQuery(_query);
            throw;
        }
        _created = true;
    }
    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        if (!_created) throw new InvalidOperationException("The material assignment system was not created.");
        EntityTableChangeBatch membership = _tableChanges!.Read();
        SomeEngine.Diagnostics.EngineTrace.Counter(MembershipEntitiesTrace, membership.Count);
        var update = new AssignmentChanges(_assignments.Current, context.World, _denseRevisionSnapshots);
        foreach (Entity entity in membership.Entities) update.CollectMembership(context.World, entity);
        using (SomeEngine.Diagnostics.EngineTrace.Begin(AssignmentQueryTrace))
        {
            context.World.ExecuteQuery(_query, context.LastSystemVersion, ref update,
                static (QueryCursor query, ref AssignmentChanges changes) => changes.Collect(query));
        }
        context.World.ExecuteQuery(_materialQuery, context.LastSystemVersion, ref update,
            static (QueryCursor query, ref AssignmentChanges changes) => changes.CollectMaterials(query));
        SomeEngine.Diagnostics.EngineTrace.Counter(DenseRevisionSnapshotsTrace, _denseRevisionSnapshots ? 1 : 0);
        _assignments.Publish(update.Complete());
        _tableChanges.Accept(membership);
    }
    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created) return;
        context.World.ReleaseQuery(_query);
        context.World.ReleaseQuery(_materialQuery);
        _query = default;
        _materialQuery = default;
        _tableChanges!.Dispose();
        _tableChanges = null;
        _created = false;
    }
    private sealed class AssignmentChanges(ClusterMaterialAssignmentSet source, RenderWorld world, bool denseRevisionSnapshots)
    {
        private readonly ClusterMaterialAssignmentSet.Update _update = new(source, world, denseRevisionSnapshots);
        private readonly HashSet<Entity> _membership = [];

        internal void CollectMembership(RenderWorld world, Entity entity)
        {
            _membership.Add(entity);
            if (!world.IsAlive(entity) || !world.Has<RenderInstance>(entity) ||
                !world.Has<RenderTransform>(entity) || !world.Has<RenderPreviousTransform>(entity) ||
                !world.Has<RenderClusterGeometry>(entity) || !world.HasBuffer<RenderMaterialBinding>(entity))
            {
                _update.Remove(entity);
                return;
            }
            string encoding = world.Read<RenderClusterGeometry>(entity).Geometry.VertexEncoding;
            var state = new MaterialBindings(_update, entity, encoding);
            world.ExecuteBufferRead<RenderMaterialBinding, MaterialBindings>(
                entity, ref state, static (BufferView<RenderMaterialBinding> bindings, ref MaterialBindings state) =>
                    state.Update.Set(state.Entity, bindings.AsSpan(), state.Encoding));
            _update.SetXray(entity, world.Has<RenderXray>(entity));
        }

        private readonly record struct MaterialBindings(
            ClusterMaterialAssignmentSet.Update Update, Entity Entity, string Encoding);
        internal void CollectMaterials(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                if (!chunk.HasChangedSinceLastSystemVersion<RenderMaterialReference>()) continue;
                QueryRowChanges changes = chunk.ReadRowChanges<RenderMaterialReference>();
                ReadOnlySpan<RenderMaterialReference> values = chunk.Read<RenderMaterialReference>();
                for (int row = 0; row < values.Length; row++)
                    if (changes.HasChanged(row)) _update.SetValue(values[row].Material);
            }
        }
        internal void Collect(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                bool bindingsChanged = chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>();
                bool meshesChanged = chunk.HasChangedSinceLastSystemVersion<RenderClusterGeometry>();
                bool xrayChanged = chunk.TryRead<RenderXray>(out _) && chunk.HasChangedSinceLastSystemVersion<RenderXray>();
                if (!bindingsChanged && !meshesChanged && !xrayChanged) continue;
                ReadOnlySpan<RenderClusterGeometry> meshes = chunk.Read<RenderClusterGeometry>();
                for (int row = 0; row < chunk.Entities.Length; row++)
                {
                    Entity entity = chunk.Entities[row];
                    if (_membership.Contains(entity)) continue;
                    if (
                        !(bindingsChanged && chunk.RowBufferChangedSinceLastSystemVersion<RenderMaterialBinding>(row)) &&
                        !(meshesChanged && chunk.RowChangedSinceLastSystemVersion<RenderClusterGeometry>(row)) &&
                        !(xrayChanged && chunk.RowChangedSinceLastSystemVersion<RenderXray>(row))) continue;
                    string encoding = meshes[row].Geometry.VertexEncoding;
                    _update.Set(entity, chunk.ReadBuffer<RenderMaterialBinding>(row).AsSpan(), encoding);
                    _update.SetXray(entity, chunk.TryRead<RenderXray>(out _));
                }
            }
        }
        internal ClusterMaterialAssignmentSet Complete()
        {
            return _update.Complete();
        }
    }
}

/// <summary>Writes the direct material-sequence offset assigned to each render instance.</summary>
public readonly struct ClusterMaterialOffsetProducer : IRenderInstanceProducer
{
    private readonly ClusterMaterialAssignments _assignments;
    private readonly ResolvedRenderInstanceProperty<uint> _slot;
    private readonly ResolvedRenderInstanceProperty<uint> _blendOcclusion;

    internal ClusterMaterialOffsetProducer(ClusterMaterialAssignments assignments)
    {
        _assignments = assignments;
        Properties = ClusterRenderFeature.MaterialSlotLayout;
        _slot = Properties.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey);
        _blendOcclusion = Properties.Resolve<uint>(ClusterRenderFeature.BlendOcclusionKey);
    }

    public RenderInstancePropertyLayout Properties { get; }

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        packet.BufferChangedSince<RenderMaterialBinding>(lastSystemVersion) ||
        packet.ChangedSince<RenderClusterGeometry>(lastSystemVersion) ||
        _assignments.Current.BlendOcclusionChanged
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination)
    {
        destination.BindPerInstance(_slot);
        destination.BindPerInstance(_blendOcclusion);
    }

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        ClusterMaterialAssignmentSet assignments = _assignments.Current;
        uint[] storage = ArrayPool<uint>.Shared.Rent(packet.Entities.Length);
        try
        {
            Span<uint> values = storage.AsSpan(0, packet.Entities.Length);
            assignments.Resolve(packet.Entities, values);
            destination.Write(_slot, values);
            assignments.ResolveBlendOcclusion(packet.Entities, values);
            destination.Write(_blendOcclusion, values);
        }
        finally { ArrayPool<uint>.Shared.Return(storage); }
    }

    public void WriteChanged(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet, uint lastSystemVersion)
    {
        ClusterMaterialAssignmentSet assignments = _assignments.Current;
        if (assignments.BlendOcclusionChanged)
        {
            Write(destination, packet);
            return;
        }

        Span<uint> value = stackalloc uint[1];
        for (int row = 0; row < packet.Count; row++)
        {
            ReadOnlyQueryPacket changed = packet.Slice(row, 1);
            if (!changed.BufferChangedSince<RenderMaterialBinding>(lastSystemVersion) &&
                !changed.ChangedSince<RenderClusterGeometry>(lastSystemVersion))
                continue;
            assignments.Resolve(changed.Entities, value);
            destination.Write(_slot, row, value[0]);
            assignments.ResolveBlendOcclusion(changed.Entities, value);
            destination.Write(_blendOcclusion, row, value[0]);
        }
    }
}
