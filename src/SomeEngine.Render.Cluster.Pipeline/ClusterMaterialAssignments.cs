using System.Buffers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Publishes only the direct local-material assignments extracted from RenderWorld. Execution
/// compatibility belongs to the independent raster and shade stage subsystems.
/// </summary>
public sealed class ClusterMaterialAssignments
{
    private ClusterMaterialAssignmentSet _current = ClusterMaterialAssignmentSet.Empty;
    private ulong _topologyVersion;

    public ClusterMaterialAssignmentSet Current => Volatile.Read(ref _current);

    public ClusterMaterialOffsetProducer CreateProducer() => new(this);

    internal void Publish(ClusterMaterialAssignmentSet next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ClusterMaterialAssignmentSet current = Current;
        if (current.HasSameTopology(next))
        {
            if (current.HasSameEntityMappings(next))
                return;
            next.SetTopologyVersion(current.TopologyVersion);
            Volatile.Write(ref _current, next);
            return;
        }

        next.SetTopologyVersion(checked(++_topologyVersion));
        Volatile.Write(ref _current, next);
    }
}

/// <summary>An immutable publication of mesh-local indices to direct Material references.</summary>
public sealed class ClusterMaterialAssignmentSet
{
    internal static ClusterMaterialAssignmentSet Empty { get; } =
        new([], [], [], [], [], []);

    private readonly ClusterMaterialSequence[] _sequences;
    private readonly Material[] _slots;
    private readonly string[] _vertexEncodings;
    private readonly int[] _entityGenerations;
    private readonly uint[] _entityOffsets;
    private ulong _topologyVersion;

    internal ClusterMaterialAssignmentSet(
        ClusterMaterialSequence[] sequences,
        Material[] slots,
        string[] vertexEncodings,
        Material[] distinctMaterials,
        int[] entityGenerations,
        uint[] entityOffsets)
    {
        if (slots.Length != vertexEncodings.Length)
            throw new ArgumentException("Material slots and vertex encodings must have identical lengths.");
        _sequences = sequences;
        _slots = slots;
        _vertexEncodings = vertexEncodings;
        DistinctMaterials = distinctMaterials;
        _entityGenerations = entityGenerations;
        _entityOffsets = entityOffsets;
    }

    public ulong TopologyVersion => _topologyVersion;

    public ReadOnlyMemory<Material> Slots => _slots;

    internal ReadOnlyMemory<string> VertexEncodings => _vertexEncodings;

    public IReadOnlyList<Material> DistinctMaterials { get; }

    public uint SlotCount => checked((uint)_slots.Length);

    public uint SlotCapacity => Math.Max(1u, SlotCount);

    public uint MaterialCount => checked((uint)DistinctMaterials.Count);

    internal void SetTopologyVersion(ulong version) => _topologyVersion = version;

    internal bool HasSameTopology(ClusterMaterialAssignmentSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_slots.Length != other._slots.Length ||
            _sequences.Length != other._sequences.Length ||
            DistinctMaterials.Count != other.DistinctMaterials.Count)
        {
            return false;
        }

        for (int index = 0; index < _slots.Length; index++)
            if (!ReferenceEquals(_slots[index], other._slots[index]) ||
                !string.Equals(_vertexEncodings[index], other._vertexEncodings[index], StringComparison.Ordinal))
                return false;
        for (int index = 0; index < DistinctMaterials.Count; index++)
            if (!ReferenceEquals(DistinctMaterials[index], other.DistinctMaterials[index]))
                return false;
        for (int index = 0; index < _sequences.Length; index++)
        {
            ClusterMaterialSequence left = _sequences[index];
            ClusterMaterialSequence right = other._sequences[index];
            if (left.Offset != right.Offset || left.Materials.Length != right.Materials.Length ||
                !string.Equals(left.VertexEncoding, right.VertexEncoding, StringComparison.Ordinal))
                return false;
            for (int material = 0; material < left.Materials.Length; material++)
                if (!ReferenceEquals(left.Materials[material], right.Materials[material]))
                    return false;
        }
        return true;
    }

    internal bool HasSameEntityMappings(ClusterMaterialAssignmentSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _entityGenerations.AsSpan().SequenceEqual(other._entityGenerations) &&
            _entityOffsets.AsSpan().SequenceEqual(other._entityOffsets);
    }

    internal void Resolve(ReadOnlySpan<Entity> entities, Span<uint> destination)
    {
        if (destination.Length != entities.Length)
        {
            throw new ArgumentException(
                "The Cluster material destination must match the packet entity count.",
                nameof(destination));
        }

        for (int row = 0; row < entities.Length; row++)
        {
            Entity entity = entities[row];
            if ((uint)entity.Index >= (uint)_entityOffsets.Length ||
                _entityGenerations[entity.Index] != entity.Generation)
            {
                throw new InvalidOperationException(
                    $"Render entity {entity} has no published Cluster material assignment.");
            }
            destination[row] = _entityOffsets[entity.Index];
        }
    }
}

