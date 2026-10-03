using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class FrameRegionTests
{
    [Fact]
    public void FrameRegionPassesExecuteBetweenTheSurroundingRegions()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId middle;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            GraphRegionId before = edit.AddRegion("Before");
            middle = edit.AddFrameRegion("Middle");
            GraphRegionId after = edit.AddRegion("After");
            AddClear(edit, data.Queue, before, data.Values, 17);
            AddCopy(edit, data.Queue, after, data.Saved, data.Output);
            edit.Commit();
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            _ = frame.AddCopyPass("Save earlier value", PassQueueSelection.Exact(data.Queue),
                new CopyData(data.Values, data.Saved), default, DeclareCopy, Copy, region: middle);
            _ = frame.AddCopyPass("Overwrite", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 29), default, DeclareClear, Clear, region: middle);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 17, 17, 17, 17 }, data.Read());
    }

    [Fact]
    public void MatchingFrameRegionShapesReuseAnalysisWithUpdatedCommandData()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            region = edit.AddFrameRegion("Write");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        for (uint value = 1; value <= 3; value++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Clear", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, value), default, DeclareClear, Clear, region);
            data.Execute(frame);
        }
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(new uint[] { 3, 3, 3, 3 }, data.Read());
    }

    [Fact]
    public void GrowingOneFrameRegionAnalyzesItsPassesAndSharedResourceUsers()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId first;
        GraphRegionId second;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            first = edit.AddFrameRegion("Changing");
            second = edit.AddFrameRegion("Other");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Write", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 41), default, DeclareClear, Clear, first);
            if (frameNumber == 2)
                _ = frame.AddCopyPass("Added writer", PassQueueSelection.Exact(data.Queue),
                    new ClearData(data.Values, 43), default, DeclareClear, Clear, first);
            for (int pass = 0; pass < 8; pass++)
                _ = frame.AddCopyPass("Other writer", PassQueueSelection.Exact(data.Queue),
                    new ClearData(data.Saved, 71), default, DeclareClear, Clear, second);
            data.Execute(frame);
        }
        Assert.InRange(statistics.AnalyzedPassCount, 1, 3);
        Assert.Equal(new uint[] { 43, 43, 43, 43 }, data.Read());
    }

    [Fact]
    public void AddingAndRemovingPersistentPassesPreservesFrameRegionAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        GraphRegionId other;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            region = edit.AddFrameRegion("Write");
            other = edit.AddRegion("Other");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        GraphPassId added = default;
        for (int frameNumber = 0; frameNumber < 4; frameNumber++)
        {
            if (frameNumber == 2)
            {
                using RenderGraphEdit edit = data.Graph.BeginEdit();
                added = AddClear(edit, data.Queue, other, data.Saved, 79);
                edit.Commit();
            }
            if (frameNumber == 3)
            {
                using RenderGraphEdit edit = data.Graph.BeginEdit();
                edit.Remove(added);
                edit.Commit();
            }
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Clear", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 53), default, DeclareClear, Clear, region);
            data.Execute(frame);
            if (frameNumber >= 2) Assert.InRange(statistics.AnalyzedPassCount, 0, 1);
        }
        Assert.Equal(new uint[] { 53, 53, 53, 53 }, data.Read());
    }

    [Fact]
    public void ShrinkingAFrameRegionPreservesTheOtherRegionsAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId first;
        GraphRegionId second;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            first = edit.AddFrameRegion("Changing");
            second = edit.AddFrameRegion("Other");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 4; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            _ = frame.AddCopyPass("Write", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 61), default, DeclareClear, Clear, first);
            if (frameNumber < 2)
                _ = frame.AddCopyPass("Temporary writer", PassQueueSelection.Exact(data.Queue),
                    new ClearData(data.Values, 67), default, DeclareClear, Clear, first);
            for (int pass = 0; pass < 8; pass++)
                _ = frame.AddCopyPass("Other writer", PassQueueSelection.Exact(data.Queue),
                    new ClearData(data.Saved, 83), default, DeclareClear, Clear, second);
            data.Execute(frame);
            if (frameNumber == 2) Assert.InRange(statistics.AnalyzedPassCount, 1, 2);
        }
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(new uint[] { 61, 61, 61, 61 }, data.Read());
    }

    [Fact]
    public void FrameRegionPassesUseTheActiveFrame()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 7);
            region = edit.AddFrameRegion("Write");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphFrame first = data.Graph.BeginFrame();
        RenderGraphFrame earlier = first;
        data.Execute(first);
        using RenderGraphFrame current = data.Graph.BeginFrame();
        bool rejected = false;
        try
        {
            _ = earlier.AddCopyPass("Earlier frame", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 41), default, DeclareClear, Clear, region);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert.True(rejected);
        earlier.Dispose();
        _ = current.AddCopyPass("Current frame", PassQueueSelection.Exact(data.Queue),
            new ClearData(data.Values, 59), default, DeclareClear, Clear, region);
        data.Execute(current);
        Assert.Equal(new uint[] { 59, 59, 59, 59 }, data.Read());
    }

    [Fact]
    public void ParentRegionEnableChangesControlItsFramePasses()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId parent;
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, default, data.Values, 7);
            parent = edit.AddRegion("Feature");
            region = edit.AddFrameRegion("Write", parent);
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame();
            frame.SetRegionEnabled(parent, frameNumber != 1);
            _ = frame.AddCopyPass("Write", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 29), default, DeclareClear, Clear, region);
            data.Execute(frame);
            uint value = frameNumber == 1 ? 7u : 29u;
            Assert.Equal(new uint[] { value, value, value, value }, data.Read());
        }
    }

    [Fact]
    public void MatchingExplicitOrderWithinAFrameRegionReusesAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            region = edit.AddFrameRegion("Write");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            GraphPassId first = frame.AddCopyPass("First", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 19), default, DeclareClear, Clear, region);
            GraphPassId second = frame.AddCopyPass("Second", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 23), default, DeclareClear, Clear, region);
            frame.OrderAfter(second, first);
            data.Execute(frame);
        }
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(new uint[] { 23, 23, 23, 23 }, data.Read());
    }

    [Fact]
    public void DeepFrameRegionsKeepAllTheirPassesBeforeTheFollowingReader()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            GraphRegionId parent = default;
            for (int depth = 0; depth < 48; depth++)
            {
                _ = edit.AddRegion($"Earlier {depth}", parent);
                parent = edit.AddRegion($"Branch {depth}", parent);
            }
            region = edit.AddFrameRegion("Writes", parent);
            AddCopy(edit, data.Queue, edit.AddRegion("Read", parent), data.Values, data.Output);
            edit.Commit();
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            for (uint value = 1; value <= 16; value++)
                _ = frame.AddCopyPass("Write", PassQueueSelection.Exact(data.Queue),
                    new ClearData(data.Values, value), default, DeclareClear, Clear, region);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 16, 16, 16, 16 }, data.Read());
    }

    [Fact]
    public void DefaultFramePassesExecuteAfterThePersistentRegions()
    {
        using var data = new RegionTestBuffers();
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, edit.AddRegion("Write"), data.Values, 31);
            edit.Commit();
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            _ = frame.AddCopyPass("Overwrite", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 37), default, DeclareClear, Clear);
            _ = frame.AddCopyPass("Read", PassQueueSelection.Exact(data.Queue),
                new CopyData(data.Values, data.Output), default, DeclareCopy, Copy);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 37, 37, 37, 37 }, data.Read());
    }

    [Fact]
    public void FrameAccessesOnPersistentPassesReuseTheirAnalysis()
    {
        using var data = new RegionTestBuffers();
        GraphPassId reader;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            AddClear(edit, data.Queue, edit.AddRegion("Write"), data.Values, 31);
            reader = edit.AddCopyFramePass("Read", PassQueueSelection.Exact(data.Queue),
                new CopyData(data.Values, data.Output), default,
                static (ref PassDefinition pass, ref CopyData value) => { }, Copy,
                edit.AddRegion("Read"));
            edit.Commit();
        }
        RenderGraphStatistics statistics = default;
        for (int frameNumber = 0; frameNumber < 3; frameNumber++)
        {
            using RenderGraphFrame frame = data.Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            PassDefinition access = frame.GetPass(reader);
            _ = access.Read(data.Values, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = access.Write(data.Output, new BufferRange(0, 16), PipelineSync.Copy,
                ResourceAccess.CopyDestination, WriteCoverage.Complete);
            data.Execute(frame);
        }
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(new uint[] { 31, 31, 31, 31 }, data.Read());
    }

