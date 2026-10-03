using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph;

internal readonly record struct BufferUploadCopy(GraphBufferSlice Source, BufferRange Destination);

internal sealed class BufferUploadCommands : PassCallbackStorage, IStaticPassDataStorage<GraphBufferId>
{
    internal GraphBufferId Buffer;
    internal override Type FrameDataType => typeof(void);
    public void SetStaticData(in GraphBufferId value) => Buffer = value;
    internal override void ClearData() => Buffer = default;

    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var commands = new CopyPassCommandScope(frame, passIndex, context);
        foreach (BufferUploadCopy copy in frame.GetBufferUploads(Buffer))
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(copy.Source.Buffer), copy.Source.Range.Offset,
                commands.GetBuffer(Buffer), copy.Destination.Offset, copy.Destination.Size));
        commands.Finish();
    }
}

public sealed partial class RenderGraph
{
    [System.Diagnostics.Conditional("DEBUG")]
    internal void ValidateBufferUpload(GraphBufferId buffer, ulong offset, int length, GraphStructure? structure = null)
    {
        structure ??= _editStructure ?? _structure;
        DevelopmentCheck.Require(buffer.Value.Owner == Identity && structure.Buffers.Contains(buffer.Value),
            "The upload target must belong to this graph.");
        GraphBuffer data = structure.Buffers.Get(buffer.Value);
        DevelopmentCheck.Require(data.Lifetime == RenderGraphResourceLifetime.Persistent &&
            data.MemoryType == MemoryType.DeviceLocal &&
            (data.Description.Usages & BufferUsages.CopyDestination) != 0,
            "Buffer uploads require a persistent device buffer with CopyDestination usage.");
        DevelopmentCheck.Require(length > 0 && offset <= data.Description.Size &&
            (ulong)length <= data.Description.Size - offset, "The upload range must fit in the target buffer.");
    }

    internal void EnsureBufferUploadPasses(IReadOnlyList<FrameBufferTransfers.BufferUpload> uploads)
    {
        if (_editToken != 0) return;
        GraphBufferId[] buffers = uploads.Select(upload => upload.Destination).Distinct()
            .Where(buffer => _structure.Buffers.Contains(buffer.Value) &&
                _structure.Buffers.Get(buffer.Value).BufferUploadPass == default).ToArray();
        if (buffers.Length == 0) return;
        using RenderGraphEdit edit = BeginEdit();
        foreach (GraphBufferId buffer in buffers) edit.AddBufferUploadPass(buffer);
        edit.Commit();
    }
}

public ref partial struct RenderGraphEdit
{
    /// <summary>Installs a persistent upload pass and returns its identity for explicit ordering.</summary>
    public GraphPassId AddBufferUploadPass(GraphBufferId buffer)
    {
        ValidateOwner(buffer.Value);
        GraphPassId existing = Structure.Buffers.Get(buffer.Value).BufferUploadPass;
        if (existing != default) return existing;
        if (Structure.BufferUploadRegion == default)
        {
            Staging.BufferUploadRegion = AddRegion("Buffer uploads");
            GraphRegion root = Staging.RootRegion;
            GraphRegionEntry entry = root.Children[^1];
            root.Children = root.Children.RemoveAt(root.Children.Count - 1).Insert(0, entry);
            GraphRegionEntry interval = Structure.GetRegionInterval(default);
            RecompileRegion(default, interval.Start, interval.End);
        }
        GraphPassId pass = AddPersistentFramePass("Upload buffer", GraphPassKind.Copy,
            PassQueueSelection.AnyCompatible(), buffer, new PassOptions(Culling: PassCullingMode.NeverCull),
            null, VariableLayoutReflection.Null, [],
            static (ref PassDefinition pass, ref GraphBufferId data) => { },
            new BufferUploadCommands(), Structure.BufferUploadRegion);
        Staging.Buffers.GetMutable(buffer.Value).BufferUploadPass = pass;
        Changes.TouchBuffer(buffer.Value);
        return pass;
    }
}

