using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PersistentRegionTests
{
    [Fact]
    public void LaterWriterInEarlierRegionSuppliesTheFollowingReader()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer readback = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphRegionId first;
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = edit.AddRegion("First");
            GraphRegionId second = edit.AddRegion("Second");
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(new BufferRange(0, 16), readback.InitialSync, readback.InitialAccess,
                    ResourceContentState.Undefined, support.GraphicsQueue)]);
            _ = edit.AddCopyPass<CopyData, byte>("Read", PassQueueSelection.Exact(support.GraphicsQueue),
                new CopyData(values, output), default,
                static (ref PassDefinition pass, ref CopyData data) =>
                {
                    _ = pass.Read(data.Source, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
                    _ = pass.Write(data.Destination, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data, in byte _) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 16)), region: second);
            edit.Commit();
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            _ = edit.AddCopyPass<GraphBufferId, byte>("Write", PassQueueSelection.Exact(support.GraphicsQueue),
                values, default,
                static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                    _ = pass.Write(buffer, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphBufferId buffer, in byte _) =>
                    commands.ClearBuffer(commands.GetBuffer(buffer), new BufferRange(0, 16), 37), region: first);
            edit.Commit();
        }
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
        using MappedBuffer mapping = support.Backend.Map(readback, MapType.Read, new BufferRange(0, 16));
        mapping.Invalidate(new BufferRange(0, 16));
        Assert.Equal(new uint[] { 37, 37, 37, 37 }, MemoryMarshal.Cast<byte, uint>(mapping.Bytes).ToArray());
    }

    [Fact]
    public void NestedRegionsExecuteInTreeOrder()
    {
        using var data = new RegionBuffers();
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            GraphRegionId tree = edit.AddRegion("Tree");
            GraphRegionId first = edit.AddRegion("First", tree);
            GraphRegionId branch = edit.AddRegion("Branch", tree);
            GraphRegionId save = edit.AddRegion("Save", branch);
            GraphRegionId overwrite = edit.AddRegion("Overwrite", branch);
            GraphRegionId restore = edit.AddRegion("Restore", tree);
            GraphRegionId output = edit.AddRegion("Output");
            data.Copy(edit, output, data.Values, data.Output);
            data.Copy(edit, restore, data.Saved, data.Values);
            data.Clear(edit, overwrite, data.Values, 22);
            data.Copy(edit, save, data.Values, data.Saved);
            data.Clear(edit, first, data.Values, 11);
            edit.Commit();
        }
        data.Execute();
        Assert.Equal(new uint[] { 11, 11, 11, 11 }, data.Read());
    }

    [Fact]
    public void RemovingARegionRemovesItsDescendantsAndTheirAccesses()
    {
        using var data = new RegionBuffers();
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, default, data.Values, 7);
            tree = edit.AddRegion("Tree");
            GraphRegionId child = edit.AddRegion("Child", tree);
            data.Clear(edit, child, data.Values, 9);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        data.Execute();
        Assert.Equal(new uint[] { 9, 9, 9, 9 }, data.Read());
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            edit.Remove(tree);
            edit.Commit();
        }
        data.Execute();
        Assert.Equal(new uint[] { 7, 7, 7, 7 }, data.Read());
    }

    [Fact]
    public void DiscardingRegionDeletionKeepsTheCommittedTree()
    {
        using var data = new RegionBuffers();
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            tree = edit.AddRegion("Tree");
            data.Clear(edit, edit.AddRegion("Child", tree), data.Values, 19);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
            edit.Remove(tree);
        data.Execute();
        Assert.Equal(new uint[] { 19, 19, 19, 19 }, data.Read());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RegionEnableChangesControlTheWholeSubtreeAcrossFrames(int framesInFlight)
    {
        using var data = new RegionBuffers(framesInFlight);
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, default, data.Values, 7);
            tree = edit.AddRegion("Tree");
            data.Clear(edit, edit.AddRegion("Child", tree), data.Values, 9);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        data.Execute();
        Assert.Equal(new uint[] { 9, 9, 9, 9 }, data.Read());
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.SetRegionEnabled(tree, false);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 7, 7, 7, 7 }, data.Read());
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.SetRegionEnabled(tree, true);
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 9, 9, 9, 9 }, data.Read());
    }

    [Fact]
    public void UnchangedFramesReuseAnalysisAndRebuildZeroRows()
    {
        using var data = new RegionBuffers();
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, edit.AddRegion("Write"), data.Values, 31);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        data.Execute();
        data.Execute();
        RenderGraphStatistics statistics = data.Execute();
        Assert.True(statistics.AnalysisReused);
        Assert.Equal(0, statistics.AnalyzedPassCount);
        Assert.Equal(0, statistics.RebuiltRowCount);
    }

    [Fact]
    public void RepeatedRegionGrowthKeepsResultsAndLimitsAnalysisToItsResourceUsers()
    {
        using var data = new RegionBuffers();
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            tree = edit.AddRegion("Growing tree");
            data.Clear(edit, tree, data.Values, 55);
            GraphRegionId other = edit.AddRegion("Other");
            for (int index = 0; index < 8; index++)
                data.Clear(edit, other, data.Saved, 99);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        data.Execute();
        data.Execute();
        bool analyzedTree = false;
        for (int batch = 0; batch < 8; batch++)
        {
            using (RenderGraphEdit edit = data.Graph.BeginEdit())
            {
                for (int child = 0; child < 16; child++)
                    _ = edit.AddRegion($"Child {batch * 16 + child}", tree);
                edit.Commit();
            }
            RenderGraphStatistics statistics = data.Execute();
            analyzedTree |= statistics.AnalyzedPassCount > 0;
            Assert.InRange(statistics.AnalyzedPassCount, 0, 2);
            Assert.Equal(new uint[] { 55, 55, 55, 55 }, data.Read());
        }
        Assert.True(analyzedTree);
    }

    [Fact]
    public void PassesInLaterChildRegionsParticipateThroughoutRepeatedGrowth()
    {
        using var data = new RegionBuffers();
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            tree = edit.AddRegion("Tree");
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        for (int batch = 0; batch < 8; batch++)
        {
            using (RenderGraphEdit edit = data.Graph.BeginEdit())
            {
                for (int child = 0; child < 16; child++)
                {
                    GraphRegionId region = edit.AddRegion($"Child {batch * 16 + child}", tree);
                    data.Clear(edit, region, data.Values, (uint)(batch * 16 + child + 1));
                }
                edit.Commit();
            }
            data.Execute();
            uint value = (uint)((batch + 1) * 16);
            Assert.Equal(new uint[] { value, value, value, value }, data.Read());
        }
    }

    [Fact]
    public void NestedPassesInLaterChildRegionsParticipateThroughoutRepeatedGrowth()
    {
        using var data = new RegionBuffers();
        GraphRegionId tree;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            tree = edit.AddRegion("Tree");
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        for (int batch = 0; batch < 8; batch++)
        {
            using (RenderGraphEdit edit = data.Graph.BeginEdit())
            {
                for (int child = 0; child < 16; child++)
                {
                    GraphRegionId region = edit.AddRegion($"Child {batch * 16 + child}", tree);
                    GraphRegionId nested = edit.AddRegion("Nested", region);
                    data.Clear(edit, nested, data.Values, (uint)(batch * 16 + child + 1));
                }
                edit.Commit();
            }
            data.Execute();
            uint value = (uint)((batch + 1) * 16);
            Assert.Equal(new uint[] { value, value, value, value }, data.Read());
        }
    }

    [Fact]
    public void RegionEnableChangesPreserveTheStructureVersion()
    {
        using var data = new RegionBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            region = edit.AddRegion("Tree");
            edit.Commit();
        }
        ulong version = data.Graph.StructureVersion;
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.SetRegionEnabled(region, false);
            data.Execute(frame);
        }
        Assert.Equal(version, data.Graph.StructureVersion);
    }

    [Fact]
    public void CommittedRegionDeletionAppliesToSubsequentFrames()
    {
        using var data = new RegionBuffers();
        GraphRegionId region;
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, default, data.Values, 7);
            region = edit.AddRegion("Tree");
            data.Clear(edit, edit.AddRegion("Child", region), data.Values, 9);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            using (RenderGraphEdit edit = data.Graph.BeginEdit())
            {
                edit.Remove(region);
                edit.Commit();
            }
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 9, 9, 9, 9 }, data.Read());
        data.Execute();
        Assert.Equal(new uint[] { 7, 7, 7, 7 }, data.Read());
    }

    [Fact]
    public void DisablingTheRootRegionControlsItsFramePasses()
    {
        using var data = new RegionBuffers();
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, default, data.Values, 37);
            data.Copy(edit, default, data.Values, data.Output);
            edit.Commit();
        }
        data.Execute();
        using (RenderGraphFrame frame = data.Graph.BeginFrame())
        {
            frame.SetRegionEnabled(default, false);
            _ = frame.AddCopyPass("Root frame pass", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new ClearData(data.Output, 73), default,
                static (ref PassDefinition pass, ref ClearData clear) =>
                    _ = pass.Write(clear.Buffer, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in ClearData clear) =>
                    commands.ClearBuffer(commands.GetBuffer(clear.Buffer), new BufferRange(0, 16), clear.Value));
            data.Execute(frame);
        }
        Assert.Equal(new uint[] { 37, 37, 37, 37 }, data.Read());
    }

    [Fact]
    public void FirstFrameReportsPassAnalysisAndRowRebuilds()
    {
        using var data = new RegionBuffers();
        using (RenderGraphEdit edit = data.Graph.BeginEdit())
        {
            data.Clear(edit, edit.AddRegion("Write"), data.Values, 31);
            data.Copy(edit, edit.AddRegion("Output"), data.Values, data.Output);
            edit.Commit();
        }
        RenderGraphStatistics statistics = data.Execute();
        Assert.Equal(2, statistics.AnalyzedPassCount);
        Assert.Equal(8, statistics.RebuiltRowCount);
    }

    [Fact]
    public void ChangingAnAccessRangeReportsItsConsumerInAnalysis()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer upload = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(16, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapping = support.Backend.Map(upload, MapType.Write, new BufferRange(0, 16)))
        {
            mapping.Bytes.Clear();
            mapping.Flush(new BufferRange(0, 16));
        }
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferAccessId range = default;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.RegisterExternalBuffer(upload,
                [new(new BufferRange(0, 16), upload.InitialSync, upload.InitialAccess,
                    ResourceContentState.Defined, support.GraphicsQueue)]);
            _ = edit.AddCopyFramePass("Read range", PassQueueSelection.AnyOfType(QueueType.Graphics), buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref GraphBufferId source) =>
                    range = pass.Read(source, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopySource, dynamicRange: true),
                static (ref CopyPassCommandScope _, in GraphBufferId _) => { });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetBufferRange(range, new BufferRange(0, 16));
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
        RenderGraphStatistics statistics = default;
        using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(
            Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics)))
        {
            frame.SetBufferRange(range, new BufferRange(0, 8));
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
        }
        Assert.Equal(1, statistics.AnalyzedPassCount);
    }

    private sealed class RegionBuffers : IDisposable
    {
        private readonly WarpGraphTestSupport _support = WarpGraphTestSupport.Create();
        private readonly Buffer _readback;
        internal RenderGraph Graph { get; }
        internal GraphBufferId Values { get; }
        internal GraphBufferId Saved { get; }
        internal GraphBufferId Output { get; }

        internal RegionBuffers(int framesInFlight = 1)
        {
            _readback = _support.Backend.CreateBuffer(_support.Device,
                new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
            Graph = new RenderGraph(_support.Backend, _support.Device, [_support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
            using RenderGraphEdit edit = Graph.BeginEdit();
            Values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            Saved = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            Output = edit.RegisterExternalBuffer(_readback,
                [new(new BufferRange(0, 16), _readback.InitialSync, _readback.InitialAccess,
                    ResourceContentState.Undefined, _support.GraphicsQueue)]);
            edit.Commit();
        }

        internal GraphPassId Clear(RenderGraphEdit edit, GraphRegionId region, GraphBufferId buffer, uint value) =>
            edit.AddCopyFramePass("Clear", PassQueueSelection.Exact(_support.GraphicsQueue),
                new ClearData(buffer, value), default,
                static (ref PassDefinition pass, ref ClearData data) =>
                    _ = pass.Write(data.Buffer, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in ClearData data) =>
                    commands.ClearBuffer(commands.GetBuffer(data.Buffer), new BufferRange(0, 16), data.Value), region);

        internal GraphPassId Copy(RenderGraphEdit edit, GraphRegionId region,
            GraphBufferId source, GraphBufferId destination) =>
            edit.AddCopyFramePass("Copy", PassQueueSelection.Exact(_support.GraphicsQueue),
                new CopyData(source, destination), default,
                static (ref PassDefinition pass, ref CopyData data) =>
                {
                    _ = pass.Read(data.Source, new BufferRange(0, 16), PipelineSync.Copy, ResourceAccess.CopySource);
                    _ = pass.Write(data.Destination, new BufferRange(0, 16), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data) =>
                    commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 16)), region);

        internal RenderGraphStatistics Execute()
        {
            RenderGraphStatistics statistics = default;
            using RenderGraphFrame frame = Graph.BeginFrame(new RenderGraphFrameOptions(
                Diagnostics: (in RenderGraphDiagnosticsView view) => statistics = view.Statistics));
            Execute(frame);
            return statistics;
        }

        internal void Execute(RenderGraphFrame frame)
        {
            var completions = new QueueCompletion[Graph.MaximumQueueCompletionCount];
            int count = frame.Execute(completions);
            _support.Wait(completions.AsSpan(0, count));
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

    private readonly record struct ClearData(GraphBufferId Buffer, uint Value);

    private readonly record struct CopyData(GraphBufferId Source, GraphBufferId Destination);
}
