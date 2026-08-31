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
    private ulong _topologyVersion;
    private readonly IClusterMaterialExecutionResolver _executionResolver;

    public ClusterMaterialTable(IClusterMaterialExecutionResolver executionResolver)
        => _executionResolver = executionResolver
            ?? throw new ArgumentNullException(nameof(executionResolver));

    public ClusterMaterialSnapshot Current => Volatile.Read(ref _current);

    public ClusterMaterialProducer CreateProducer() => new(this);

    internal ClusterMaterialExecutionKeys ResolveExecutionKeys(Material material)
        => _executionResolver.Resolve(material);

    internal void Publish(ClusterMaterialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClusterMaterialSnapshot current = Current;
        if (current.HasSameTopology(snapshot))
        {
            if (current.HasSameEntityMappings(snapshot))
                return;
            snapshot.SetTopologyVersion(current.TopologyVersion);
            Volatile.Write(ref _current, snapshot);
            return;
        }
        snapshot.SetTopologyVersion(checked(++_topologyVersion));
        Volatile.Write(ref _current, snapshot);
    }
}

public sealed class ClusterMaterialSnapshot
{
    internal static ClusterMaterialSnapshot Empty { get; } =
        new([], [], [], 2, 0, 0, 0, [], []);

    private readonly ClusterMaterialSequence[] _sequences;
    private readonly int[] _entityGenerations;
    private readonly uint[] _entityOffsets;
    private ulong _topologyVersion;

    internal ClusterMaterialSnapshot(
        ClusterMaterialSequence[] sequences,
        Material[] materials,
        uint[] slotWords,
        uint slotCapacity,
        uint rasterBinCount,
        uint deformBinCount,
        uint shadeBinCount)
        : this(sequences, materials, slotWords, slotCapacity,
            rasterBinCount, deformBinCount, shadeBinCount, [], [])
    {
    }

    internal ClusterMaterialSnapshot(
        ClusterMaterialSequence[] sequences,
        Material[] materials,
        uint[] slotWords,
        uint slotCapacity,
        uint rasterBinCount,
        uint deformBinCount,
        uint shadeBinCount,
        int[] entityGenerations,
        uint[] entityOffsets)
    {
        _sequences = sequences;
        _entityGenerations = entityGenerations;
        _entityOffsets = entityOffsets;
        Materials = materials;
        SlotWords = slotWords;
        SlotCapacity = slotCapacity;
        RasterBinCount = rasterBinCount;
        DeformBinCount = deformBinCount;
        ShadeBinCount = shadeBinCount;
    }

    public ulong TopologyVersion => _topologyVersion;

    public IReadOnlyList<Material> Materials { get; }

    public ReadOnlyMemory<uint> SlotWords { get; }

    public uint SlotCapacity { get; }

    public uint MaterialCount => checked((uint)Materials.Count);

    public uint RasterBinCount { get; }

    public uint DeformBinCount { get; }

    public uint ShadeBinCount { get; }

    internal void SetTopologyVersion(ulong version) => _topologyVersion = version;

    internal bool HasSameTopology(ClusterMaterialSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (SlotCapacity != other.SlotCapacity ||
            RasterBinCount != other.RasterBinCount ||
            DeformBinCount != other.DeformBinCount ||
            ShadeBinCount != other.ShadeBinCount ||
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
            ClusterMaterialSequence left = _sequences[index];
            ClusterMaterialSequence right = other._sequences[index];
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

internal readonly record struct ClusterMaterialSequence(
    Material[] Materials,
    uint Offset);

public readonly record struct ClusterMaterialExecutionKeys(
    uint Raster,
    uint Deform,
    uint Shade);

public interface IClusterMaterialExecutionResolver
{
    ClusterMaterialExecutionKeys Resolve(Material material);
}

/// <summary>
/// Assigns independent dense execution keys from material runtime types. Slot values, textures,
/// and optional descriptor bases therefore never create execution bins by themselves.
/// </summary>
public sealed class ClusterMaterialTypeExecutionResolver : IClusterMaterialExecutionResolver
{
    private readonly Dictionary<Type, uint> _raster = [];
    private readonly Dictionary<Type, uint> _deform = [];
    private readonly Dictionary<Type, uint> _shade = [];

    public ClusterMaterialExecutionKeys Resolve(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        Type type = material.GetType();
        return new ClusterMaterialExecutionKeys(
            Resolve(_raster, type),
            Resolve(_deform, type),
            Resolve(_shade, type));
    }

    private static uint Resolve(Dictionary<Type, uint> values, Type type)
    {
        if (values.TryGetValue(type, out uint key)) return key;
        key = checked((uint)values.Count);
        values.Add(type, key);
        return key;
    }
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

        var builder = new SnapshotBuilder(_table);
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
        private readonly List<ClusterMaterialSequence> _sequences = [];
        private readonly ClusterMaterialTable _table;
        private readonly List<Material> _materials = [];
        private readonly HashSet<Material> _materialSet = [];
        private readonly List<ClusterMaterialExecutionKeys[]> _sequenceKeys = [];
        private int[] _entityGenerations = [];
        private uint[] _entityOffsets = [];
        private int _usedSlots;

        internal SnapshotBuilder(ClusterMaterialTable table) => _table = table;

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
            foreach (ClusterMaterialSequence sequence in _sequences)
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

            var materials = new Material[bindings.Length];
            var keys = new ClusterMaterialExecutionKeys[bindings.Length];
            for (int index = 0; index < bindings.Length; index++)
            {
                Material material = bindings[index].Material;
                ArgumentNullException.ThrowIfNull(material);
                materials[index] = material;
                keys[index] = _table.ResolveExecutionKeys(material);
                if (_materialSet.Add(material)) _materials.Add(material);
            }
            uint offset = checked((uint)_usedSlots);
            _usedSlots = checked(_usedSlots + bindings.Length);
            _sequences.Add(new ClusterMaterialSequence(materials, offset));
            _sequenceKeys.Add(keys);
            return offset;
        }

        internal ClusterMaterialSnapshot Build()
        {
            uint slotCapacity = checked((uint)Math.Max(2, (_usedSlots + 1) & ~1));
            var words = new uint[checked((int)(slotCapacity * ClusterMaterialTable.FieldCount))];
            int slot = 0;
            uint rasterBinCount = 0;
            uint deformBinCount = 0;
            uint shadeBinCount = 0;
            foreach (ClusterMaterialExecutionKeys[] keys in _sequenceKeys)
            {
                foreach (ClusterMaterialExecutionKeys key in keys)
                {
                    words[checked((int)(ClusterMaterialTable.RasterBinField * slotCapacity) + slot)] = key.Raster;
                    words[checked((int)(ClusterMaterialTable.DeformBinField * slotCapacity) + slot)] = key.Deform;
                    words[checked((int)(ClusterMaterialTable.ShadeBinField * slotCapacity) + slot)] = key.Shade;
                    rasterBinCount = Math.Max(rasterBinCount, checked(key.Raster + 1));
                    deformBinCount = Math.Max(deformBinCount, checked(key.Deform + 1));
                    shadeBinCount = Math.Max(shadeBinCount, checked(key.Shade + 1));
                    slot++;
                }
            }
            return new ClusterMaterialSnapshot(
                [.. _sequences],
                [.. _materials],
                words,
                slotCapacity,
                rasterBinCount,
                deformBinCount,
                shadeBinCount,
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
