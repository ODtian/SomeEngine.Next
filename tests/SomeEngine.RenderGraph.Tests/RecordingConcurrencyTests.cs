using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RecordingConcurrencyTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined, false)]
    [InlineData(FrameSubmissionMode.Pipelined, true)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, false)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit, true)]
    public void WorkerRecordingOverlapsCallingThreadWithoutChangingGpuDependencies(
        FrameSubmissionMode mode, bool dependent)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue, support.CopyQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(4, BufferUsages.CopyDestination), MemoryType.Readback);
        using var recorded = new ManualResetEventSlim();
        int callingThread = Environment.CurrentManagedThreadId;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.CreateTransientBuffer(new BufferDesc(4,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(new BufferRange(0, 4), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, ResourceContentState.Undefined)]);
            var data = new CopyData(source, destination, recorded, callingThread, dependent);
            edit.AddCopyPass<CopyData, byte>("calling thread producer",
                PassQueueSelection.Exact(support.CopyQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref CopyData data) =>
                    pass.Write(data.Source, new BufferRange(0, 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in CopyData data, in byte _) =>
                {
                    Assert.Equal(data.CallingThread, Environment.CurrentManagedThreadId);
                    Assert.True(data.Recorded.Wait(TimeSpan.FromSeconds(10)),
                        "A worker-eligible callback should be able to record while the calling thread records.");
                    commands.ClearBuffer(commands.GetBuffer(data.Source), new BufferRange(0, 4), 73);
                });
            edit.AddCopyPass<CopyData, byte>("worker consumer",
                PassQueueSelection.Exact(support.GraphicsQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.WorkerEligible),
                static (ref PassDefinition pass, ref CopyData data) =>
                {
                    if (data.Dependent)
                        pass.Read(data.Source, new BufferRange(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, new BufferRange(0, 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in CopyData data, in byte _) =>
                {
                    if (data.Dependent)
                        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                            commands.GetBuffer(data.Destination), 0, 4));
                    else
                        commands.ClearBuffer(commands.GetBuffer(data.Destination), new BufferRange(0, 4), 73);
                    data.Recorded.Set();
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 4; index++)
        {
            recorded.Reset();
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: mode)))
                frame.Execute(completions);
            support.Wait(completions);
            using MappedBuffer mapped = support.Backend.Map(result, MapType.Read, new BufferRange(0, 4));
            mapped.Invalidate(new BufferRange(0, 4));
            Assert.Equal(73u, MemoryMarshal.Cast<byte, uint>(mapped.Bytes)[0]);
        }
    }

    private readonly record struct CopyData(GraphBufferId Source, GraphBufferId Destination,
        ManualResetEventSlim Recorded, int CallingThread, bool Dependent);

    [Fact]
    public void RecordingFailureWaitsForOtherCallbacksBeforeReturning()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.CopyQueue, support.GraphicsQueue]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.CreateTransientBuffer(new BufferDesc(4,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            var data = new FailureData(source, entered, release, finished);
            edit.AddCopyPass<FailureData, byte>("failing calling-thread callback",
                PassQueueSelection.Exact(support.CopyQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref FailureData data) =>
                    pass.Write(data.Source, new BufferRange(0, 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in FailureData data, in byte _) =>
                {
                    bool started = data.Entered.Wait(TimeSpan.FromSeconds(10));
                    data.Release.Set();
                    Assert.True(started);
                    throw new InvalidOperationException("recording failed");
                });
            edit.AddCopyPass<FailureData, byte>("worker callback completing during failure",
                PassQueueSelection.Exact(support.GraphicsQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref FailureData data) =>
                    pass.Read(data.Source, new BufferRange(0, 4), PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in FailureData data, in byte _) =>
                {
                    data.Entered.Set();
                    Assert.True(data.Release.Wait(TimeSpan.FromSeconds(10)));
                    data.Finished.Set();
                });
            edit.Commit();
        }
        using RenderGraphFrame frame = graph.BeginFrame();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool failed = false;
        try { frame.Execute(completions); }
        catch (InvalidOperationException exception) when (exception.Message == "recording failed") { failed = true; }
        Assert.True(failed);
        Assert.True(finished.IsSet);
    }

    private readonly record struct FailureData(GraphBufferId Source, ManualResetEventSlim Entered,
        ManualResetEventSlim Release, ManualResetEventSlim Finished);
}
