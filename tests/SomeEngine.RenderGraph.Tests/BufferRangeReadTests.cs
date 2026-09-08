using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class BufferRangeReadTests
{
    [Fact]
    public void PartialReadsPreserveOtherDefinedBytesAcrossFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer upload = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        byte[] bytes = Enumerable.Range(0, 32).Select(index => (byte)(index + 1)).ToArray();
        using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, BufferRange.Whole))
            bytes.CopyTo(mapping.Bytes);
        GraphBufferId buffer, resultId;
        GraphPassId initialize, reader;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId uploadId = edit.RegisterExternalBuffer(upload,
                [new BufferBoundaryState(new BufferRange(0, 32), upload.InitialSync,
                    upload.InitialAccess, ResourceContentState.Defined)]);
            resultId = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(new BufferRange(0, 8), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
            buffer = edit.CreatePersistentBuffer(new BufferDesc(32,
                BufferUsages.CopyDestination | BufferUsages.CopySource));
            initialize = edit.AddCopyFramePass("initialize persistent bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                new SparseCopy(uploadId, buffer, 0, 0, 32), default,
                static (ref PassDefinition pass, ref SparseCopy data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, static (ref CopyPassCommandScope commands, in SparseCopy data) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 32)));
            edit.AddCopyFramePass("partially update previously initialized bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                buffer, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferId data) =>
                    pass.Write(data, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial),
                static (ref CopyPassCommandScope commands, in GraphBufferId data) => { });
            reader = edit.AddCopyFramePass("read changing persistent subset", PassQueueSelection.Exact(support.GraphicsQueue),
                new SparseCopy(buffer, resultId, 0, 0, 4), default,
                static (ref PassDefinition pass, ref SparseCopy data) =>
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete), SparseCopy.RecordPacked);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (uint index = 0; index < 3; index++)
        {
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.SetPassEnabled(initialize, index == 0);
                frame.SetPassData(reader, new SparseCopy(buffer, resultId, index * 4, 0, 4));
                PassDefinition pass = frame.GetPass(reader);
                pass.ReadRanges(buffer, new BufferRangeSet([new(index * 4, 4), new(index * 4 + 16, 4)]),
                    PipelineSync.Copy, ResourceAccess.CopySource);
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer readback = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            Assert.True(readback.Bytes[..4].SequenceEqual(bytes.AsSpan((int)index * 4, 4)));
            Assert.True(readback.Bytes[4..8].SequenceEqual(bytes.AsSpan((int)index * 4 + 16, 4)));
        }
    }

    [Fact]
    public void SharedSparseReadsReuseAnalysisAndRetainUploadDependencies()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer upload = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopySource, "sparse source upload"), MemoryType.Upload);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(24, BufferUsages.CopyDestination, "sparse packed results"), MemoryType.Readback);
        GraphBufferId uploadId, buffer, resultId;
        GraphPassId[] readers = new GraphPassId[3];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            uploadId = edit.DeclareExternalBuffer(new BufferDesc(32, BufferUsages.CopySource), MemoryType.Upload);
            resultId = edit.DeclareExternalBuffer(new BufferDesc(24, BufferUsages.CopyDestination), MemoryType.Readback);
            buffer = edit.CreateTransientBuffer(new BufferDesc(32,
                BufferUsages.CopyDestination | BufferUsages.CopySource, "sparse intermediate"));
            edit.AddCopyFramePass("upload two sparse bands", PassQueueSelection.Exact(support.GraphicsQueue),
                new SparseCopy(uploadId, buffer, 0, 0, 8), default,
                static (ref PassDefinition pass, ref SparseCopy data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, new BufferRange(0, 8), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    pass.Write(data.Destination, new BufferRange(16, 8), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                }, SparseCopy.RecordBands);
            for (int index = 0; index < readers.Length; index++)
                readers[index] = edit.AddCopyFramePass("read sparse bands",
                    PassQueueSelection.Exact(support.GraphicsQueue),
                    new SparseCopy(buffer, resultId, 0, checked((ulong)index * 8), 4), default,
                    static (ref PassDefinition pass, ref SparseCopy data) =>
                        pass.Write(data.Destination, new BufferRange(data.DestinationOffset, 8),
                            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    SparseCopy.RecordPacked);
            edit.Commit();
        }
        BufferRange[] original = [new(0, 4), new(16, 4)];
        var first = new BufferRangeSet(original);
        original[0] = new BufferRange(8, 4); // The caller cannot mutate a retained read set.
        var second = new BufferRangeSet([new(4, 4), new(20, 4)]);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool reused = false;
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            byte[] bytes = Enumerable.Range(0, 32).Select(value => (byte)(frameIndex * 32 + value)).ToArray();
            using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, BufferRange.Whole))
                bytes.CopyTo(mapping.Bytes);
            ulong offset = frameIndex < 3 ? 0u : 4u;
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Capture)))
            {
                frame.BindExternalBuffer(resultId, result,
                    [new BufferBoundaryState(new BufferRange(0, 24), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
                frame.BindExternalBuffer(uploadId, upload,
                    [new BufferBoundaryState(new BufferRange(0, 32),
                        frameIndex == 0 ? upload.InitialSync : PipelineSync.Copy,
                        frameIndex == 0 ? upload.InitialAccess : ResourceAccess.CopySource,
                        ResourceContentState.Defined)]);
                for (int index = 0; index < readers.Length; index++)
                {
                    PassDefinition pass = frame.GetPass(readers[index]);
                    pass.ReadRanges(buffer, frameIndex < 3 ? first : second,
                        PipelineSync.Copy, ResourceAccess.CopySource);
                    frame.SetPassData(readers[index], new SparseCopy(buffer, resultId,
                        offset, checked((ulong)index * 8), 4));
                }
                frame.Execute(completions);
                support.Wait(completions);
            }
            Assert.True((frameIndex is not (0 or 3)) == reused, $"Frame {frameIndex}: analysis reused = {reused}");
            using MappedBuffer readback = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            for (int reader = 0; reader < readers.Length; reader++)
            {
                Assert.True(readback.Bytes.Slice(reader * 8, 4).SequenceEqual(bytes.AsSpan((int)offset, 4)));
                Assert.True(readback.Bytes.Slice(reader * 8 + 4, 4).SequenceEqual(bytes.AsSpan((int)offset + 16, 4)));
            }
        }
#if DEBUG
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalBuffer(resultId, result,
                [new BufferBoundaryState(new BufferRange(0, 24), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
            frame.BindExternalBuffer(uploadId, upload,
                [new BufferBoundaryState(new BufferRange(0, 32), PipelineSync.Copy,
                    ResourceAccess.CopySource, ResourceContentState.Defined)]);
            PassDefinition pass = frame.GetPass(readers[0]);
            pass.ReadRanges(buffer, new BufferRangeSet([new BufferRange(8, 4)]),
                PipelineSync.Copy, ResourceAccess.CopySource);
            string? failure = null;
            try { frame.Execute(completions); }
            catch (InvalidOperationException error) { failure = error.Message; }
            Assert.Contains("RG4001", failure);
        }
#endif
        void Capture(in RenderGraphDiagnosticsView diagnostics) => reused = diagnostics.Statistics.AnalysisReused;
    }

    private readonly record struct SparseCopy(
        GraphBufferId Source, GraphBufferId Destination, ulong SourceOffset, ulong DestinationOffset, ulong Size)
    {
        internal static void RecordBands(ref CopyPassCommandScope commands, in SparseCopy data)
        {
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                commands.GetBuffer(data.Destination), 0, 8));
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 16,
                commands.GetBuffer(data.Destination), 16, 8));
        }

        internal static void RecordPacked(ref CopyPassCommandScope commands, in SparseCopy data)
        {
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), data.SourceOffset,
                commands.GetBuffer(data.Destination), data.DestinationOffset, data.Size));
            commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), data.SourceOffset + 16,
                commands.GetBuffer(data.Destination), data.DestinationOffset + 4, data.Size));
        }
    }
}
