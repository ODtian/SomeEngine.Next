using System.Buffers;
using SomeEngine.Assets;
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
/// Immutable CPU publication consumed by instance composition and the frame graph. Slot words use
/// the same field-major SOA contract as the Cluster shaders; material identity comes exclusively
/// from extracted <see cref="RenderMaterialBinding"/> buffers.
/// </summary>
public sealed class ClusterMaterialTable
{
    public const uint RasterBinField = 0;
    public const uint DeformBinField = 1;
    public const uint ShadeBinField = 2;
    public const uint FieldCount = 3;

    private ClusterMaterialSnapshot _current = ClusterMaterialSnapshot.Empty;
    private ulong _version;

    public ClusterMaterialSnapshot Current => Volatile.Read(ref _current);

    public ClusterMaterialProducer CreateProducer() => new(this);

    internal void Publish(ClusterMaterialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClusterMaterialSnapshot current = Current;
        if (current.HasSameTopology(snapshot))
        {
            if (current.HasSameEntityMappings(snapshot))
                return;
            snapshot.SetVersion(current.Version);
            Volatile.Write(ref _current, snapshot);
            return;
        }
        snapshot.SetVersion(checked(++_version));
        Volatile.Write(ref _current, snapshot);
    }
}

public sealed class ClusterMaterialSnapshot
{
    internal static ClusterMaterialSnapshot Empty { get; } =
        new([], [], [], 2, [], []);

    private readonly MaterialSequence[] _sequences;
    private readonly int[] _entityGenerations;
    private readonly uint[] _entityOffsets;
    private ulong _version;

    internal ClusterMaterialSnapshot(
        MaterialSequence[] sequences,
        AssetHandle<Material>[] materials,
        uint[] slotWords,
        uint slotCapacity)
        : this(sequences, materials, slotWords, slotCapacity, [], [])
    {
    }

    internal ClusterMaterialSnapshot(
        MaterialSequence[] sequences,
        AssetHandle<Material>[] materials,
        uint[] slotWords,
        uint slotCapacity,
        int[] entityGenerations,
        uint[] entityOffsets)
    {
        _sequences = sequences;
        _entityGenerations = entityGenerations;
        _entityOffsets = entityOffsets;
        Materials = materials;
        SlotWords = slotWords;
        SlotCapacity = slotCapacity;
    }

    public ulong Version => _version;

    public IReadOnlyList<AssetHandle<Material>> Materials { get; }

    public ReadOnlyMemory<uint> SlotWords { get; }

    public uint SlotCapacity { get; }

    public uint MaterialCount => checked((uint)Materials.Count);

    internal void SetVersion(ulong version) => _version = version;

    internal bool HasSameTopology(ClusterMaterialSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (SlotCapacity != other.SlotCapacity ||
            Materials.Count != other.Materials.Count ||
            _sequences.Length != other._sequences.Length ||
            !SlotWords.Span.SequenceEqual(other.SlotWords.Span))
        {
            return false;
        }

        for (int index = 0; index < Materials.Count; index++)
            if (Materials[index] != other.Materials[index])
                return false;

        for (int index = 0; index < _sequences.Length; index++)
        {
            MaterialSequence left = _sequences[index];
            MaterialSequence right = other._sequences[index];
            if (left.Offset != right.Offset ||
                !left.Materials.AsSpan().SequenceEqual(right.Materials))
            {
                return false;
            }
        }
        return true;
    }

    internal bool HasSameEntityMappings(ClusterMaterialSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _entityGenerations.AsSpan().SequenceEqual(other._entityGenerations) &&
            _entityOffsets.AsSpan().SequenceEqual(other._entityOffsets);
    }

    internal void Resolve(
        ReadOnlySpan<Entity> entities,
        Span<uint> destination)
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
                    $"Render entity {entity} has no published Cluster material-slot mapping.");
            }
            destination[row] = _entityOffsets[entity.Index];
        }
    }
}

internal sealed class MaterialSequence(
    AssetHandle<Material>[] materials,
    uint offset)
{
    internal AssetHandle<Material>[] Materials { get; } = materials;

    internal uint Offset { get; } = offset;
}

/// <summary>Publishes material bins before the Cluster instance producer writes slot offsets.</summary>
public sealed class ClusterMaterialSystem : ISystem<RenderPrepareSystemContext>
{
    private readonly ClusterMaterialTable _table;
    private QueryHandle _query;
    private long _topologyRevision = -1;
    private bool _created;

