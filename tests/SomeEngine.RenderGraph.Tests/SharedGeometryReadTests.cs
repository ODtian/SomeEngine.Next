using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class SharedGeometryReadTests
{
    [Fact]
    public void RetainedReadersObserveFrameUploadsAndChangedSparseRanges()
    {
        const int readerCount = 24;
        const int rangeCount = 128;
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(readerCount * rangeCount * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferId geometry, result;
        GraphPassId[] readers = new GraphPassId[readerCount];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            geometry = edit.CreateTransientBuffer(new BufferDesc(rangeCount * 8,
                BufferUsages.CopyDestination | BufferUsages.CopySource));
            result = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            for (int index = 0; index < readers.Length; index++)
            {
                var data = new SparseRead(geometry, result, BufferRangeSet.Empty, (ulong)index * rangeCount * 4);
                readers[index] = edit.AddCopyFramePass("retained geometry reader",
                    PassQueueSelection.Exact(support.GraphicsQueue), data, default,
                    static (ref PassDefinition pass, ref SparseRead data) =>
                        pass.Write(data.Destination, new(data.OutputOffset, rangeCount * 4),
                            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in SparseRead data) =>
                    {
                        ulong offset = data.OutputOffset;
                        foreach (BufferRange range in data.Ranges.Ranges)
                        {
                            commands.CopyBuffer(new(commands.GetBuffer(data.Source), range.Offset,
                                commands.GetBuffer(data.Destination), offset, range.Size));
                            offset += range.Size;
                        }
                    });
            }
            edit.Commit();
        }
        var even = new BufferRangeSet(Enumerable.Range(0, rangeCount).Select(i => new BufferRange((ulong)i * 8, 4)).ToArray());
        var odd = new BufferRangeSet(Enumerable.Range(0, rangeCount).Select(i => new BufferRange((ulong)i * 8 + 4, 4)).ToArray());
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 4; frameIndex++)
        {
            BufferRangeSet ranges = frameIndex < 2 ? even : odd;
            byte[] bytes = Enumerable.Range(0, rangeCount * 8).Select(i => (byte)(i + frameIndex * 17)).ToArray();
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                GraphBufferSlice source = frame.Upload(bytes, BufferUsages.CopySource);
                var copies = new SparseRead(source.Buffer, geometry, ranges, source.Range.Offset);
                GraphPassId upload = frame.AddCopyPass("frame geometry uploads",
                    PassQueueSelection.Exact(support.GraphicsQueue), copies, default,
                    static (ref PassDefinition pass, ref SparseRead data) =>
                    {
                        pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                        foreach (BufferRange range in data.Ranges.Ranges)
                            pass.Write(data.Destination, range, PipelineSync.Copy,
                                ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    static (ref CopyPassCommandScope commands, in SparseRead data) =>
                    {
                        foreach (BufferRange range in data.Ranges.Ranges)
                            commands.CopyBuffer(new(commands.GetBuffer(data.Source), data.OutputOffset + range.Offset,
                                commands.GetBuffer(data.Destination), range.Offset, range.Size));
                    });
                for (int index = 0; index < readers.Length; index++)
                {
                    frame.SetPassData(readers[index], new SparseRead(geometry, result, ranges, (ulong)index * rangeCount * 4));
                    PassDefinition pass = frame.GetPass(readers[index]);
                    pass.ReadRanges(geometry, ranges, PipelineSync.Copy, ResourceAccess.CopySource);
                    frame.OrderAfter(readers[index], upload);
                }
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer readback = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            for (int reader = 0; reader < readers.Length; reader++)
                for (int range = 0; range < rangeCount; range++)
                    Assert.True(readback.Bytes.Slice((reader * rangeCount + range) * 4, 4)
                        .SequenceEqual(bytes.AsSpan((int)ranges.Ranges[range].Offset, 4)));
        }
    }

    private readonly record struct SparseRead(GraphBufferId Source, GraphBufferId Destination,
        BufferRangeSet Ranges, ulong OutputOffset);
}
