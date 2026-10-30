using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RecordingConcurrencyTests
{
    [Fact]
    public void WorkerRecordingOverlapsOrderedCallingThreadPassesOnTheSameQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device,
            [support.GraphicsQueue], new RenderGraphDesc(MaximumFramesInFlight: 1));
        const int workerCopies = 32;
        using Buffer output = support.Backend.CreateBuffer(support.Device,
            new BufferDesc((workerCopies + 1) * sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        using var recorded = new ManualResetEventSlim();
        int callingThread = Environment.CurrentManagedThreadId;
        var callingOrder = new List<int>();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId source = edit.CreateTransientBuffer(new BufferDesc(4,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphBufferId destination = edit.RegisterExternalBuffer(output,
                [new(BufferRange.Whole, output.InitialSync, output.InitialAccess, ResourceContentState.Undefined)]);
            var data = new CopyData(source, destination, recorded, callingThread, true);
            GraphPassId first = edit.AddCopyFramePass("first calling-thread write",
                PassQueueSelection.Exact(support.GraphicsQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref CopyData data) =>
                    pass.Write(data.Source, new BufferRange(0, 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in CopyData data) =>
                {
                    Assert.Equal(data.CallingThread, Environment.CurrentManagedThreadId);
                    callingOrder.Add(1);
                    Assert.True(data.Recorded.Wait(TimeSpan.FromSeconds(10)));
                    commands.ClearBuffer(commands.GetBuffer(data.Source), new BufferRange(0, 4), 73);
                });
            GraphPassId previous = first;
            for (int index = 0; index < workerCopies; index++)
            {
                ulong offset = (ulong)index * sizeof(uint);
                GraphPassId worker = edit.AddCopyFramePass("worker copy between calling-thread writes",
                    PassQueueSelection.Exact(support.GraphicsQueue), data,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    (ref PassDefinition pass, ref CopyData data) =>
                    {
                        pass.Read(data.Source, new BufferRange(0, 4), PipelineSync.Copy, ResourceAccess.CopySource);
                        pass.Write(data.Destination, new BufferRange(offset, 4), PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    (ref CopyPassCommandScope commands, in CopyData data) =>
                    {
                        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(data.Source), 0,
                            commands.GetBuffer(data.Destination), offset, 4));
                        data.Recorded.Set();
                    });
                edit.OrderAfter(worker, previous);
                previous = worker;
            }
            GraphPassId last = edit.AddCopyFramePass("last calling-thread write",
                PassQueueSelection.Exact(support.GraphicsQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref CopyData data) =>
                    pass.Write(data.Destination, new BufferRange(workerCopies * sizeof(uint), 4), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands, in CopyData data) =>
                {
                    Assert.Equal(data.CallingThread, Environment.CurrentManagedThreadId);
                    callingOrder.Add(2);
                    commands.ClearBuffer(commands.GetBuffer(data.Destination), new BufferRange(workerCopies * sizeof(uint), 4), 149);
                });
            edit.OrderAfter(last, previous);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 4; index++)
        {
            recorded.Reset();
            callingOrder.Clear();
            using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(FrameSubmissionMode.RecordAhead)))
                frame.Execute(completions);
            support.Wait(completions);
            Assert.Equal(new[] { 1, 2 }, callingOrder);
            using MappedBuffer mapping = support.Backend.Map(output, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.Equal(Enumerable.Repeat(73u, workerCopies).Append(149u),
                MemoryMarshal.Cast<byte, uint>(mapping.Bytes).ToArray());
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.RecordAhead, false)]
    [InlineData(FrameSubmissionMode.RecordAhead, true)]
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
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.WorkerEligible, EstimatedRecordingCost: 64),
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
            edit.DeclareCopyPass("independent worker work", PassQueueSelection.Exact(support.GraphicsQueue), options: new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedRecordingCost: 64));
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
        var recordingFailure = new InvalidOperationException("recording failed");
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
                (ref CopyPassCommandScope _, in FailureData data, in byte _) =>
                {
                    bool started = data.Entered.Wait(TimeSpan.FromSeconds(10));
                    data.Release.Set();
                    Assert.True(started);
                    throw recordingFailure;
                });
            edit.AddCopyPass<FailureData, byte>("worker callback completing during failure",
                PassQueueSelection.Exact(support.GraphicsQueue), data,
                new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedRecordingCost: 64),
                static (ref PassDefinition pass, ref FailureData data) =>
                    pass.Read(data.Source, new BufferRange(0, 4), PipelineSync.Copy, ResourceAccess.CopySource),
                static (ref CopyPassCommandScope _, in FailureData data, in byte _) =>
                {
                    data.Entered.Set();
                    Assert.True(data.Release.Wait(TimeSpan.FromSeconds(10)));
                    data.Finished.Set();
                });
            edit.DeclareCopyPass("independent worker work", PassQueueSelection.Exact(support.GraphicsQueue), options: new PassOptions(Culling: PassCullingMode.NeverCull, EstimatedRecordingCost: 64));
            edit.Commit();
        }
        using RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(FrameSubmissionMode.RecordAhead));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        bool failed = false;
        try { frame.Execute(completions); }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, recordingFailure)) { failed = true; }
        Assert.True(failed);
        Assert.True(finished.IsSet);
    }

    private readonly record struct FailureData(GraphBufferId Source, ManualResetEventSlim Entered,
        ManualResetEventSlim Release, ManualResetEventSlim Finished);
}
