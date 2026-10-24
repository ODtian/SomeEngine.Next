using System.Runtime.InteropServices;
using SomeEngine.Diagnostics;
using SomeEngine.Trace;
using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class QueueSubmissionTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined, false)]
    [InlineData(FrameSubmissionMode.Pipelined, true)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, false)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, true)]
    [InlineData(FrameSubmissionMode.RecordAhead, false)]
    [InlineData(FrameSubmissionMode.RecordAhead, true)]
    public void SubmissionMergingPreservesQueueDependenciesAndReadback(
        FrameSubmissionMode mode, bool crossesBack)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Queue[] queues = [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue, support.GraphicsQueue];
        var outputs = new List<Buffer>();
        string path = Path.Combine(Path.GetTempPath(), $"graph-merging-{Guid.NewGuid():N}.setrace");
        try
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                var passes = new GraphPassId[4];
                for (int index = 0; index < 4; index++)
                {
                    Buffer output = support.Backend.CreateBuffer(support.Device,
                        new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
                    outputs.Add(output);
                    GraphBufferId resource = edit.RegisterExternalBuffer(output,
                        [Endpoint(output, queues[index], ResourceContentState.Undefined)]);
                    passes[index] = AddClear(edit, $"merge output {index}", queues[index], resource, (uint)(index + 1));
                }
                edit.OrderAfter(passes[1], passes[0]);
                edit.OrderAfter(passes[3], passes[crossesBack ? 1 : 2]);
                edit.Commit();
            }
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            var options = new RenderGraphFrameOptions(mode,
                RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
            using (EngineTrace.Start(path, new TraceOptions { Detail = true, BufferMegabytes = 4 }))
            {
                for (int frameIndex = 1; frameIndex <= 2; frameIndex++)
                {
                    using var trace = EngineTrace.BeginFrame(frameIndex);
                    using RenderGraphFrame frame = graph.BeginFrame(options);
                    int count = frame.Execute(completions);
                    support.Wait(completions.AsSpan(0, count));
                    for (int index = 0; index < outputs.Count; index++)
                    {
                        using MappedBuffer mapped = support.Backend.Map(outputs[index], MapType.Read, new(0, 64));
                        mapped.Invalidate(new(0, 64));
                        foreach (uint value in MemoryMarshal.Cast<byte, uint>(mapped.Bytes))
                            Assert.Equal((uint)(index + 1), value);
                    }
                }
            }
            TraceData data = TraceFile.Read(path);
            Assert.Equal(0, data.Misuse);
            Assert.All(data.Threads, thread => Assert.Equal(0, thread.Dropped));
            int submitSite = data.Sites.Single(site => site.Name == "ID3D12CommandQueue.ExecuteCommandLists").Id;
            for (int frameIndex = 1; frameIndex <= 2; frameIndex++)
                Assert.Equal(crossesBack ? 4 : 3, data.Events.Count(value =>
                    value.Event.Frame == frameIndex && value.Event.Kind == TraceEventKind.Begin &&
                    value.Event.Site == submitSite));
        }
        finally
        {
            foreach (Buffer output in outputs) output.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void RemovingAndRestoringQueueDependenciesUpdatesSubmissionCountAndReadback(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue], new RenderGraphDesc(MaximumFramesInFlight: 3));
        var outputs = new List<Buffer>();
        var passes = new GraphPassId[3];
        Queue[] queues = [support.GraphicsQueue, support.ComputeQueue, support.GraphicsQueue];
        string path = Path.Combine(Path.GetTempPath(), $"graph-queue-changes-{Guid.NewGuid():N}.setrace");
        try
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                for (int index = 0; index < passes.Length; index++)
                {
                    Buffer output = support.Backend.CreateBuffer(support.Device,
                        new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
                    outputs.Add(output);
                    GraphBufferId resource = edit.RegisterExternalBuffer(output,
                        [Endpoint(output, queues[index], ResourceContentState.Undefined)]);
                    passes[index] = AddClear(edit, $"queue change output {index}", queues[index], resource, (uint)(index + 1));
                }
                edit.OrderAfter(passes[1], passes[0]);
                edit.OrderAfter(passes[2], passes[1]);
                edit.Commit();
            }
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            var options = new RenderGraphFrameOptions(mode,
                RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
            using (EngineTrace.Start(path, new TraceOptions { Detail = true, BufferMegabytes = 4 }))
            {
                for (int frameIndex = 1; frameIndex <= 9; frameIndex++)
                {
                    if (frameIndex is 4 or 7)
                    {
                        using RenderGraphEdit edit = graph.BeginEdit();
                        if (frameIndex == 4)
                        {
                            edit.RemoveOrder(passes[1], passes[0]);
                            edit.RemoveOrder(passes[2], passes[1]);
                        }
                        else
                        {
                            edit.OrderAfter(passes[1], passes[0]);
                            edit.OrderAfter(passes[2], passes[1]);
                        }
                        edit.Commit();
                    }
                    using var trace = EngineTrace.BeginFrame(frameIndex);
                    using RenderGraphFrame frame = graph.BeginFrame(options);
                    int count = frame.Execute(completions);
                    support.Wait(completions.AsSpan(0, count));
                    for (int index = 0; index < outputs.Count; index++)
                    {
                        using MappedBuffer mapped = support.Backend.Map(outputs[index], MapType.Read, new(0, 64));
                        mapped.Invalidate(new(0, 64));
                        foreach (uint value in MemoryMarshal.Cast<byte, uint>(mapped.Bytes))
                            Assert.Equal((uint)(index + 1), value);
                    }
                }
            }
            TraceData data = TraceFile.Read(path);
            Assert.Equal(0, data.Misuse);
            Assert.All(data.Threads, thread => Assert.Equal(0, thread.Dropped));
            int submitSite = data.Sites.Single(site => site.Name == "ID3D12CommandQueue.ExecuteCommandLists").Id;
            for (int frameIndex = 1; frameIndex <= 9; frameIndex++)
                Assert.Equal(frameIndex is >= 4 and <= 6 ? 2 : 3,
                    data.Events.Count(value => value.Event.Frame == frameIndex && value.Event.Kind == TraceEventKind.Begin &&
                        value.Event.Site == submitSite));
        }
        finally
        {
            foreach (Buffer output in outputs) output.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SameQueueDependenciesInheritCrossQueueWavesAndCompletionsAreCompact()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer readback = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId gate = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId value = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId readbackId = edit.RegisterExternalBuffer(
                readback,
                [Endpoint(readback, support.CopyQueue, ResourceContentState.Undefined)]);
            _ = AddClear(edit, "wave gate", support.CopyQueue, gate, 1);
            GraphPassId delayed = edit.AddCopyPass<DependentClearState, byte>(
                "cross-queue delayed clear",
                PassQueueSelection.Exact(support.ComputeQueue),
                new DependentClearState(gate, value, 333),
                default,
                static (ref PassDefinition access, ref DependentClearState state) =>
                {
                    _ = access.Read(
                        state.Gate,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopySource);
                    _ = access.Write(
                        state.Buffer,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands,
                    in DependentClearState state,
                    in byte _) => commands.ClearBuffer(
                        commands.GetBuffer(state.Buffer),
                        new BufferRange(0, 64),
                        state.Value));
            _ = AddClear(edit, "same-queue successor", support.ComputeQueue, value, 444);
            GraphPassId observe = AddCopy(
                edit,
                "observe wave order",
                support.CopyQueue,
                new CopyState(value, readbackId, 64));
            edit.OrderAfter(observe, delayed);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        Assert.Equal(2, submitted);
        Assert.NotEqual(default, completions[0]);
        Assert.NotEqual(default, completions[1]);
        Assert.Equal(default, completions[2]);
        support.Wait(completions.AsSpan(0, submitted));

        using MappedBuffer mapping = support.Backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, 64));
        mapping.Invalidate(new BufferRange(0, 64));
        Assert.Equal(444u, BitConverter.ToUInt32(mapping.Bytes));
    }

    [Fact]
    public void PartialSubmissionTransfersAcceptedCompletionsToFrameSlot()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.DeclareCopyPass("accepted first wave", PassQueueSelection.Exact(support.CopyQueue),
                (ref PassDefinition access) =>
                    _ = access.Write(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete));
            _ = edit.AddCopyPass<int, byte>(
                "failing second wave",
                PassQueueSelection.Exact(support.GraphicsQueue),
                0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref int state) =>
                    _ = access.Read(buffer, new BufferRange(0, 64),
                        PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in int _, in byte _) =>
                    throw new InvalidOperationException("injected callback failure"));
            edit.Commit();
        }

        QueueCompletion[] completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using RenderGraphFrame frame = graph.BeginFrame();
        bool failed = false;
        try { _ = frame.Execute(completions); }
        catch (InvalidOperationException) { failed = true; }
        Assert.True(failed);
        QueueCompletion accepted = Assert.Single(completions, value => value != default);
        support.Wait([accepted]);
    }

    [Fact]
    public void ExecuteCompactsCompletionsForNonContiguousUsedQueueSlots()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId graphicsBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            GraphBufferId copyBuffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination));
            AddClear(edit, "graphics clear", support.GraphicsQueue, graphicsBuffer, 1);
            AddClear(edit, "copy clear", support.CopyQueue, copyBuffer, 2);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count;
        using (RenderGraphFrame frame = graph.BeginFrame())
            count = frame.Execute(completions);

        Assert.Equal(2, count);
        Assert.Same(support.GraphicsQueue, completions[0].Queue);
        Assert.Same(support.CopyQueue, completions[1].Queue);
        support.Wait(completions.AsSpan(0, count));
    }
}