internal readonly record struct ClusterMaterialSequence(
    Material[] Materials,
    string VertexEncoding,
    uint Offset);

/// <summary>Publishes direct material assignments before instance properties consume offsets.</summary>
public sealed class ClusterMaterialAssignmentSystem : ISystem<RenderPrepareSystemContext>
{
    private readonly ClusterMaterialAssignments _assignments;
    private QueryHandle _query;
    private long _topologyRevision = -1;
    private bool _created;

    public ClusterMaterialAssignmentSystem(ClusterMaterialAssignments assignments)
        => _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));

    public static QueryDefinition EntityQuery() =>
        new QueryDefinitionBuilder()
            .Read<RenderInstance>()
            .Read<RenderTransform>()
            .Read<RenderPreviousTransform>()
            .Read<RenderMesh>()
            .ReadBuffer<RenderMaterialBinding>()
            .Build();

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The Cluster material-assignment system is already created.");
        _query = context.World.Query(EntityQuery());
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            throw new InvalidOperationException("The Cluster material-assignment system was not created.");
        long topologyRevision = context.World.PublishedTopologyRevision;
        if (_topologyRevision == topologyRevision)
        {
            bool changed = false;
            context.World.ExecuteQuery(
                _query,
                context.LastSystemVersion,
                ref changed,
                static (QueryCursor cursor, ref bool state) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        if (!chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>() &&
                            !chunk.HasChangedSinceLastSystemVersion<RenderMesh>())
                            continue;
                        state = true;
                        return;
                    }
                });
            if (!changed)
                return;
        }

        var builder = new AssignmentBuilder();
        context.World.ExecuteQuery(
            _query,
            ref builder,
            static (QueryCursor cursor, ref AssignmentBuilder state) => state.Collect(cursor));
        _assignments.Publish(builder.Build());
        _topologyRevision = topologyRevision;
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;
        context.World.ReleaseQuery(_query);
        _query = default;
        _topologyRevision = -1;
        _created = false;
    }

    private sealed class AssignmentBuilder
    {
        private readonly List<ClusterMaterialSequence> _sequences = [];
        private readonly List<Material> _slots = [];
        private readonly List<string> _vertexEncodings = [];
        private readonly List<Material> _distinctMaterials = [];
        private readonly HashSet<Material> _distinctSet =
            new(ReferenceEqualityComparer.Instance);
        private int[] _entityGenerations = [];
        private uint[] _entityOffsets = [];

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                ReadOnlySpan<RenderMaterialBinding> bindings =
                    row.ReadBuffer<RenderMaterialBinding>().AsSpan();
                if (bindings.IsEmpty)
                    throw new InvalidOperationException("A Cluster mesh instance has no material binding.");
                Mesh mesh = row.Read<RenderMesh>().Mesh;
                string vertexEncoding = mesh.VertexEncoding ?? throw new InvalidDataException(
                    $"Cluster mesh '{mesh.Name}' has no vertex encoding compatibility.");
                uint offset = ResolveSequence(bindings, vertexEncoding);
                Entity entity = row.Entity;
                EnsureEntityCapacity(checked(entity.Index + 1));
                _entityGenerations[entity.Index] = entity.Generation;
                _entityOffsets[entity.Index] = offset;
            }
        }

        private uint ResolveSequence(
            ReadOnlySpan<RenderMaterialBinding> bindings,
            string vertexEncoding)
        {
            foreach (ClusterMaterialSequence sequence in _sequences)
            {
                if (sequence.Materials.Length != bindings.Length ||
                    !string.Equals(sequence.VertexEncoding, vertexEncoding, StringComparison.Ordinal))
                    continue;
                bool equal = true;
                for (int index = 0; index < bindings.Length; index++)
                {
                    if (ReferenceEquals(sequence.Materials[index], bindings[index].Material))
                        continue;
                    equal = false;
                    break;
                }
                if (equal)
                    return sequence.Offset;
            }

            uint offset = checked((uint)_slots.Count);
            var materials = new Material[bindings.Length];
            for (int index = 0; index < bindings.Length; index++)
            {
                Material material = bindings[index].Material;
                ArgumentNullException.ThrowIfNull(material);
                materials[index] = material;
                _slots.Add(material);
                _vertexEncodings.Add(vertexEncoding);
                if (_distinctSet.Add(material))
                    _distinctMaterials.Add(material);
            }
            _sequences.Add(new ClusterMaterialSequence(materials, vertexEncoding, offset));
            return offset;
        }

        internal ClusterMaterialAssignmentSet Build() => new(
            [.. _sequences],
            [.. _slots],
            [.. _vertexEncodings],
            [.. _distinctMaterials],
            _entityGenerations,
            _entityOffsets);

        private void EnsureEntityCapacity(int required)
        {
            if (_entityOffsets.Length >= required)
                return;
            int capacity = Math.Max(required, Math.Max(16, _entityOffsets.Length * 2));
            Array.Resize(ref _entityGenerations, capacity);
            Array.Resize(ref _entityOffsets, capacity);
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
