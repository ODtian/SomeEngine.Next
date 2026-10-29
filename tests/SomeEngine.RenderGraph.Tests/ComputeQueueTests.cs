using SomeEngine.Graphics;
using System.Runtime.InteropServices;
using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ComputeQueueTests
{
    [Theory]
    [InlineData(2, QueueType.Graphics)]
    [InlineData(5, QueueType.Compute)]
    public void ComputeChainsChooseQueuesFromTheirTotalCostAndBoundaryWork(int count, QueueType expectedType)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using Buffer background = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        var compute = new GraphPassId[count];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.CreateBuffer(new(64, BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId backgroundOutput = edit.RegisterExternalBuffer(background,
                [Endpoint(background, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [Endpoint(result, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphPassId produce = AddClear(edit, "graphics produces compute input", support.GraphicsQueue, source, 7);
            GraphPassId busy = AddClear(edit, "independent graphics work", support.GraphicsQueue, backgroundOutput, 9);
            edit.SetPassOptions(busy, new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 64));
            edit.OrderAfter(busy, produce);
            for (int index = 0; index < count; index++)
            {
                GraphBufferId destination = edit.CreateBuffer(new(64, BufferUsages.CopySource | BufferUsages.CopyDestination));
                compute[index] = edit.AddComputeFramePass($"compute copy {index}", PassQueueSelection.AnyCompatible(),
                    new CopyState(source, destination, 64), default, DeclareCopy,
                    static (ref ComputePassCommandScope commands, in CopyState data) =>
                        commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Destination), 0, data.Size)));
                source = destination;
            }
            GraphPassId consume = AddCopy(edit, "graphics consumes compute output", support.GraphicsQueue,
                new CopyState(source, output, 64));
            edit.OrderAfter(consume, busy);
            edit.Commit();
        }
        var selected = new Queue?[count];
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: CaptureQueues))) frame.Execute(completions);
        support.Wait(completions);
        Queue expected = expectedType == QueueType.Graphics ? support.GraphicsQueue : support.ComputeQueue;
        Assert.All(selected, queue => Assert.Same(expected, queue));
        using MappedBuffer mapped = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.All(MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray(), value => Assert.Equal(7u, value));

        void CaptureQueues(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphPassDiagnostic pass in diagnostics.Passes)
            {
                int index = Array.IndexOf(compute, pass.Id);
                if (index >= 0) selected[index] = pass.Queue;
            }
        }
    }

    [Fact]
    public void AddedComputeDependencyKeepsEarlierGraphicsWorkRunnable()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer graphicsBytes = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer computeBytes = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphPassId graphics, compute, consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId graphicsOutput = edit.RegisterExternalBuffer(graphicsBytes,
                [Endpoint(graphicsBytes, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphBufferId computeOutput = edit.RegisterExternalBuffer(computeBytes,
                [Endpoint(computeBytes, support.ComputeQueue, ResourceContentState.Undefined)]);
            graphics = AddClear(edit, "earlier graphics output", support.GraphicsQueue, graphicsOutput, 1);
            compute = AddClear(edit, "compute output", support.ComputeQueue, computeOutput, 2);
            consumer = AddClear(edit, "later graphics output", support.GraphicsQueue, graphicsOutput, 3);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame()) frame.Execute(completions);
        support.Wait(completions);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(consumer, compute);
            edit.Commit();
        }

        using ExternalTimeline gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        QueueCompletion blocked = support.Backend.Submit(support.ComputeQueue,
            new QueueSubmitDesc([], [new TimelinePoint(gate, 1)], [], [], []));
        var receiver = new ComputeQueueCompletions(graphics, compute);
        Array.Clear(completions);
        try
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(receiver);
            frame.Execute(completions);
            Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(receiver.Graphics, TimeSpan.FromSeconds(10)));
            Assert.False(support.Backend.IsComplete(blocked));
            using MappedBuffer prefix = support.Backend.Map(graphicsBytes, MapType.Read, BufferRange.Whole);
            prefix.Invalidate(prefix.Range);
            Assert.All(MemoryMarshal.Cast<byte, uint>(prefix.Bytes).ToArray(), value => Assert.Equal(1u, value));
        }
        finally
        {
            QueueCompletion released = support.Backend.Submit(support.CopyQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, 1)]));
            support.Wait([released]);
            support.Wait(completions);
            support.Wait([blocked]);
        }
        using MappedBuffer completed = support.Backend.Map(graphicsBytes, MapType.Read, BufferRange.Whole);
        completed.Invalidate(completed.Range);
        Assert.All(MemoryMarshal.Cast<byte, uint>(completed.Bytes).ToArray(), value => Assert.Equal(3u, value));
    }

    [Fact]
    public void GraphicsPrefixCompletesWhileCompatibleComputeWaitsOnItsQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using Buffer graphicsBytes = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId graphics, compute;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId intermediate = edit.CreateBuffer(new(32,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId graphicsOutput = edit.RegisterExternalBuffer(graphicsBytes,
                [new(BufferRange.Whole, graphicsBytes.InitialSync, graphicsBytes.InitialAccess,
                    ResourceContentState.Undefined)]);
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            graphics = edit.AddCopyFramePass("independent graphics prefix", PassQueueSelection.Exact(support.GraphicsQueue),
                (input, graphicsOutput), new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 64),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Output), 0, 32));
                });
            compute = edit.AddComputeFramePass("independent compatible compute", PassQueueSelection.AnyCompatible(),
                (input, intermediate), new PassOptions(EstimatedExecutionCost: 8, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref ComputePassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Output) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset, commands.GetBuffer(data.Output), 0, 32));
                });
            GraphPassId consumer = edit.AddCopyFramePass("graphics consumes compute", PassQueueSelection.Exact(support.GraphicsQueue),
                (intermediate, output), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Output) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Output) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0, commands.GetBuffer(data.Output), 0, 32)));
            edit.OrderAfter(consumer, graphics);
            edit.Commit();
        }

        using ExternalTimeline gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        QueueCompletion blocked = support.Backend.Submit(support.ComputeQueue,
            new QueueSubmitDesc([], [new TimelinePoint(gate, 1)], [], [], []));
        var receiver = new ComputeQueueCompletions(graphics, compute);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        byte[] expected = Enumerable.Range(0, 32).Select(index => (byte)(index * 7)).ToArray();
        try
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
            frame.ObserveExecution(receiver);
            frame.Execute(completions);
            Assert.Same(support.ComputeQueue, receiver.Compute.Queue);
            Assert.Same(support.GraphicsQueue, receiver.Graphics.Queue);
            Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(receiver.Graphics, TimeSpan.FromSeconds(10)));
            Assert.False(support.Backend.IsComplete(blocked));
        }
        finally
        {
            QueueCompletion released = support.Backend.Submit(support.CopyQueue,
                new QueueSubmitDesc([], [], [], [], [new TimelineSignal(gate, 1)]));
            support.Wait([released]);
            support.Wait(completions);
            support.Wait([blocked]);
        }
        using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        Assert.Equal(expected, mapping.Bytes.ToArray());
    }

    [Theory]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void ChangingCompatibleComputeToGraphicsPreservesEarlierFrames(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(32, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        GraphPassId consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId intermediate = edit.CreateBuffer(new(32,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddCopyFramePass("produce on compute", PassQueueSelection.AnyOfType(QueueType.Compute),
                (input, intermediate), default,
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphBufferId Buffer) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBuffer(new(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Buffer), 0, 32));
                });
            consumer = edit.AddComputeFramePass("compatible compute commands", PassQueueSelection.AnyCompatible(),
                (intermediate, destination), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref ComputePassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0,
                        commands.GetBuffer(data.Destination), 0, 32)));
            edit.Commit();
        }

        Queue? selected = null;
        var options = new RenderGraphFrameOptions(SubmissionMode: mode, Diagnostics: CaptureQueue);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(options);
            Execute(frame, iteration, support.ComputeQueue);
        }

        using (RenderGraphFrame earlier = graph.BeginFrame(options))
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                edit.SetPassQueue(consumer, PassQueueSelection.AnyOfType(QueueType.Graphics));
                edit.Commit();
            }
            Execute(earlier, 4, support.ComputeQueue);
        }
        for (int iteration = 5; iteration < 10; iteration++)
        {
            using RenderGraphFrame frame = graph.BeginFrame(options);
            Execute(frame, iteration, support.GraphicsQueue);
        }

        ulong version = graph.StructureVersion;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.SetPassQueue(consumer, PassQueueSelection.AnyOfType(QueueType.Graphics));
            edit.Commit();
        }
        Assert.Equal(version, graph.StructureVersion);

        void Execute(RenderGraphFrame frame, int iteration, Queue expectedQueue)
        {
            selected = null;
            byte[] expected = Enumerable.Range(0, 32).Select(index => (byte)(index * 7 + iteration)).ToArray();
            frame.SetBufferInput(input, frame.Upload(expected, BufferUsages.CopySource));
            int count = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, count));
            Assert.Same(expectedQueue, selected);
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(expected, mapping.Bytes.ToArray());
        }

        void CaptureQueue(in RenderGraphDiagnosticsView diagnostics)
        {
            foreach (RenderGraphPassDiagnostic pass in diagnostics.Passes)
                if (pass.Id == consumer) selected = pass.Queue;
        }
    }
    private sealed class ComputeQueueCompletions(GraphPassId graphics, GraphPassId compute) : IRenderGraphFrameExecutionObserver
    {
        internal QueueCompletion Graphics;
        internal QueueCompletion Compute;
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            result.TryGetPassCompletion(graphics, out Graphics);
            result.TryGetPassCompletion(compute, out Compute);
        }
    }
}
