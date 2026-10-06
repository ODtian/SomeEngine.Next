using System.Collections;
using System.Collections.Immutable;
using System.Buffers;
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
    private ImmutableDictionary<long, (Material Resource, ulong Revision)> _values = ImmutableDictionary<long, (Material Resource, ulong Revision)>.Empty;
    private ImmutableDictionary<long, ImmutableHashSet<int>> _materialOffsets = ImmutableDictionary<long, ImmutableHashSet<int>>.Empty;
    private ImmutableHashSet<long> _changes = ImmutableHashSet<long>.Empty;
    private ImmutableHashSet<int> _changedOffsets = ImmutableHashSet<int>.Empty;
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
        TopologyVersion = source.TopologyVersion;
    }

    public ulong TopologyVersion { get; private set; }
    public uint SlotCount => checked((uint)_entries.Count);
    public uint SlotCapacity => Math.Max(1u, _capacity);
    public uint MaterialCount => checked((uint)_values.Count);
    public IEnumerable<Material> DistinctMaterials => _values.Values.Select(value => value.Resource);
    internal IEnumerable<long> ChangedMaterialIds => _changes;
    internal IEnumerable<Material> ChangedMaterials => _changes.Where(_values.ContainsKey).Select(id => _values[id].Resource);
    internal IEnumerable<int> ActiveOffsets => _entries.Keys;
    internal IEnumerable<int> ChangedOffsets => _changedOffsets;
    internal IEnumerable<Entity> Entities => _entities.Keys;
    internal void CollectChangedEntities(HashSet<Entity> entities)
    {
        foreach ((Entity entity, ClusterMaterialSequence sequence) in _entities)
        {
            uint offset = _sequences[sequence].Offset;
            for (int index = 0; index < sequence.Count; index++)
                if (_changedOffsets.Contains(checked((int)offset + index)) ||
                    _changes.Contains(sequence.MaterialAt(index)))
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

    internal sealed class Update
    {
        private readonly ClusterMaterialAssignmentSet _source;
        private readonly ClusterMaterialAssignmentSet _result;
        private readonly RenderWorld _world;
        private readonly HashSet<long> _neededMaterials = [];
        private bool _topologyChanged;
        internal Update(ClusterMaterialAssignmentSet source, RenderWorld world)
        {
            _source = source;
            _result = new(source);
            _world = world;
        }

        internal void Set(Entity entity, ReadOnlySpan<RenderMaterialBinding> bindings, string encoding)
        {
            if (bindings.IsEmpty) throw new InvalidOperationException("A Cluster mesh instance has no material binding.");
            if (_result._entities.TryGetValue(entity, out var previous) && previous.Matches(bindings, encoding))
            {
                return;
            }
            Remove(entity);
            var sequence = new ClusterMaterialSequence(bindings, encoding);
            if (_result._sequences.TryGetValue(sequence, out var allocation))
                _result._sequences = _result._sequences.SetItem(sequence, (allocation.Offset, allocation.References + 1));
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
                if (!_result._values.ContainsKey(binding.MaterialId))
                    _neededMaterials.Add(binding.MaterialId);
        }

        internal void SetValue(Material material)
        {
            if (!_result._materialOffsets.ContainsKey(material.MaterialId)) return;
            if (_result._values.TryGetValue(material.MaterialId, out var value) && ReferenceEquals(value.Resource, material) && value.Revision == material.Revision) return;
            _result._values = _result._values.SetItem(material.MaterialId, (material, material.Revision));
            _result._changes = _result._changes.Add(material.MaterialId);
        }

        internal void Remove(Entity entity)
        {
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
                    _result._values = _result._values.Remove(id);
                    _result._changes = _result._changes.Add(id);
                }
                else _result._materialOffsets = _result._materialOffsets.SetItem(id, addresses);
            }
            _topologyChanged = true;
        }

        internal ClusterMaterialAssignmentSet Complete()
        {
            foreach (long id in _neededMaterials)
                if (_result._materialOffsets.ContainsKey(id) && !_result._values.ContainsKey(id))
                    SetValue(_world.ReadMaterial(id));
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
    public ClusterMaterialAssignmentSystem(ClusterMaterialAssignments assignments) =>
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
    public static QueryDefinition EntityQuery() => new QueryDefinitionBuilder()
        .Read<RenderInstance>().Read<RenderTransform>().Read<RenderPreviousTransform>()
        .Read<RenderMesh>().ReadBuffer<RenderMaterialBinding>().Build();
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
        var update = new AssignmentChanges(_assignments.Current, context.World);
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
    private sealed class AssignmentChanges(ClusterMaterialAssignmentSet source, RenderWorld world)
    {
        private readonly ClusterMaterialAssignmentSet.Update _update = new(source, world);
        private readonly HashSet<Entity> _membership = [];

        internal void CollectMembership(RenderWorld world, Entity entity)
        {
            _membership.Add(entity);
            if (!world.IsAlive(entity) || !world.Has<RenderInstance>(entity) ||
                !world.Has<RenderTransform>(entity) || !world.Has<RenderPreviousTransform>(entity) ||
                !world.Has<RenderMesh>(entity) || !world.HasBuffer<RenderMaterialBinding>(entity))
            {
                _update.Remove(entity);
                return;
            }
            RenderMesh mesh = world.Read<RenderMesh>(entity);
            string encoding = mesh.Mesh.VertexEncoding ?? throw new InvalidDataException(
                $"Cluster mesh '{mesh.Mesh.Name}' has no vertex encoding compatibility.");
            var state = new MaterialBindings(_update, entity, encoding);
            world.ExecuteBufferRead<RenderMaterialBinding, MaterialBindings>(
                entity, ref state, static (BufferView<RenderMaterialBinding> bindings, ref MaterialBindings state) =>
                    state.Update.Set(state.Entity, bindings.AsSpan(), state.Encoding));
        }

        private readonly record struct MaterialBindings(
            ClusterMaterialAssignmentSet.Update Update, Entity Entity, string Encoding);
        internal void CollectMaterials(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<RenderMaterialReference> values = chunk.Read<RenderMaterialReference>();
                for (int row = 0; row < values.Length; row++)
                    _update.SetValue(values[row].Material);
            }
        }
        internal void Collect(QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                bool bindingsChanged = chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>();
                bool meshesChanged = chunk.HasChangedSinceLastSystemVersion<RenderMesh>();
                if (!bindingsChanged && !meshesChanged) continue;
                ReadOnlySpan<RenderMesh> meshes = chunk.Read<RenderMesh>();
                for (int row = 0; row < chunk.Entities.Length; row++)
                {
                    Entity entity = chunk.Entities[row];
                    if (_membership.Contains(entity)) continue;
                    if (
                        !(bindingsChanged && chunk.RowBufferChangedSinceLastSystemVersion<RenderMaterialBinding>(row)) &&
                        !(meshesChanged && chunk.RowChangedSinceLastSystemVersion<RenderMesh>(row))) continue;
                    string encoding = meshes[row].Mesh.VertexEncoding ?? throw new InvalidDataException(
                        $"Cluster mesh '{meshes[row].Mesh.Name}' has no vertex encoding compatibility.");
                    _update.Set(entity, chunk.ReadBuffer<RenderMaterialBinding>(row).AsSpan(), encoding);
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

    internal ClusterMaterialOffsetProducer(ClusterMaterialAssignments assignments)
    {
        _assignments = assignments;
        Properties = ClusterRenderFeature.MaterialSlotLayout;
        _slot = Properties.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey);
    }

    public RenderInstancePropertyLayout Properties { get; }

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        packet.BufferChangedSince<RenderMaterialBinding>(lastSystemVersion) ||
        packet.ChangedSince<RenderMesh>(lastSystemVersion)
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination) =>
        destination.BindPerInstance(_slot);

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        ClusterMaterialAssignmentSet assignments = _assignments.Current;
        uint[]? rented = null;
        scoped Span<uint> offsets = packet.Count <= 1_024
            ? stackalloc uint[packet.Count]
            : (rented = ArrayPool<uint>.Shared.Rent(packet.Count)).AsSpan(0, packet.Count);
        try
        {
            assignments.Resolve(packet.Entities, offsets);
            destination.Write(_slot, offsets);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<uint>.Shared.Return(rented);
        }
    }
}
