using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CorrectnessRegressionTests
{
    [Fact]
    public void Resource_ids_from_another_graph_invocation_are_rejected()
    {
        GraphRecording first = new();
        GraphRecording second = new();
        BufferId foreign = first.AddBuffer(new BufferDesc(64, BufferUsage.CopySource), default);
        int pass = second.AddPass("consumer", new QueueSelection(QueueType.Copy));

        ArgumentException? error = null;
        try
        {
            _ = second.AddBufferAccess(
                pass,
                foreign,
                ResourceEffect.Read,
                BufferUse.CopySource,
                BufferRange.Whole,
                PriorContents.Required,
                WriteCoverage.Partial);
        }
        catch (ArgumentException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains("different graph invocation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_physical_import_is_rejected_before_dependency_analysis()
    {
        using Device device = new();
        GraphRecording recording = new();
        BufferHandle physical = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopySource));
        ImportedBuffer import = new(
            physical,
            device.GetBufferMetadata(physical),
            BufferUse.CopySource,
            BufferUse.CopySource,
            true);
        _ = recording.AddBuffer(new BufferDesc(64, BufferUsage.CopySource), import);

        InvalidOperationException? error = null;
        try
        {
            _ = recording.AddBuffer(new BufferDesc(64, BufferUsage.CopySource), import);
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains("imported only once", error.Message, StringComparison.Ordinal);
        device.DestroyBuffer(physical);
    }

    [Fact]
    public void Distinct_texture_mips_each_receive_their_own_initial_transition()
    {
        using Device device = new();
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(32, 32, Format.R8G8B8A8UNorm, TextureUsage.CopySource | TextureUsage.CopyDestination, MipLevels: 2),
            default);
        TextureSubresourceRange mip0 = new(0, 1, 0, 1, TextureAspect.Color);
        TextureSubresourceRange mip1 = new(1, 1, 0, 1, TextureAspect.Color);

        int first = recording.AddPass("mip-0", new QueueSelection(QueueType.Copy));
        _ = recording.AddTextureAccess(first, texture, ResourceEffect.Write, TextureUse.CopyDestination, mip0, PriorContents.Discard, WriteCoverage.Full);
        recording.SetExecution(first, static (ICommandContext _, in PassResources _) => { });
        int second = recording.AddPass("mip-1", new QueueSelection(QueueType.Copy));
        _ = recording.AddTextureAccess(second, texture, ResourceEffect.Write, TextureUse.CopyDestination, mip1, PriorContents.Discard, WriteCoverage.Full);
        recording.SetExecution(second, static (ICommandContext _, in PassResources _) => { });

        BufferDesc outputDesc = new(4, BufferUsage.CopyDestination);
        BufferHandle outputHandle = device.CreateBuffer(outputDesc);
        BufferId output = recording.AddBuffer(outputDesc, new ImportedBuffer(
            outputHandle,
            device.GetBufferMetadata(outputHandle),
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            false));
        int publish = recording.AddPass("publish", QueueSelection.Copy);
        _ = recording.AddTextureAccess(publish, texture, ResourceEffect.Read, TextureUse.CopySource, mip0, PriorContents.Required, WriteCoverage.Partial);
        _ = recording.AddTextureAccess(publish, texture, ResourceEffect.Read, TextureUse.CopySource, mip1, PriorContents.Required, WriteCoverage.Partial);
        _ = recording.AddBufferAccess(publish, output, ResourceEffect.Write, BufferUse.CopyDestination, BufferRange.Whole, PriorContents.Discard, WriteCoverage.Full);
        recording.SetExecution(publish, static (ICommandContext _, in PassResources _) => { });

        FrozenGraph frozen = recording.Freeze(device);
        CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: false);

        BarrierTemplate firstBarrier = Assert.Single(compiled.BeforeBarriers[first]);
        BarrierTemplate secondBarrier = Assert.Single(compiled.BeforeBarriers[second]);
        Assert.Equal(mip0, firstBarrier.TextureRange);
        Assert.Equal(mip1, secondBarrier.TextureRange);
        Assert.Equal(ResourceState.Common, firstBarrier.Before);
        Assert.Equal(ResourceState.Common, secondBarrier.Before);
        Assert.Equal(ResourceState.CopyDestination, firstBarrier.After);
        Assert.Equal(ResourceState.CopyDestination, secondBarrier.After);
        device.DestroyBuffer(outputHandle);
    }

    [Fact]
    public void Empty_graph_is_a_zero_submission_success()
    {
        using Device device = new();
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();

        GraphExecution execution = graph.Execute(ref builder);

        Assert.Empty(execution.Completions);
        Assert.False(execution.Completions is GpuCompletion[]);
        Assert.True(execution.Wait(TimeSpan.Zero));
        Assert.Equal(0, graph.Statistics.CommandListsRecorded);
        Assert.Equal(0, graph.Statistics.Submissions);
    }

    [Fact]
    public void Transient_resource_and_heap_enter_deferred_retirement_without_wait_idle()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferId transient = builder.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        PassBuilder pass = builder.AddPass("produce", new QueueSelection(QueueType.Copy));
        output.Root(ref builder, ref pass);
        _ = pass.Write(transient, BufferUse.CopyDestination);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        GraphExecution execution = graph.Execute(ref builder);

        GpuCompletion completion = Assert.Single(execution.Completions);
        Assert.Equal(0UL, device.GetCompletedValue(completion.Queue));
        device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 2);
    }

    [Fact]
    public void Unused_import_cannot_silently_promise_a_final_state_transition()
    {
        using Device device = new();
        GraphRecording recording = new();
        BufferDesc desc = new(64, BufferUsage.CopySource | BufferUsage.CopyDestination);
        BufferHandle physical = device.CreateBuffer(desc);
        _ = recording.AddBuffer(
            desc,
            new ImportedBuffer(
                physical,
                device.GetBufferMetadata(physical),
                BufferUse.CopySource,
                BufferUse.CopyDestination,
                true));

        FrozenGraph frozen = recording.Freeze(device);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Compiler.Compile(frozen, device.Compilation, optimized: false));

        Assert.Contains("no submission owns the transition", error.Message, StringComparison.Ordinal);
        device.DestroyBuffer(physical);
    }

    [Fact]
    public void Queue_selection_preserves_declared_preference_order()
    {
        using Device allQueues = new();
        Assert.Equal(QueueType.Compute, QueueSelection.Compute.Select(allQueues.Compilation));
        Assert.Equal(QueueType.Copy, QueueSelection.Copy.Select(allQueues.Compilation));

        using Device graphicsOnly = new(new Options
        {
            SupportsAsyncCompute = false,
            SupportsCopyQueue = false,
        });
        Assert.Equal(QueueType.Graphics, QueueSelection.Compute.Select(graphicsOnly.Compilation));
        Assert.Equal(QueueType.Graphics, QueueSelection.Copy.Select(graphicsOnly.Compilation));
    }

    [Fact]
    public void Transparent_cache_only_starts_async_optimized_compilation_for_an_enabled_transform()
    {
        using Device device = new();
        using (RenderGraph conservativeOnly = new(device))
        {
            GraphBuilder builder = conservativeOnly.Begin();
            _ = conservativeOnly.Execute(ref builder);
            Assert.Equal(0, conservativeOnly.Statistics.OptimizedFlightsStarted);
        }

        using (RenderGraph optimized = new(device, new RenderGraphOptions
               {
                   EnableTransientAliasing = true,
               }))
        {
            GraphBuilder builder = optimized.Begin();
            _ = optimized.Execute(ref builder);
            Assert.Equal(1, optimized.Statistics.OptimizedFlightsStarted);
        }

        using RenderGraph synchronousOnly = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableTransientAliasing = true,
        });
        GraphBuilder synchronousBuilder = synchronousOnly.Begin();
        _ = synchronousOnly.Execute(ref synchronousBuilder);
        Assert.Equal(0, synchronousOnly.Statistics.OptimizedFlightsStarted);
    }

    [Fact]
    public void Coordinator_recording_lane_runs_on_the_render_graph_owner_thread()
    {
        using Device device = new();
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        int ownerThread = Environment.CurrentManagedThreadId;
        int callbackThread = 0;
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass(
            "coordinator-affine",
            QueueSelection.Copy,
            PassRecordingLane.Coordinator);
        output.Root(ref builder, ref pass);
        pass.Execute((ICommandContext _, in PassResources _) =>
            callbackThread = Environment.CurrentManagedThreadId);

        GraphExecution execution = graph.Execute(ref builder);

        Assert.Equal(ownerThread, callbackThread);
        Assert.True(execution.Wait(TimeSpan.Zero));
    }

    [Fact]
    public void Worker_and_coordinator_recording_lanes_can_overlap_before_ordered_submission()
    {
        using Device device = new();
        using ObservableOutput workerOutput = new(device);
        using ObservableOutput coordinatorOutput = new(device);
        using RenderGraph graph = new(device);
        using ManualResetEventSlim workerEntered = new();
        using ManualResetEventSlim coordinatorReleasedWorker = new();
        int ownerThread = Environment.CurrentManagedThreadId;
        int workerThread = 0;
        int coordinatorThread = 0;

        GraphBuilder builder = graph.Begin();
        PassBuilder worker = builder.AddPass("worker", QueueSelection.Copy);
        workerOutput.Root(ref builder, ref worker);
        worker.Execute((ICommandContext _, in PassResources _) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            workerEntered.Set();
            if (!coordinatorReleasedWorker.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Coordinator recording did not overlap the worker recording unit.");
        });

        PassBuilder coordinator = builder.AddPass(
            "coordinator",
            QueueSelection.Copy,
            PassRecordingLane.Coordinator);
        coordinatorOutput.Root(ref builder, ref coordinator);
        coordinator.Execute((ICommandContext _, in PassResources _) =>
        {
            coordinatorThread = Environment.CurrentManagedThreadId;
            if (!workerEntered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Worker recording did not begin while the coordinator was recording.");
            coordinatorReleasedWorker.Set();
        });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.NotEqual(ownerThread, workerThread);
        Assert.Equal(ownerThread, coordinatorThread);
        Assert.True(execution.Wait(TimeSpan.Zero));
        AssertCrossQueueBatchOrderingAndOutput();
    }

    private static void AssertCrossQueueBatchOrderingAndOutput()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        byte[] expected = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(value * 23 + 9))).ToArray();
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopyDestination),
            MemoryType.Readback);
        device.WriteBuffer(upload, 0, expected);
        try
        {
            using (RenderGraph compileGraph = new(device))
            {
                GraphBuilder compileBuilder = compileGraph.Begin();
                BuildCrossQueueCopy(ref compileBuilder, upload, readback, (ulong)expected.Length);
                GraphRecording recording = compileBuilder.Consume(compileGraph);
                compileGraph.Abandon(recording);
                FrozenGraph frozen = recording.Freeze(device);
                CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: false);

                Assert.Equal(2, compiled.ExecutionBatches.Length);
                Assert.Equal(QueueType.Copy, compiled.ExecutionBatches[0].Queue);
                Assert.Equal(QueueType.Graphics, compiled.ExecutionBatches[1].Queue);
                Assert.Contains(0, compiled.ExecutionBatches[1].Dependencies);
            }

            using RenderGraph graph = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
            });
            GraphBuilder builder = graph.Begin();
            BuildCrossQueueCopy(ref builder, upload, readback, (ulong)expected.Length);
            GraphExecution execution = graph.Execute(ref builder);

            Assert.Equal(2, device.Statistics.Submissions);
            Assert.Equal(1, device.Statistics.SubmissionWaits);
            GpuCompletion copy = Assert.Single(execution.Completions, static value => value.Queue == QueueType.Copy);
            GpuCompletion graphics = Assert.Single(execution.Completions, static value => value.Queue == QueueType.Graphics);
            Assert.Equal(0UL, device.GetCompletedValue(QueueType.Copy));
            Assert.Equal(0UL, device.GetCompletedValue(QueueType.Graphics));
            device.AdvanceCompletion(copy);
            Assert.False(execution.Wait(TimeSpan.Zero));
            device.AdvanceCompletion(graphics);
            Assert.True(execution.Wait(TimeSpan.Zero));
            byte[] actual = new byte[expected.Length];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    private static void BuildCrossQueueCopy(
        ref GraphBuilder builder,
        BufferHandle upload,
        BufferHandle readback,
        ulong size)
    {
        BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
        BufferId destination = builder.ImportBuffer(
            readback,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        BufferId intermediate = builder.CreateBuffer(new BufferDesc(
            size,
            BufferUsage.CopySource | BufferUsage.CopyDestination));

        PassBuilder producer = builder.AddPass("cross-queue-producer", QueueSelection.Copy);
        BufferAccess input = producer.Read(source, BufferUse.CopySource);
        BufferAccess intermediateWrite = producer.Write(intermediate, BufferUse.CopyDestination);
        producer.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(input), 0, resources.Get(intermediateWrite), 0, size));

        PassBuilder consumer = builder.AddPass("cross-queue-consumer", QueueSelection.Graphics);
        BufferAccess intermediateRead = consumer.Read(intermediate, BufferUse.CopySource);
        BufferAccess output = consumer.Write(destination, BufferUse.CopyDestination);
        consumer.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(intermediateRead), 0, resources.Get(output), 0, size));
    }

    private sealed class ObservableOutput : IDisposable
    {
        private readonly Device _device;
        private readonly BufferHandle _buffer;

        public ObservableOutput(Device device)
        {
            _device = device;
            _buffer = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination));
        }

        public void Root(ref GraphBuilder builder, ref PassBuilder pass)
        {
            BufferId output = builder.ImportBuffer(
                _buffer,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            _ = pass.Write(output, BufferUse.CopyDestination);
        }

        public void Dispose()
        {
            _device.DestroyBuffer(_buffer);
            _device.CollectGarbage();
        }
    }
}