#if DEBUG
    [Fact]
    public void FrameRegionsAcceptPassesThroughTheFrameApi()
    {
        using var data = new RegionTestBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            region = edit.AddFrameRegion("Writes");
            AddCopy(edit, data.Queue, edit.AddRegion("Read"), data.Values, data.Output);
            edit.Commit();
        }
        bool rejected = false;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            try
            {
                AddClear(edit, data.Queue, region, data.Values, 7);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
        }
        Assert.True(rejected);
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            _ = frame.AddCopyPass("Write", PassQueueSelection.Exact(data.Queue),
                new ClearData(data.Values, 37), default, DeclareClear, Clear, region);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 37, 37, 37, 37 }, data.Read());
    }
#endif

    internal static GraphPassId AddClear(RenderGraphEdit edit, Queue queue,
        GraphRegionId region, GraphBufferId buffer, uint value) =>
        edit.AddCopyFramePass("Clear", PassQueueSelection.Exact(queue),
            new ClearData(buffer, value), default, DeclareClear, Clear, region);

    internal static GraphPassId AddCopy(RenderGraphEdit edit, Queue queue,
        GraphRegionId region, GraphBufferId source, GraphBufferId destination) =>
        edit.AddCopyFramePass("Copy", PassQueueSelection.Exact(queue),
            new CopyData(source, destination), default, DeclareCopy, Copy, region);

    internal static void DeclareClear(ref PassDefinition pass, ref ClearData data) =>
        _ = pass.Write(data.Buffer, new BufferRange(0, 16), PipelineSync.Copy,
            ResourceAccess.CopyDestination, WriteCoverage.Complete);

    internal static void Clear(ref CopyPassCommandScope commands, in ClearData data) =>
        commands.ClearBuffer(commands.GetBuffer(data.Buffer), new BufferRange(0, 16), data.Value);

    internal static void DeclareCopy(ref PassDefinition pass, ref CopyData data)
    {
        _ = pass.Read(data.Source, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
        _ = pass.Write(data.Destination, new BufferRange(0, 16), PipelineSync.Copy,
            ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }

    internal static void Copy(ref CopyPassCommandScope commands, in CopyData data) =>
        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
            commands.GetBuffer(data.Destination), 0, 16));

    internal readonly record struct ClearData(GraphBufferId Buffer, uint Value);
    internal readonly record struct CopyData(GraphBufferId Source, GraphBufferId Destination);
}

