using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PersistentReadbackTests
{
    [Fact]
    public void ReadbackIsDeliveredOnceAfterFrameCompletionWithItsRecordedTag()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        var receiver = new ReadbackResults();
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddGeneralFramePass("Fill values", PassQueueSelection.AnyOfType(QueueType.Graphics),
                values, default,
                static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                    pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref GeneralPassCommandScope commands, in GraphBufferId buffer) =>
                    commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, 17));
            readback = edit.AddReadback(values, new BufferRange(4, 8), receiver, "Values",
                edit.AddRegion("Feedback"));
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                frame.SetReadbackTag(readback, 41);
                frame.Execute(completions);
            }
            using (RenderGraphFrame frame = graph.BeginFrame())
                frame.SetReadbackTag(readback, 99);
            graph.CollectCompleted();
            Assert.Empty(receiver.Values);
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        graph.CollectCompleted();
        graph.CollectCompleted();
        var result = Assert.Single(receiver.Values);
        Assert.Equal(41ul, result.Tag);
        Assert.Equal(new uint[] { 17, 17 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
    }

    [Fact]
    public void DisabledReadbackWaitsUntilItIsEnabledAgain()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackResults();
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddGeneralFramePass("Fill values", PassQueueSelection.AnyOfType(QueueType.Graphics),
                values, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                    pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref GeneralPassCommandScope commands, in GraphBufferId buffer) =>
                    commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, 23));
            readback = edit.AddReadback(values, BufferRange.Whole, receiver, "Values");
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetReadbackEnabled(readback, false);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Empty(receiver.Values);
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetReadbackEnabled(readback, true);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Equal(new uint[] { 23, 23, 23, 23 },
            MemoryMarshal.Cast<byte, uint>(Assert.Single(receiver.Values).Bytes).ToArray());
    }

    [Fact]
    public void CancelledFrameDropsItsReadbackAndBufferWrites()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackResults();
        GraphBufferId values;
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            readback = edit.AddReadback(values, BufferRange.Whole, receiver, "Values");
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 5, 6, 7, 8 }.AsSpan()));
            frame.SetReadbackTag(readback, 1);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.GetWriteSpan(values, 0, 16).Fill(99);
            frame.SetReadbackTag(readback, 2);
        }
        graph.CollectCompleted();
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetReadbackTag(readback, 3);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Equal(new ulong[] { 1, 3 }, receiver.Values.Select(result => result.Tag));
        Assert.All(receiver.Values, result => Assert.Equal(new uint[] { 5, 6, 7, 8 },
            MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray()));
    }

    [Fact]
    public void ReadbackCanEndASubmissionBeforeFollowingWork()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var receiver = new ReadbackResults();
        bool laterRecorded = false;
        GraphBufferId values;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            _ = edit.AddReadback(values, BufferRange.Whole, receiver, "Early values",
                options: new PassOptions(Recording: PassRecordingMode.CallingThread) { SubmitAfter = true });
            edit.AddGeneralFramePass("Following work", PassQueueSelection.AnyOfType(QueueType.Graphics),
                values, new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                    pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                (ref GeneralPassCommandScope commands, in GraphBufferId buffer) =>
                {
                    laterRecorded = true;
                    commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, 23);
                });
            edit.Commit();
        }
        var timing = new SubmissionTiming(() => laterRecorded);
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 17, 17, 17, 17 }.AsSpan()));
            frame.ObserveExecution(timing);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.True(timing.EarlySubmission);
        Assert.Equal(new uint[] { 17, 17, 17, 17 },
            MemoryMarshal.Cast<byte, uint>(Assert.Single(receiver.Values).Bytes).ToArray());
    }

    private sealed class SubmissionTiming(Func<bool> laterRecorded) : IRenderGraphFrameExecutionObserver
    {
        internal bool EarlySubmission { get; private set; }
        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => EarlySubmission |= !laterRecorded();
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) { }
    }

    private sealed class ReadbackResults : IGraphReadbackReceiver
    {
        internal List<(byte[] Bytes, ulong Tag)> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values.Add((bytes.ToArray(), tag));
    }
}
