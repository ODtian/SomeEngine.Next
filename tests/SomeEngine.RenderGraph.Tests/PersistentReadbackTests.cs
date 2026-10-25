using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class PersistentReadbackTests
{
    [Fact]
    public void ReadbackReceiverFailureDoesNotPreventLaterDeliveryOrTheNextFrame()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var failure = new InvalidOperationException();
        var failingReceiver = new FailingReadbackResults(failure);
        var receiver = new ReadbackResults();
        GraphBufferId values;
        GraphReadbackId failingReadback, readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            failingReadback = edit.AddReadback(values, new BufferRange(0, 8), failingReceiver, "Failing values");
            readback = edit.AddReadback(values, new BufferRange(8, 8), receiver, "Following values");
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 5, 6, 7, 8 }.AsSpan()));
            frame.SetReadbackTag(failingReadback, 41);
            frame.SetReadbackTag(readback, 42);
            frame.Execute(completions);
        }
        support.Wait(completions);
        AggregateException error = Assert.Throws<AggregateException>(graph.CollectCompleted);
        Assert.Same(failure, Assert.Single(error.Flatten().InnerExceptions));
        var failed = Assert.Single(failingReceiver.Values);
        Assert.Equal(41ul, failed.Tag);
        Assert.Equal(new uint[] { 5, 6 }, MemoryMarshal.Cast<byte, uint>(failed.Bytes).ToArray());
        var delivered = Assert.Single(receiver.Values);
        Assert.Equal(42ul, delivered.Tag);
        Assert.Equal(new uint[] { 7, 8 }, MemoryMarshal.Cast<byte, uint>(delivered.Bytes).ToArray());
        graph.CollectCompleted();
        Assert.Single(failingReceiver.Values);
        Assert.Single(receiver.Values);

        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetReadbackEnabled(failingReadback, false);
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 9, 10, 11, 12 }.AsSpan()));
            frame.SetReadbackTag(readback, 43);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.Single(failingReceiver.Values);
        Assert.Equal(new ulong[] { 42, 43 }, receiver.Values.Select(result => result.Tag));
        Assert.Equal(new uint[] { 11, 12 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[1].Bytes).ToArray());
    }

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
    public void RecordingFailureBeforeAnySubmissionKeepsPreviousBufferContents()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackResults();
        bool fail = false;
        GraphBufferId values;
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(values);
            readback = edit.AddReadback(values, BufferRange.Whole, receiver, "Values");
            edit.AddGeneralFramePass("Interrupt recording", PassQueueSelection.Exact(support.GraphicsQueue),
                0, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: false,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) => { if (fail) throw new RecordingFailure(); });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: FrameSubmissionMode.RecordAllThenSubmit)))
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 5, 6, 7, 8 }.AsSpan()));
            frame.SetReadbackTag(readback, 1);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Equal(1ul, Assert.Single(receiver.Values).Tag);
        Assert.Equal(new uint[] { 5, 6, 7, 8 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[0].Bytes).ToArray());

        fail = true;
        var observer = new FinishedBufferResults(values);
        bool rejected = false;
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: FrameSubmissionMode.RecordAllThenSubmit)))
        {
            frame.GetWriteSpan(values, 0, 16).Fill(99);
            frame.SetReadbackTag(readback, 2);
            frame.ObserveExecution(observer);
            try { frame.Execute(completions); }
            catch (RecordingFailure) { rejected = true; }
        }
        Assert.True(rejected);
        var failed = Assert.Single(observer.Frames);
        Assert.Empty(failed.Completions);
        Assert.Empty(failed.States);
        Assert.False(failed.Uploaded);
        Assert.Equal(default, failed.UploadCompletion);
        graph.CollectCompleted();
        Assert.Equal(1ul, Assert.Single(receiver.Values).Tag);

        fail = false;
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: FrameSubmissionMode.RecordAllThenSubmit)))
        {
            frame.SetReadbackTag(readback, 3);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.Equal(new ulong[] { 1, 3 }, receiver.Values.Select(result => result.Tag));
        Assert.All(receiver.Values, result => Assert.Equal(new uint[] { 5, 6, 7, 8 },
            MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray()));

        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: FrameSubmissionMode.RecordAllThenSubmit)))
        {
            frame.WriteBuffer(values, 4, MemoryMarshal.AsBytes(new uint[] { 13 }.AsSpan()));
            frame.SetReadbackTag(readback, 4);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.Equal(new ulong[] { 1, 3, 4 }, receiver.Values.Select(result => result.Tag));
        Assert.Equal(new uint[] { 5, 13, 7, 8 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[2].Bytes).ToArray());
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    public void AcceptedUploadBeforeRecordingFailureRemainsReadableInLaterFrames(FrameSubmissionMode mode)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackResults();
        bool fail = false;
        GraphBufferId values;
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            values = edit.CreatePersistentBuffer(new BufferDesc(16,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            edit.AddBufferUploadPass(values);
            readback = edit.AddReadback(values, BufferRange.Whole, receiver, "Values",
                options: new PassOptions(RepeatCommands: false, Recording: PassRecordingMode.CallingThread)
                    { SubmitAfter = true });
            GraphPassId later = edit.AddGeneralFramePass("Interrupt later recording",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: false,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref int _) => { },
                (ref GeneralPassCommandScope _, in int _) => { if (fail) throw new RecordingFailure(); });
            edit.OrderAfter(later, readback);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 5, 6, 7, 8 }.AsSpan()));
            frame.SetReadbackTag(readback, 1);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Equal(1ul, Assert.Single(receiver.Values).Tag);
        Assert.Equal(new uint[] { 5, 6, 7, 8 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[0].Bytes).ToArray());

        fail = true;
        var observer = new FinishedBufferResults(values);
        bool rejected = false;
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 9, 10, 11, 12 }.AsSpan()));
            frame.SetReadbackTag(readback, 2);
            frame.ObserveExecution(observer);
            try { frame.Execute(completions); }
            catch (RecordingFailure) { rejected = true; }
        }
        Assert.True(rejected);
        var failed = Assert.Single(observer.Frames);
        Assert.NotEmpty(failed.Completions);
        Assert.NotEmpty(failed.States);
        Assert.True(failed.Uploaded);
        Assert.NotEqual(default, failed.UploadCompletion);
        support.Wait(failed.Completions);
        graph.CollectCompleted();
        Assert.Equal(1ul, Assert.Single(receiver.Values).Tag);

        fail = false;
        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.SetReadbackTag(readback, 3);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.Equal(new ulong[] { 1, 3 }, receiver.Values.Select(result => result.Tag));
        Assert.Equal(new uint[] { 9, 10, 11, 12 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[1].Bytes).ToArray());

        using (RenderGraphFrame frame = graph.BeginFrame(new(SubmissionMode: mode)))
        {
            frame.WriteBuffer(values, 4, MemoryMarshal.AsBytes(new uint[] { 13 }.AsSpan()));
            frame.SetReadbackTag(readback, 4);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        graph.CollectCompleted();
        Assert.Equal(new ulong[] { 1, 3, 4 }, receiver.Values.Select(result => result.Tag));
        Assert.Equal(new uint[] { 9, 13, 11, 12 },
            MemoryMarshal.Cast<byte, uint>(receiver.Values[2].Bytes).ToArray());
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


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatedTimestampCommandDataRequiresAnExplicitFrameWrite(bool declareWrite)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.CalibratedTimestamps);
        ulong frequency = support.Backend.CalibrateTimestamps(support.GraphicsQueue).QueueFrequency;
        Assert.True(frequency > 0);
        using QueryPool pool = support.Backend.CreateQueryPool(support.Device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2));
        Assert.Equal(8u, pool.ResultInfo.ResultStride);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var receiver = new ReadbackResults();
        GraphQueryPoolId queries;
        GraphPassId timestamps, resolve;
        GraphReadbackId readback;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            queries = edit.RegisterQueryPool(pool);
            GraphBufferId values = edit.CreateBuffer(new BufferDesc(16,
                BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource));
            timestamps = edit.AddGeneralFramePass("Optional timestamp pair",
                PassQueueSelection.Exact(support.GraphicsQueue), default(GraphQueryPoolId),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition _, ref GraphQueryPoolId _) => { },
                static (ref GeneralPassCommandScope commands, in GraphQueryPoolId id) =>
                {
                    if (id == default) return;
                    commands.WriteTimestamp(id, 0);
                    commands.WriteTimestamp(id, 1);
                });
            resolve = edit.AddGeneralFramePass("Resolve optional timestamps",
                PassQueueSelection.Exact(support.GraphicsQueue), new TimestampResolve(queries, values),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref TimestampResolve data) =>
                {
                    pass.Read(data.Queries, new QueryRange(0, 2));
                    pass.Write(data.Values, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref GeneralPassCommandScope commands, in TimestampResolve data) =>
                    commands.ResolveQueries(data.Queries, 0, 2, commands.GetBuffer(data.Values),
                        new BufferRange(0, 16)));
            edit.OrderAfter(resolve, timestamps);
            readback = edit.AddReadback(values, BufferRange.Whole, receiver, "Timestamp values");
            edit.OrderAfter(readback, resolve);
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassEnabled(resolve, false);
            frame.SetReadbackEnabled(readback, false);
            frame.Execute(completions);
        }
        support.Wait(completions);
        graph.CollectCompleted();
        Assert.Empty(receiver.Values);

        bool undefinedRead = false;
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(timestamps, queries);
            if (declareWrite) frame.GetPass(timestamps).Write(queries, new QueryRange(0, 2));
            frame.SetPassEnabled(resolve, true);
            frame.SetReadbackEnabled(readback, true);
            frame.SetReadbackTag(readback, 42);
            try { frame.Execute(completions); }
            catch (UndefinedResourceReadException) { undefinedRead = true; }
        }
        Assert.Equal(!declareWrite, undefinedRead);
        if (!declareWrite) return;
        support.Wait(completions);
        graph.CollectCompleted();
        var result = Assert.Single(receiver.Values);
        Assert.Equal(42ul, result.Tag);
        ReadOnlySpan<ulong> ticks = MemoryMarshal.Cast<byte, ulong>(result.Bytes);
        Assert.Equal(2, ticks.Length);
        Assert.True(ticks[0] > 0 && ticks[1] >= ticks[0]);
        double milliseconds = 1000.0 * (ticks[1] - ticks[0]) / frequency;
        Assert.True(double.IsFinite(milliseconds) && milliseconds >= 0);
    }

    private sealed class RecordingFailure : Exception;

    private sealed class FinishedBufferResults(GraphBufferId buffer) : IRenderGraphFrameExecutionObserver
    {
        internal List<(QueueCompletion[] Completions, BufferBoundaryState[] States,
            bool Uploaded, QueueCompletion UploadCompletion)> Frames { get; } = [];

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            bool uploaded = result.TryGetPersistentBufferUploadCompletion(buffer, out QueueCompletion completion);
            Frames.Add((result.SubmittedCompletions.ToArray(), result.GetBufferStates(buffer).ToArray(),
                uploaded, completion));
        }
    }

    private readonly record struct TimestampResolve(GraphQueryPoolId Queries, GraphBufferId Values);

    private sealed class SubmissionTiming(Func<bool> laterRecorded) : IRenderGraphFrameExecutionObserver
    {
        internal bool EarlySubmission { get; private set; }
        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => EarlySubmission |= !laterRecorded();
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) { }
    }

    private sealed class FailingReadbackResults(Exception error) : IGraphReadbackReceiver
    {
        internal List<(byte[] Bytes, ulong Tag)> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            Values.Add((bytes.ToArray(), tag));
            throw error;
        }
    }

    private sealed class ReadbackResults : IGraphReadbackReceiver
    {
        internal List<(byte[] Bytes, ulong Tag)> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values.Add((bytes.ToArray(), tag));
    }
}
