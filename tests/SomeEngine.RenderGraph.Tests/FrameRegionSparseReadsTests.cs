using Xunit.Abstractions;
using static SomeEngine.RenderGraph.Tests.FrameRegionTests;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameRegionSparseReadsTests(ITestOutputHelper output)
{
    [Fact]
    public void UnchangedSparseReadsInAFrameRegionReuseAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 31);
            region = edit.AddFrameRegion("Read");
            edit.Commit();
        }
        var ranges = new BufferRangeSet([new(0, 8), new(8, 8)]);
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Read sparse ranges", PassQueueSelection.Exact(data.Queue),
                new SparseCopyData(data.Values, data.Output, ranges), default,
                static (ref PassDefinition pass, ref SparseCopyData value) =>
                {
                    pass.ReadRanges(value.Source, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource);
                    _ = pass.Write(value.Destination, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in SparseCopyData value) =>
                {
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(value.Source), 0,
                        commands.GetBuffer(value.Destination), 0, 8));
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(value.Source), 8,
                        commands.GetBuffer(value.Destination), 8, 8));
                }, region);
            data.Execute(frame);
        }
        output.WriteLine($"Unchanged: reused={statistics.AnalysisReused}, analyzed={statistics.AnalyzedPassCount}, rebuilt={statistics.RebuiltRowCount}");
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(new uint[] { 31, 31, 31, 31 }, data.Read());
    }

    [Fact]
    public void GrowingAFrameRegionLeavesOtherSparseReadsOutOfAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId changing;
        GraphRegionId other;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 41);
            AddClear(edit, data.Queue, default, data.Saved, 79);
            changing = edit.AddFrameRegion("Changing");
            other = edit.AddFrameRegion("Other");
            AddCopy(edit, data.Queue, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        var ranges = new BufferRangeSet([new(0, 16)]);
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Read", PassQueueSelection.Exact(data.Queue),
                new SparseReadData(data.Values, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref SparseReadData value) =>
                    pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in SparseReadData _) => { }, changing);
            if (frameNumber == 2)
                _ = frame.AddCopyPass("Added read", PassQueueSelection.Exact(data.Queue),
                    new SparseReadData(data.Values, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref SparseReadData value) =>
                        pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in SparseReadData _) => { }, changing);
            for (int reader = 0; reader < 8; reader++)
                _ = frame.AddCopyPass("Other read", PassQueueSelection.Exact(data.Queue),
                    new SparseReadData(data.Saved, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref SparseReadData value) =>
                        pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in SparseReadData _) => { }, other);
            data.Execute(frame);
        }
        output.WriteLine($"Growing: analyzed={statistics.AnalyzedPassCount}, rebuilt={statistics.RebuiltRowCount}");
        Assert.InRange(statistics.AnalyzedPassCount, 1, 4);
        Assert.Equal(new uint[] { 41, 41, 41, 41 }, data.Read());
    }

    [Fact]
    public void ShrinkingAFrameRegionLeavesOtherSparseReadsOutOfAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId changing;
        GraphRegionId other;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 43);
            AddClear(edit, data.Queue, default, data.Saved, 83);
            changing = edit.AddFrameRegion("Changing");
            other = edit.AddFrameRegion("Other");
            AddCopy(edit, data.Queue, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        var ranges = new BufferRangeSet([new(0, 16)]);
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Read", PassQueueSelection.Exact(data.Queue),
                new SparseReadData(data.Values, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref SparseReadData value) =>
                    pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in SparseReadData _) => { }, changing);
            if (frameNumber < 2)
                _ = frame.AddCopyPass("Removed read", PassQueueSelection.Exact(data.Queue),
                    new SparseReadData(data.Values, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref SparseReadData value) =>
                        pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in SparseReadData _) => { }, changing);
            for (int reader = 0; reader < 8; reader++)
                _ = frame.AddCopyPass("Other read", PassQueueSelection.Exact(data.Queue),
                    new SparseReadData(data.Saved, ranges), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref SparseReadData value) =>
                        pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in SparseReadData _) => { }, other);
            data.Execute(frame);
        }
        output.WriteLine($"Shrinking: analyzed={statistics.AnalyzedPassCount}, rebuilt={statistics.RebuiltRowCount}");
        Assert.InRange(statistics.AnalyzedPassCount, 1, 3);
        Assert.Equal(new uint[] { 43, 43, 43, 43 }, data.Read());
    }

    [Fact]
    public void AFailedFramePassDeclarationDiscardsItsSparseReads()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 53);
            region = edit.AddFrameRegion("Read");
            edit.Commit();
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            bool failed = false;
            try
            {
                _ = frame.AddCopyPass("Failed read", PassQueueSelection.Exact(data.Queue),
                    new SparseReadData(data.Saved, new BufferRangeSet([new(0, 16)])), default,
                    static (ref PassDefinition pass, ref SparseReadData value) =>
                    {
                        pass.ReadRanges(value.Buffer, value.Ranges, PipelineSync.Copy, ResourceAccess.CopySource);
                        throw new InvalidOperationException();
                    },
                    static (ref CopyPassCommandScope _, in SparseReadData _) => { }, region);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            Assert.True(failed);
            _ = frame.AddCopyPass("Read initialized values", PassQueueSelection.Exact(data.Queue),
                new CopyData(data.Values, data.Output), default, DeclareCopy, Copy, region);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 53, 53, 53, 53 }, data.Read());
    }

    private readonly record struct SparseReadData(GraphBufferId Buffer, BufferRangeSet Ranges);
    private readonly record struct SparseCopyData(GraphBufferId Source, GraphBufferId Destination, BufferRangeSet Ranges);
}
