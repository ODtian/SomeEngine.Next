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
using SomeEngine.Job;
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
    private ImmutableDictionary<long, (Material Resource, ulong Revision, bool Ordered)> _values =
        ImmutableDictionary<long, (Material Resource, ulong Revision, bool Ordered)>.Empty;
    private ImmutableDictionary<long, ImmutableHashSet<int>> _materialOffsets = ImmutableDictionary<long, ImmutableHashSet<int>>.Empty;
    private ImmutableHashSet<long> _changes = ImmutableHashSet<long>.Empty;
    private ImmutableHashSet<int> _changedOffsets = ImmutableHashSet<int>.Empty;
    private ImmutableHashSet<Entity> _xrayEntities = ImmutableHashSet<Entity>.Empty;
    private ImmutableHashSet<long> _xrayMaterials = ImmutableHashSet<long>.Empty;
    private uint _capacity;

    private ClusterMaterialAssignmentSet() { }
    private ClusterMaterialAssignmentSet(ClusterMaterialAssignmentSet source)
    {
        _entities = source._entities;
        _sequences = source._sequences;
        _free = source._free;
        _entries = source._entries;
        _values = source._values;
        _materialOffsets = source._materialOffsets;
        _changes = source._changes;
        _changedOffsets = source._changedOffsets;
        _capacity = source._capacity;
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
    internal IEnumerable<long> ChangedMaterialIds => _changes;
    internal IEnumerable<Material> ChangedMaterials => _changes.Where(_values.ContainsKey).Select(id => _values[id].Resource);
    internal IEnumerable<int> ActiveOffsets => _entries.Keys;
    internal IEnumerable<(int Offset, int Count)> MaterialRanges =>
        _sequences.Select(pair => (checked((int)pair.Value.Offset), pair.Key.Count));
    internal IEnumerable<int> ChangedOffsets => _changedOffsets;
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
        result.BlendOcclusionChanged = false;
        return result;
    }

    // Published sets share persistent dictionaries. Revisions are captured values, so a
    // later edit to the same Material still changes _values without mutating older sets.
    internal ClusterMaterialAssignmentSet ChangesSince(ClusterMaterialAssignmentSet? previous)
    {
        if (previous is not null && ReferenceEquals(_entries, previous._entries) &&
            ReferenceEquals(_values, previous._values))
            return _changes.IsEmpty && _changedOffsets.IsEmpty ? this : WithoutChanges();

        var result = new ClusterMaterialAssignmentSet(this);
        var materials = ImmutableHashSet.CreateBuilder<long>();
        var offsets = ImmutableHashSet.CreateBuilder<int>();
        if (previous is null || !ReferenceEquals(_values, previous._values))
        {
            foreach (var pair in _values)
                if (previous is null || !previous._values.TryGetValue(pair.Key, out var before) ||
                    !ReferenceEquals(pair.Value.Resource, before.Resource) || pair.Value.Revision != before.Revision)
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
        return result;
    }

    internal bool Matches(Entity entity, ReadOnlySpan<RenderMaterialBinding> bindings, string encoding) =>
        _entities.TryGetValue(entity, out var sequence) && sequence.Matches(bindings, encoding);

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
        private ImmutableDictionary<long, (Material Resource, ulong Revision, bool Ordered)>.Builder? _values;
        private ImmutableHashSet<long>.Builder? _changes;
        private bool _topologyChanged;
        private bool _xrayChanged;
        private readonly List<MaterialSequenceAllocation> _newSequences = [];
        private int _entryCount;
        internal Update(ClusterMaterialAssignmentSet source, RenderWorld world)
        {
            _source = source;
            _result = new(source);
            _world = world;
        }

        internal void Set(Entity entity, ClusterMaterialSequence sequence)
        {
            if (sequence.Count == 0) throw new InvalidOperationException("A Cluster mesh instance has no material binding.");
            Remove(entity);
            if (_result._sequences.TryGetValue(sequence, out var allocation))
                _result._sequences = _result._sequences.SetItem(sequence, (allocation.Offset, allocation.References + 1));
            else
            {
                uint offset;
                if (_result._free.TryGetValue(sequence.Count, out var free) && !free.IsEmpty)
                {
                    offset = free.Peek();
                    _result._free = _result._free.SetItem(sequence.Count, free.Pop());
                }
                else
                {
                    offset = _result._capacity;
                    _result._capacity = checked(offset + (uint)sequence.Count);
                }
                _result._sequences = _result._sequences.Add(sequence, (offset, 1));
                _newSequences.Add(new(sequence, offset, _entryCount));
                _entryCount += sequence.Count;
                _topologyChanged = true;
            }
            _result._entities = _result._entities.Add(entity, sequence);
            for (int index = 0; index < sequence.Count; index++)
                if (!ContainsMaterial(sequence.MaterialAt(index))) _neededMaterials.Add(sequence.MaterialAt(index));
        }

        private readonly record struct MaterialSequenceAllocation(ClusterMaterialSequence Sequence, uint Offset, int FirstEntry);
        private readonly record struct MaterialEntry(int Address, long Material, string Encoding);

        private readonly struct WriteEntriesJob(List<MaterialSequenceAllocation> sequences, MaterialEntry[] entries) : IJobParallelFor
        {
            public void Execute(int index)
            {
                MaterialSequenceAllocation allocation = sequences[index];
                ClusterMaterialSequence sequence = allocation.Sequence;
                for (int entry = 0; entry < sequence.Count; entry++)
                    entries[allocation.FirstEntry + entry] = new(checked((int)allocation.Offset + entry),
                        sequence.MaterialAt(entry), sequence.Encoding);
            }
        }

        private void FillNewEntries()
        {
            if (_entryCount == 0) return;
            var entries = new MaterialEntry[_entryCount];
            Scheduler scheduler = WorldJobs.TryGetScheduler(_world, out var selected) ? selected : JobSystem.Default;
            scheduler.ExecuteParallel(new WriteEntriesJob(_newSequences, entries), _newSequences.Count);
            foreach (MaterialEntry entry in entries)
            {
                _result._entries = _result._entries.Add(entry.Address, (entry.Material, entry.Encoding));
                ImmutableHashSet<int> addresses = _result._materialOffsets.TryGetValue(entry.Material, out var existing)
                    ? existing : ImmutableHashSet<int>.Empty;
                _result._materialOffsets = _result._materialOffsets.SetItem(entry.Material, addresses.Add(entry.Address));
                _result._changedOffsets = _result._changedOffsets.Add(entry.Address);
            }
        }

        internal void SetValue(Material material)
        {
            if (!_result._materialOffsets.ContainsKey(material.MaterialId)) return;
            var values = _values;
            bool found = values is null
                ? _result._values.TryGetValue(material.MaterialId, out var value)
                : values.TryGetValue(material.MaterialId, out value);
            if (found && ReferenceEquals(value.Resource, material) && value.Revision == material.Revision) return;
            if (found && value.Ordered != material.State.Ordered) _result.BlendOcclusionChanged = true;
            (_values ??= _result._values.ToBuilder())[material.MaterialId] = (material, material.Revision, material.State.Ordered);
            (_changes ??= _result._changes.ToBuilder()).Add(material.MaterialId);
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
            FillNewEntries();
            foreach (long id in _neededMaterials)
                if (_result._materialOffsets.ContainsKey(id) && !ContainsMaterial(id))
                    SetValue(_world.ReadMaterial(id));
            if (_values is not null) _result._values = _values.ToImmutable();
            if (_changes is not null) _result._changes = _changes.ToImmutable();
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
    internal string Encoding => _encoding;
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
    private readonly ClusterMaterialAssignments _assignments;
    private QueryHandle _query;
    private QueryHandle _materialQuery;
    private EntityTableChanges? _tableChanges;
    private bool _created;
    private readonly List<AssignmentRange> _ranges = [];
    public ClusterMaterialAssignmentSystem(ClusterMaterialAssignments assignments) =>
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
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
        var update = new AssignmentChanges(_assignments.Current, context.World, _ranges);
        foreach (Entity entity in membership.Entities) update.CollectMembership(context.World, entity);
        context.World.ExecuteQuery(_query, context.LastSystemVersion, ref update,
            static (QueryCursor query, ref AssignmentChanges changes) => changes.Collect(query));
        context.World.ExecuteQuery(_materialQuery, context.LastSystemVersion, ref update,
            static (QueryCursor query, ref AssignmentChanges changes) => changes.CollectMaterials(query));
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
    private sealed class AssignmentRange
    {
        internal readonly List<MaterialSequenceGroup> Groups = [];
        internal readonly List<(Entity Entity, bool Enabled)> Xray = [];
        private readonly Dictionary<ClusterMaterialSequence, MaterialSequenceGroup> _groups = [];

        internal void Reset() { Groups.Clear(); Xray.Clear(); _groups.Clear(); }

        internal void Collect(ReadOnlyQueryPacket packet, uint version, ClusterMaterialAssignmentSet source, HashSet<Entity> membership)
        {
            bool bindingsChanged = packet.BufferChangedSince<RenderMaterialBinding>(version);
            bool meshesChanged = packet.ChangedSince<RenderClusterGeometry>(version);
            bool xray = packet.Has<RenderXray>();
            bool xrayChanged = xray && packet.ChangedSince<RenderXray>(version);
            if (!bindingsChanged && !meshesChanged && !xrayChanged && membership.Count == 0) return;
            ReadOnlySpan<RenderClusterGeometry> meshes = packet.Read<RenderClusterGeometry>();
            for (int row = 0; row < packet.Count; row++)
            {
                Entity entity = packet.Entities[row];
                if (!membership.Contains(entity)
                    && !(bindingsChanged && packet.RowBufferChangedSince<RenderMaterialBinding>(row, version))
                    && !(meshesChanged && packet.RowChangedSince<RenderClusterGeometry>(row, version))
                    && !(xrayChanged && packet.RowChangedSince<RenderXray>(row, version))) continue;
                var bindings = packet.ReadBuffer<RenderMaterialBinding>(row).AsSpan();
                string encoding = meshes[row].Geometry.VertexEncoding;
                if (!source.Matches(entity, bindings, encoding))
                {
                    var sequence = new ClusterMaterialSequence(bindings, encoding);
                    if (!_groups.TryGetValue(sequence, out var group))
                    {
                        group = new(sequence);
                        _groups.Add(sequence, group);
                        Groups.Add(group);
                    }
                    group.Entities.Add(entity);
                }
                Xray.Add((entity, xray));
            }
        }
    }

    private sealed class MaterialSequenceGroup(ClusterMaterialSequence sequence)
    {
        internal readonly ClusterMaterialSequence Sequence = sequence;
        internal readonly List<Entity> Entities = [];
    }

    private sealed class AssignmentChanges(ClusterMaterialAssignmentSet source, RenderWorld world, List<AssignmentRange> ranges)
    {
        private readonly ClusterMaterialAssignmentSet.Update _update = new(source, world);
        private readonly HashSet<Entity> _membership = [];

        internal void CollectMembership(RenderWorld world, Entity entity)
        {
            _membership.Add(entity);
            if (!world.IsAlive(entity) || !world.Has<RenderInstance>(entity) ||
                !world.Has<RenderTransform>(entity) || !world.Has<RenderPreviousTransform>(entity) ||
                !world.Has<RenderClusterGeometry>(entity) || !world.HasBuffer<RenderMaterialBinding>(entity))
                _update.Remove(entity);
        }

        internal void CollectMaterials(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
                foreach (RenderMaterialReference value in chunk.Read<RenderMaterialReference>()) _update.SetValue(value.Material);
        }

        private readonly struct CollectRangeJob(AssignmentChanges changes) : IReadOnlyQueryPacketJob
        {
            public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet) =>
                changes.CollectRange(context.PacketIndex, packet, context.LastSystemVersion);
        }

        private void CollectRange(int index, ReadOnlyQueryPacket packet, uint version) =>
            ranges[index].Collect(packet, version, source, _membership);

        internal void Collect(QueryCursor query)
        {
            if (_membership.Count == 0)
            {
                bool changed = false;
                foreach (QueryChunkView chunk in query.Chunks)
                    if (chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>()
                        || chunk.HasChangedSinceLastSystemVersion<RenderClusterGeometry>()
                        || chunk.Has<RenderXray>() && chunk.HasChangedSinceLastSystemVersion<RenderXray>())
                    {
                        changed = true;
                        break;
                    }
                if (!changed) return;
            }
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(query);
            while (ranges.Count < packets.PacketCount) ranges.Add(new());
            try
            {
                packets.ExecuteParallel(new CollectRangeJob(this));
                for (int index = 0; index < packets.PacketCount; index++)
                {
                    AssignmentRange range = ranges[index];
                    foreach (MaterialSequenceGroup group in range.Groups)
                        foreach (Entity entity in group.Entities) _update.Set(entity, group.Sequence);
                    foreach (var value in range.Xray) _update.SetXray(value.Entity, value.Enabled);
                }
            }
            finally
            {
                for (int index = 0; index < packets.PacketCount; index++) ranges[index].Reset();
            }
        }

        internal ClusterMaterialAssignmentSet Complete() => _update.Complete();
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
}
