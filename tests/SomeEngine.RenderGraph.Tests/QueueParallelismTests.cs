
namespace SomeEngine.RenderGraph.Tests;

public sealed class QueueParallelismTests
{
    [Fact]
    public void IndependentCompatiblePassesUseAvailableQueuesAfterCostChanges()
    {
        using var support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue]);
        GraphPassId first, second;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            var producer = AddEmpty(edit, "fork source", PassQueueSelection.Exact(support.GraphicsQueue), 1);
            first = AddEmpty(edit, "first branch", PassQueueSelection.AnyCompatible(), 16);
            second = AddEmpty(edit, "second branch", PassQueueSelection.AnyCompatible(), 16);
            edit.OrderAfter(first, producer);
            edit.OrderAfter(second, producer);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            if (frameIndex == 1)
            {
                using var edit = graph.BeginEdit();
                edit.SetPassOptions(second, new(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: 17));
                edit.Commit();
            }
            Queue? firstQueue = null, secondQueue = null;
            using var frame = graph.BeginFrame(new(Diagnostics: Capture));
            frame.Execute(completions);
            support.Wait(completions);
            Assert.NotNull(firstQueue);
            Assert.NotNull(secondQueue);
            Assert.NotSame(firstQueue, secondQueue);

            void Capture(in RenderGraphDiagnosticsView view)
            {
                foreach (var pass in view.Passes)
                {
                    if (pass.Id == first) firstQueue = pass.Queue;
                    if (pass.Id == second) secondQueue = pass.Queue;
                }
            }
        }
    }

    [Fact]
    public void CrossQueueConsumerCompletesBeforeLaterProducerQueueWorkIsReleased()
    {
        using var support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines, graphicsQueueCount: 2);
        Queue releaseQueue = support.Backend.GetQueue(support.Device, QueueType.Graphics, 1);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var gateBytes = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using var result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue]);
        GraphBufferInput input;
        GraphPassId consumer;
        using (var edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [Endpoint(result, support.ComputeQueue, ResourceContentState.Undefined)]);
            var producer = AddEmpty(edit, "early graphics source", PassQueueSelection.Exact(support.GraphicsQueue), 1);
            consumer = AddClear(edit, "compute consumer", support.ComputeQueue, output, 42);
            edit.OrderAfter(consumer, producer);
            AddRead(edit, "later graphics gate", support.GraphicsQueue, input);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (ulong value = 1; value <= 2; value++)
        {
            var blocked = support.Backend.Submit(support.CopyQueue, new([], [new(gate, value)], [], [], []));
            var observed = new PassCompletion(consumer);
            try
            {
                using var frame = graph.BeginFrame();
                GraphBufferId source = frame.Import(gateBytes,
                    [new(BufferRange.Whole, gateBytes.InitialSync, gateBytes.InitialAccess,
                        ResourceContentState.Defined, support.CopyQueue, blocked)]);
                frame.SetBufferInput(input, new(source, BufferRange.Whole));
                frame.ObserveExecution(observed);
                frame.Execute(completions);
                Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(observed.Completion, TimeSpan.FromSeconds(10)));
                using var mapped = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
                mapped.Invalidate(mapped.Range);
                Assert.Equal(42u, BitConverter.ToUInt32(mapped.Bytes));
            }
            finally
            {
                support.Backend.Submit(releaseQueue, new([], [], [], [], [new(gate, value)]));
                support.Wait(completions);
            }
        }
    }

    [Fact]
    public void EarlierWorkCompletesBeforeALaterCrossQueueWaitIsReleased()
    {
        using var support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines, graphicsQueueCount: 2);
        Queue releaseQueue = support.Backend.GetQueue(support.Device, QueueType.Graphics, 1);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var gateBytes = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using var result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.ComputeQueue]);
        GraphBufferInput input;
        GraphPassId earlier, delayed, later;
        using (var edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            GraphBufferId output = edit.RegisterExternalBuffer(result,
                [Endpoint(result, support.GraphicsQueue, ResourceContentState.Undefined)]);
            earlier = AddClear(edit, "independent graphics output", support.GraphicsQueue, output, 73);
            delayed = AddRead(edit, "delayed compute work", support.ComputeQueue, input);
            later = AddEmpty(edit, "graphics consumer", PassQueueSelection.Exact(support.GraphicsQueue), 1);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (ulong value = 1; value <= 2; value++)
        {
            if (value == 2)
            {
                using var edit = graph.BeginEdit();
                edit.OrderAfter(later, delayed);
                edit.Commit();
            }
            var blocked = support.Backend.Submit(support.CopyQueue, new([], [new(gate, value)], [], [], []));
            var observed = new PassCompletion(earlier);
            try
            {
                using var frame = graph.BeginFrame();
                GraphBufferId source = frame.Import(gateBytes,
                    [new(BufferRange.Whole, gateBytes.InitialSync, gateBytes.InitialAccess,
                        ResourceContentState.Defined, support.CopyQueue, blocked)]);
                frame.SetBufferInput(input, new(source, BufferRange.Whole));
                frame.ObserveExecution(observed);
                frame.Execute(completions);
                Assert.Equal(WaitStatus.Completed, support.Backend.WaitCpu(observed.Completion, TimeSpan.FromSeconds(10)));
                using var mapped = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
                mapped.Invalidate(mapped.Range);
                Assert.Equal(73u, BitConverter.ToUInt32(mapped.Bytes));
            }
            finally
            {
                support.Backend.Submit(releaseQueue, new([], [], [], [], [new(gate, value)]));
                support.Wait(completions);
            }
        }
    }

    private static BufferBoundaryState Endpoint(Buffer buffer, Queue queue, ResourceContentState contents) =>
        new(BufferRange.Whole, buffer.InitialSync, buffer.InitialAccess, contents, queue);

    private static GraphPassId AddEmpty(RenderGraphEdit edit, string label, PassQueueSelection queue, uint cost) =>
        edit.AddCopyFramePass(label, queue, 0, new(Culling: PassCullingMode.NeverCull, EstimatedExecutionCost: cost),
            static (ref PassDefinition _, ref int _) => { }, static (ref CopyPassCommandScope _, in int _) => { });

    private static GraphPassId AddRead(RenderGraphEdit edit, string label, Queue queue, GraphBufferInput input) =>
        edit.AddCopyFramePass(label, PassQueueSelection.Exact(queue), input, new(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref GraphBufferInput source) =>
                pass.Read(source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource),
            static (ref CopyPassCommandScope _, in GraphBufferInput _) => { });

    private static GraphPassId AddClear(RenderGraphEdit edit, string label, Queue queue, GraphBufferId output, uint value) =>
        edit.AddCopyFramePass(label, PassQueueSelection.Exact(queue), (output, value), new(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphBufferId Buffer, uint Value) data) =>
                pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in (GraphBufferId Buffer, uint Value) data) =>
                commands.ClearBuffer(commands.GetBuffer(data.Buffer), BufferRange.Whole, data.Value));

    private sealed class PassCompletion(GraphPassId pass) : IRenderGraphFrameExecutionObserver
    {
        internal QueueCompletion Completion;
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) =>
            result.TryGetPassCompletion(pass, out Completion);
    }
}