public ref partial struct RenderGraphFrame
{
    public Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length) =>
        State.GetWriteSpan(buffer, offset, length);

    /// <summary>Returns the buffer ranges written by the next frame and during recording.</summary>
    public BufferRangeSet GetBufferUploadRanges(GraphBufferId buffer) => State.GetBufferUploadRanges(buffer);

    public void WriteBuffer(GraphBufferId buffer, ulong offset, scoped ReadOnlySpan<byte> data) =>
        data.CopyTo(GetWriteSpan(buffer, offset, data.Length));
}

internal sealed partial class RenderGraphFrameState
{
    private readonly Dictionary<GraphBufferId, BufferUploadCopy[]> _bufferUploads = [];

    internal Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length)
    {
        EnsureAuthoring();
        Graph.ValidateBufferUpload(buffer, offset, length, StructureIndex.Structure);
        return Slot.GetBufferTransfers(Graph).GetWriteSpan(buffer, offset, length);
    }

    internal bool HasBufferUploads(PassCallbackStorage commands) =>
        commands is not BufferUploadCommands upload || _bufferUploads.ContainsKey(upload.Buffer);

    internal BufferUploadCopy[] GetBufferUploads(GraphBufferId buffer) => _bufferUploads[buffer];

    internal BufferRangeSet GetBufferUploadRanges(GraphBufferId buffer)
    {
        EnsureAuthoring();
        var ranges = new List<BufferRange>();
        if (_bufferUploads.TryGetValue(buffer, out BufferUploadCopy[]? copies))
            foreach (BufferUploadCopy copy in copies) ranges.Add(copy.Destination);
        Slot.GetBufferTransfers(Graph).AddUploadRanges(buffer, ranges);
        ranges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var combined = new List<BufferRange>();
        foreach (BufferRange range in ranges)
        {
            if (combined.Count != 0 && combined[^1].Offset + combined[^1].Size >= range.Offset)
                combined[^1] = combined[^1] with
                {
                    Size = Math.Max(combined[^1].Offset + combined[^1].Size, range.Offset + range.Size) - combined[^1].Offset,
                };
            else combined.Add(range);
        }
        return new BufferRangeSet(CollectionsMarshal.AsSpan(combined));
    }

    internal void CollectBufferUploads(FrameBufferTransfers.BufferUpload[] uploads)
    {
        foreach (var group in uploads.GroupBy(upload => upload.Destination))
        {
            GraphBufferId destination = group.Key;
            var copies = new List<BufferUploadCopy>();
            if (_bufferUploads.TryGetValue(destination, out BufferUploadCopy[]? previous)) copies.AddRange(previous);
            foreach (FrameBufferTransfers.BufferUpload upload in group)
            {
                Buffer resource = upload.Page.Buffer;
                GraphBufferId source = Import(resource, upload.Page.BoundaryStates);
                copies.Add(new BufferUploadCopy(new GraphBufferSlice(source, upload.Range),
                    new BufferRange(upload.Offset, upload.Range.Size)));
            }
            _bufferUploads[destination] = CombineBufferUploads(copies);
        }
    }

    private static BufferUploadCopy[] CombineBufferUploads(List<BufferUploadCopy> copies)
    {
        int[] order = Enumerable.Range(0, copies.Count).ToArray();
        Array.Sort(order, (left, right) => copies[left].Destination.Offset.CompareTo(copies[right].Destination.Offset));
        ulong end = 0;
        bool overlap = false;
        foreach (int index in order)
        {
            BufferRange range = copies[index].Destination;
            overlap |= range.Offset < end;
            end = Math.Max(end, range.Offset + range.Size);
        }
        List<BufferUploadCopy> ranges = overlap ? ResolveOverlappingUploads(copies) :
            order.Select(index => copies[index]).ToList();
        var combined = new List<BufferUploadCopy>();
        foreach (BufferUploadCopy copy in ranges)
        {
            if (combined.Count != 0)
            {
                BufferUploadCopy previous = combined[^1];
                if (previous.Source.Buffer == copy.Source.Buffer &&
                    previous.Source.Range.Offset + previous.Source.Range.Size == copy.Source.Range.Offset &&
                    previous.Destination.Offset + previous.Destination.Size == copy.Destination.Offset)
                {
                    combined[^1] = new BufferUploadCopy(new GraphBufferSlice(copy.Source.Buffer,
                        previous.Source.Range with { Size = previous.Source.Range.Size + copy.Source.Range.Size }),
                        previous.Destination with { Size = previous.Destination.Size + copy.Destination.Size });
                    continue;
                }
            }
            combined.Add(copy);
        }
        return combined.ToArray();
    }

    private static List<BufferUploadCopy> ResolveOverlappingUploads(List<BufferUploadCopy> copies)
    {
        var boundaries = new List<(ulong Offset, int Write, bool Begins)>(copies.Count * 2);
        for (int index = 0; index < copies.Count; index++)
        {
            BufferRange range = copies[index].Destination;
            boundaries.Add((range.Offset, index, true));
            boundaries.Add((range.Offset + range.Size, index, false));
        }
        boundaries.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var active = new SortedSet<int>();
        var result = new List<BufferUploadCopy>();
        int boundary = 0;
        while (boundary < boundaries.Count)
        {
            ulong start = boundaries[boundary].Offset;
            do
            {
                var entry = boundaries[boundary++];
                if (entry.Begins) active.Add(entry.Write);
                else active.Remove(entry.Write);
            } while (boundary < boundaries.Count && boundaries[boundary].Offset == start);
            if (active.Count == 0 || boundary == boundaries.Count) continue;
            BufferUploadCopy copy = copies[active.Max];
            ulong length = boundaries[boundary].Offset - start;
            ulong source = copy.Source.Range.Offset + start - copy.Destination.Offset;
            result.Add(new BufferUploadCopy(new GraphBufferSlice(copy.Source.Buffer, new BufferRange(source, length)),
                new BufferRange(start, length)));
        }
        return result;
    }

    private void PrepareBufferUploads()
    {
        FrameBufferTransfers transfers = Slot.GetBufferTransfers(Graph);
        FrameBufferTransfers.BufferUpload[] uploads = transfers.TakeUploads();
        Graph.EnsureBufferUploadPasses(uploads);
        CollectBufferUploads(uploads);
        foreach (var (destination, copies) in _bufferUploads)
        {
            GraphPassId pass = StructureIndex.Structure.Buffers.Get(destination.Value).BufferUploadPass;
            if (pass == default)
            {
                _ = AddCopyPass("Upload buffer", PassQueueSelection.AnyCompatible(),
                    (destination, copies), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition definition, ref (GraphBufferId Buffer, BufferUploadCopy[] Copies) data) =>
                        DeclareBufferUploads(ref definition, data.Buffer, data.Copies),
                    static (ref CopyPassCommandScope commands, in (GraphBufferId Buffer, BufferUploadCopy[] Copies) data) =>
                    {
                        foreach (BufferUploadCopy copy in data.Copies)
                            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(copy.Source.Buffer), copy.Source.Range.Offset,
                                commands.GetBuffer(data.Buffer), copy.Destination.Offset, copy.Destination.Size));
                    }, default);
                FramePass row = _dynamicPasses[^1];
                row.IsBufferUpload = true;
                _dynamicPasses[^1] = row;
            }
            else
            {
                PassDefinition definition = GetPass(pass);
                DeclareBufferUploads(ref definition, destination, copies);
                _executor.ChangePass(pass.Value);
            }
        }
        transfers.FlushUploads();
    }

    private void ClearBufferUploads()
    {
        foreach (GraphBufferId buffer in _bufferUploads.Keys)
        {
            GraphPassId pass = StructureIndex.Structure.Buffers.Get(buffer.Value).BufferUploadPass;
            if (pass != default) _executor.ChangePass(pass.Value);
        }
        _bufferUploads.Clear();
    }

    private static void DeclareBufferUploads(ref PassDefinition definition, GraphBufferId destination,
        ReadOnlySpan<BufferUploadCopy> copies)
    {
        foreach (BufferUploadCopy copy in copies)
            definition.Read(copy.Source.Buffer, copy.Source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
        BufferRange range = copies[0].Destination;
        for (int index = 1; index < copies.Length; index++)
        {
            BufferRange next = copies[index].Destination;
            if (next.Offset <= range.Offset + range.Size)
                range = range with { Size = Math.Max(range.Offset + range.Size, next.Offset + next.Size) - range.Offset };
            else
            {
                definition.Write(destination, range, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                range = next;
            }
        }
        definition.Write(destination, range, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }
}
