using SomeEngine.Resources;
using System.Collections.Immutable;
using SomeEngine.Render.Materials;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Scalar bytes derived from extracted materials, with addresses retained across local edits.</summary>
internal sealed class ClusterMaterialScalars
{
    private readonly record struct ScalarValue(Material Material,
        ScalarLayout.ScalarMaterialLayout Layout, Func<SomeEngine.Resources.Texture?, uint> TextureIndex);

    internal static ClusterMaterialScalars Empty { get; } = new();
    private ImmutableDictionary<int, (int Offset, int Size)> _ranges =
        ImmutableDictionary<int, (int, int)>.Empty;
    private ImmutableDictionary<int, ImmutableStack<int>> _free =
        ImmutableDictionary<int, ImmutableStack<int>>.Empty;

    internal ClusterBytes Offsets { get; private set; } = ClusterBytes.Zero(sizeof(uint));
    internal ClusterBytes Bytes { get; private set; } = ClusterBytes.Zero(ScalarLayout.HeaderByteSize);
    internal BufferRange[] ByteChanges { get; private set; } = [];
    internal BufferRange[] OffsetChanges { get; private set; } = [];

    internal ClusterMaterialScalars Update(
        ClusterShadeStageSet stages, ClusterMaterialAssignmentSet assignments,
        Func<SomeEngine.Resources.Texture?, uint> textureIndex, IEnumerable<int>? textureChanges = null)
    {
        if (assignments.ChangedOffsets.Count == 0 && assignments.ChangedMaterialIds.Count == 0 &&
            (textureChanges is null ||
                (textureChanges.TryGetNonEnumeratedCount(out int textureCount) && textureCount == 0)))
            return this;

        var addresses = new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        if (textureChanges is not null) addresses.UnionWith(textureChanges);
        if (addresses.Count == 0)
            return this;

        var ranges = _ranges;
        var free = _free;
        var writes = new List<(int Offset, int Size, ScalarValue Value)>(addresses.Count);
        int byteCount = Bytes.Length;
        var offsetWrites = new List<(int Offset, uint Value)>();
        foreach (int address in addresses)
        {
            bool exists = assignments.TryGetEntry(address, out Material material, out _);
            ScalarLayout.ScalarMaterialLayout? layout = exists ? stages.ScalarLayouts[address] : null;
            int size = layout?.ByteSize ?? 0;
            bool allocated = ranges.TryGetValue(address, out var range);
            if (allocated && (!exists || range.Size != size))
            {
                free = free.SetItem(range.Size, free.GetValueOrDefault(range.Size,
                    ImmutableStack<int>.Empty).Push(range.Offset));
                ranges = ranges.Remove(address);
                allocated = false;
            }
            if (!exists)
            {
                WriteOffset(address, 0);
                continue;
            }
            if (!allocated)
            {
                int offset;
                if (free.TryGetValue(size, out var available) && !available.IsEmpty)
                {
                    offset = available.Peek();
                    free = free.SetItem(size, available.Pop());
                }
                else
                {
                    offset = byteCount;
                    byteCount = checked(byteCount + size);
                }
                range = (offset, size);
                ranges = ranges.SetItem(address, range);
            }
            if (!allocated) WriteOffset(address, checked((uint)range.Offset));
            writes.Add((range.Offset, size, new(material, layout!, textureIndex)));
        }
        // Encoders write into copied pages. An encoding failure leaves the accepted bytes intact.
        ClusterBytes bytes = Bytes.WithChanges(byteCount, writes, static (value, destination) =>
            value.Layout.Encode(value.Material, destination, value.TextureIndex));
        ClusterBytes offsets = Offsets.WithUInt32Changes(checked((int)assignments.SlotCapacity * sizeof(uint)), offsetWrites);
        return new ClusterMaterialScalars
        {
            _ranges = ranges, _free = free, Offsets = offsets, Bytes = bytes,
            ByteChanges = writes.Select(write => new BufferRange(checked((ulong)write.Offset),
                checked((ulong)write.Size))).ToArray(),
            OffsetChanges = offsetWrites.Select(write => new BufferRange(checked((ulong)write.Offset),
                sizeof(uint))).ToArray(),
        };

        void WriteOffset(int address, uint offset)
        {
            int byteOffset = checked(address * sizeof(uint));
            offsetWrites.Add((byteOffset, offset));
        }
    }

}