    public ClusterMaterialSystem(ClusterMaterialTable table)
        => _table = table ?? throw new ArgumentNullException(nameof(table));

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
            throw new InvalidOperationException("The Cluster material system is already created.");
        _query = context.World.Query(EntityQuery());
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            throw new InvalidOperationException("The Cluster material system was not created.");
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
                        if (!chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>())
                            continue;
                        state = true;
                        return;
                    }
                });
            if (!changed)
                return;
        }

        var builder = new SnapshotBuilder();
        context.World.ExecuteQuery(
            _query,
            ref builder,
            static (QueryCursor cursor, ref SnapshotBuilder state) => state.Collect(cursor));
        _table.Publish(builder.Build());
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

    private sealed class SnapshotBuilder
    {
        private readonly List<MaterialSequence> _sequences = [];
        private readonly List<AssetHandle<Material>> _materials = [];
        private readonly Dictionary<AssetHandle<Material>, uint> _bins = [];
        private readonly List<uint[]> _sequenceBins = [];
        private int[] _entityGenerations = [];
        private uint[] _entityOffsets = [];
        private int _usedSlots;

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                ReadOnlySpan<RenderMaterialBinding> bindings =
                    row.ReadBuffer<RenderMaterialBinding>().AsSpan();
                if (bindings.IsEmpty)
                    throw new InvalidOperationException("A Cluster mesh instance has no material binding.");
                uint offset = ResolveSequence(bindings);
                Entity entity = row.Entity;
                EnsureEntityCapacity(checked(entity.Index + 1));
                _entityGenerations[entity.Index] = entity.Generation;
                _entityOffsets[entity.Index] = offset;
            }
        }

        private uint ResolveSequence(ReadOnlySpan<RenderMaterialBinding> bindings)
        {
            foreach (MaterialSequence sequence in _sequences)
            {
                if (sequence.Materials.Length != bindings.Length)
                    continue;
                bool equal = true;
                for (int material = 0; material < bindings.Length; material++)
                {
                    if (sequence.Materials[material] == bindings[material].Material)
                        continue;
                    equal = false;
                    break;
                }
                if (equal)
                    return sequence.Offset;
            }

            var handles = new AssetHandle<Material>[bindings.Length];
            var bins = new uint[bindings.Length];
            for (int index = 0; index < bindings.Length; index++)
            {
                AssetHandle<Material> handle = bindings[index].Material;
                if (!handle.IsValid || handle.LoadState != AssetLoadState.Ready)
                {
                    throw new InvalidOperationException(
                        $"Cluster material {handle} is not ready at the render prepare boundary.");
                }
                handles[index] = handle;
                if (!_bins.TryGetValue(handle, out uint bin))
                {
                    bin = checked((uint)_materials.Count);
                    _bins.Add(handle, bin);
                    _materials.Add(handle);
                }
                bins[index] = bin;
            }
            uint offset = checked((uint)_usedSlots);
            _usedSlots = checked(_usedSlots + bindings.Length);
            _sequences.Add(new MaterialSequence(handles, offset));
            _sequenceBins.Add(bins);
            return offset;
        }

        internal ClusterMaterialSnapshot Build()
        {
            uint slotCapacity = checked((uint)Math.Max(2, (_usedSlots + 1) & ~1));
            var words = new uint[checked((int)(slotCapacity * ClusterMaterialTable.FieldCount))];
            int slot = 0;
            foreach (uint[] bins in _sequenceBins)
            {
                foreach (uint bin in bins)
                {
                    words[checked((int)(ClusterMaterialTable.RasterBinField * slotCapacity) + slot)] = bin;
                    words[checked((int)(ClusterMaterialTable.DeformBinField * slotCapacity) + slot)] = bin;
                    words[checked((int)(ClusterMaterialTable.ShadeBinField * slotCapacity) + slot)] = bin;
                    slot++;
                }
            }
            return new ClusterMaterialSnapshot(
                [.. _sequences],
                [.. _materials],
                words,
                slotCapacity,
                _entityGenerations,
                _entityOffsets);
        }

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

/// <summary>Instance-property producer that writes material-sequence offsets from ECS buffers.</summary>
public readonly struct ClusterMaterialProducer : IRenderInstanceProducer
{
    private readonly ClusterMaterialTable _table;
    private readonly ResolvedRenderInstanceProperty<uint> _slot;

    internal ClusterMaterialProducer(ClusterMaterialTable table)
    {
        _table = table;
        Properties = ClusterRenderFeature.MaterialSlotLayout;
        _slot = Properties.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey);
    }

    public RenderInstancePropertyLayout Properties { get; }

    public RenderInstanceChanges GetChanges(
        ReadOnlyQueryPacket packet,
        uint lastSystemVersion) =>
        packet.BufferChangedSince<RenderMaterialBinding>(lastSystemVersion)
            ? RenderInstanceChanges.Values
            : RenderInstanceChanges.None;

    public void Bind(RenderInstanceWriteSlice destination) =>
        destination.BindPerInstance(_slot);

    public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
    {
        ClusterMaterialSnapshot snapshot = _table.Current;
        uint[]? rented = null;
        scoped Span<uint> offsets;
        if (packet.Count <= 1_024)
        {
            offsets = stackalloc uint[packet.Count];
        }
        else
        {
            rented = ArrayPool<uint>.Shared.Rent(packet.Count);
            offsets = rented.AsSpan(0, packet.Count);
        }
        try
        {
            snapshot.Resolve(packet.Entities, offsets);
            destination.Write(_slot, offsets);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<uint>.Shared.Return(rented);
        }
    }
}