internal sealed class RegionTestBuffers : IDisposable
{
    private readonly WarpGraphTestSupport _support = WarpGraphTestSupport.Create();
    private readonly Buffer _readback;
    internal RenderGraph Graph { get; }
    internal Queue Queue => _support.GraphicsQueue;
    internal GraphBufferId Values { get; }
    internal GraphBufferId Saved { get; }
    internal GraphBufferId Output { get; }

    internal RegionTestBuffers()
    {
        _readback = _support.Backend.CreateBuffer(_support.Device,
            new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
        Graph = new RenderGraph(_support.Backend, _support.Device, [Queue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using RenderGraphEdit edit = Graph.BeginEdit();
        Values = edit.CreatePersistentBuffer(new BufferDesc(16,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        Saved = edit.CreatePersistentBuffer(new BufferDesc(16,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        Output = edit.RegisterExternalBuffer(_readback,
            [new(new BufferRange(0, 16), _readback.InitialSync, _readback.InitialAccess,
                ResourceContentState.Undefined, Queue)]);
        edit.Commit();
    }

    internal void Execute(RenderGraphFrame frame)
    {
        var completions = new QueueCompletion[Graph.MaximumQueueCompletionCount];
        int count = frame.Execute(completions);
        _support.Wait(completions.AsSpan(0, count));
    }

    internal RenderGraphStatistics Execute()
    {
        RenderGraphStatistics statistics = default;
        using RenderGraphFrame frame = Graph.BeginFrame(new RenderGraphFrameOptions(
            Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
        Execute(frame);
        return statistics;
    }

    internal uint[] Read()
    {
        using MappedBuffer mapping = _support.Backend.Map(_readback, MapType.Read, new BufferRange(0, 16));
        mapping.Invalidate(new BufferRange(0, 16));
        return MemoryMarshal.Cast<byte, uint>(mapping.Bytes).ToArray();
    }

    public void Dispose()
    {
        Graph.Dispose();
        _readback.Dispose();
        _support.Dispose();
    }
}
