using SomeEngine.Resources;
using System.Buffers.Binary;
using System.Collections.Immutable;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Scalar bytes derived from extracted materials, with addresses retained across local edits.</summary>
internal sealed class ClusterMaterialScalars
{
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
        var addresses = new HashSet<int>(assignments.ChangedOffsets);
        foreach (long material in assignments.ChangedMaterialIds)
            addresses.UnionWith(assignments.GetMaterialOffsets(material));
        if (textureChanges is not null) addresses.UnionWith(textureChanges);
        if (addresses.Count == 0) return this;

        var ranges = _ranges;
        var free = _free;
        var writes = new List<(int Offset, byte[] Bytes)>();
        var offsetChanges = new List<BufferRange>();
        int byteCount = Bytes.Length;
        var offsetWrites = new List<(int Offset, byte[] Bytes)>();
        foreach (int address in addresses)
        {
            bool exists = assignments.TryGetEntry(address, out Material material, out _);
            // Complete every potentially failing encoding before publishing any new bytes.
            byte[] value = exists ? stages.ScalarLayouts[address].Encode(material, textureIndex) : [];
            bool allocated = ranges.TryGetValue(address, out var range);
            if (allocated && (!exists || range.Size != value.Length))
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
                if (free.TryGetValue(value.Length, out var available) && !available.IsEmpty)
                {
                    offset = available.Peek();
                    free = free.SetItem(value.Length, available.Pop());
                }
                else
                {
                    offset = byteCount;
                    byteCount = checked(byteCount + value.Length);
                }
                range = (offset, value.Length);
                ranges = ranges.SetItem(address, range);
            }
            if (!allocated) WriteOffset(address, checked((uint)range.Offset));
            writes.Add((range.Offset, value));
        }
        ClusterBytes bytes = Bytes.WithChanges(byteCount, writes);
        ClusterBytes offsets = Offsets.WithChanges(checked((int)assignments.SlotCapacity * sizeof(uint)), offsetWrites);
        return new ClusterMaterialScalars
        {
            _ranges = ranges, _free = free, Offsets = offsets, Bytes = bytes,
            ByteChanges = writes.Select(write => new BufferRange(checked((ulong)write.Offset),
                checked((ulong)write.Bytes.Length))).ToArray(),
            OffsetChanges = offsetChanges.ToArray(),
        };

        void WriteOffset(int address, uint offset)
        {
            byte[] value = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, offset);
            int byteOffset = checked(address * sizeof(uint));
            offsetWrites.Add((byteOffset, value));
            offsetChanges.Add(new BufferRange(checked((ulong)byteOffset), sizeof(uint)));
        }
    }
}
