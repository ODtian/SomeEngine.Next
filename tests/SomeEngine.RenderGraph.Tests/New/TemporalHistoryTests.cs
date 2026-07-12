using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Graphics.Null;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class TemporalHistoryTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void History_ring_resolves_current_and_prior_frames_after_completion()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });
        Guid stableId = Guid.Parse("dfae9082-7a4d-41cc-9edb-670559932bb7");
        BufferHandle first = Upload(device, [11, 22, 33, 44]);
        BufferHandle second = Upload(device, [51, 62, 73, 84]);
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        try
        {
            GraphExecution frame0 = WriteFrame(device, graph, stableId, first, QueueSelection.Copy, 4);
            Assert.True(frame0.Wait(TimeSpan.Zero));

            GraphExecution frame1 = ReadPreviousAndWriteCurrent(
                device,
                graph,
                stableId,
                second,
                output,
                QueueSelection.Copy,
                4);
            Assert.True(frame1.Wait(TimeSpan.Zero));
            byte[] actual = new byte[4];
            device.ReadBuffer(output, 0, actual);
            Assert.Equal(new byte[] { 11, 22, 33, 44 }, actual);
            Assert.Contains(frame1.Capture!.Resources, static resource =>
                resource.Lifetime == ResourceLifetime.Temporal && resource.HistoryOffset == 1);
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyBuffer(second);
            device.DestroyBuffer(first);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Reset_resize_and_in_flight_generations_never_publish_stale_history()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        Guid stableId = Guid.Parse("065f7e53-0761-4c4c-b4ed-39968eb69605");
        BufferHandle upload = Upload(device, [1, 2, 3, 4]);
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        try
        {
            Assert.True(WriteFrame(device, graph, stableId, upload, QueueSelection.Copy, 4).Wait(TimeSpan.Zero));
            graph.ResetHistory(stableId);

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                ReadPrevious(device, graph, stableId, output, QueueSelection.Copy, 4));
            Assert.Contains("has not been imported or fully produced", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyBuffer(upload);
        }

        AssertDescriptorChangeInvalidatesHistory();
        AssertInFlightHistoryOrdering();
        AssertFailedFramePreservesCommittedHistory();
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Descriptor_change_recreates_the_ring_and_invalidates_old_slices()
    {
        AssertDescriptorChangeInvalidatesHistory();
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void In_flight_history_is_ordered_by_cross_queue_completion_readiness()
    {
        AssertInFlightHistoryOrdering();
    }

    private static void AssertDescriptorChangeInvalidatesHistory()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        Guid stableId = Guid.Parse("dcae5a32-83ad-4498-a1d1-b8c88df98eca");
        BufferHandle upload = Upload(device, [5, 6, 7, 8]);
        BufferHandle output = device.CreateBuffer(new BufferDesc(8, BufferUsage.CopyDestination), MemoryType.Readback);
        try
        {
            Assert.True(WriteFrame(device, graph, stableId, upload, QueueSelection.Copy, 4).Wait(TimeSpan.Zero));
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                ReadPrevious(device, graph, stableId, output, QueueSelection.Copy, 8));
            Assert.Contains("has not been imported or fully produced", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyBuffer(upload);
        }
    }

    private static void AssertInFlightHistoryOrdering()
    {
        using NullDevice device = new(new NullOptions { AutoCompleteSubmissions = false });
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        Guid stableId = Guid.Parse("452169a8-7b56-4651-ad71-ffda7ac2adca");
        BufferHandle first = Upload(device, [9, 8, 7, 6]);
        BufferHandle second = Upload(device, [4, 3, 2, 1]);
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        try
        {
            GraphExecution frame0 = WriteFrame(device, graph, stableId, first, QueueSelection.Copy, 4);
            GraphExecution frame1 = ReadPreviousAndWriteCurrent(
                device,
                graph,
                stableId,
                second,
                output,
                QueueSelection.Compute,
                4);

            Assert.True(device.Statistics.SubmissionWaits >= 1);
            foreach (GpuCompletion completion in frame0.Completions) device.AdvanceCompletion(completion);
            foreach (GpuCompletion completion in frame1.Completions) device.AdvanceCompletion(completion);
            Assert.True(frame1.Wait(TimeSpan.Zero));
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyBuffer(second);
            device.DestroyBuffer(first);
        }
    }

    private static void AssertFailedFramePreservesCommittedHistory()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        Guid stableId = Guid.Parse("27b55987-799c-4a70-8819-9913d650a3af");
        BufferHandle upload = Upload(device, [4, 2, 4, 2]);
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        try
        {
            Assert.True(WriteFrame(device, graph, stableId, upload, QueueSelection.Copy, 4).Wait(TimeSpan.Zero));

            GraphBuilder failed = graph.Begin();
            BufferId temporal = failed.CreateBuffer(Temporal(stableId, 4));
            PassBuilder write = failed.AddPass("failed-history-frame", QueueSelection.Copy);
            _ = write.Write(temporal, BufferUse.CopyDestination, new BufferRange(0, 4));
            write.Execute(static (ICommandContext _, in PassResources _) =>
                throw new InvalidOperationException("expected history failure"));
            Exception failure = CaptureFailure(graph, ref failed);
            Assert.Contains("expected history failure", failure.ToString(), StringComparison.Ordinal);

            ReadPrevious(device, graph, stableId, output, QueueSelection.Copy, 4);
            byte[] actual = new byte[4];
            device.ReadBuffer(output, 0, actual);
            Assert.Equal(new byte[] { 4, 2, 4, 2 }, actual);
        }
        finally
        {
            device.DestroyBuffer(output);
            device.DestroyBuffer(upload);
        }
    }

    private static Exception CaptureFailure(RenderGraph graph, ref GraphBuilder builder)
    {
        try
        {
            _ = graph.Execute(ref builder);
            return new Xunit.Sdk.XunitException("The temporal failure frame unexpectedly succeeded.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static BufferHandle Upload(NullDevice device, byte[] bytes)
    {
        BufferHandle handle = device.CreateBuffer(new BufferDesc((ulong)bytes.Length, BufferUsage.CopySource), MemoryType.Upload);
        device.WriteBuffer(handle, 0, bytes);
        return handle;
    }

    private static BufferResourceDesc Temporal(Guid stableId, ulong size) => BufferResourceDesc.Temporal(
        new BufferDesc(size, BufferUsage.CopySource | BufferUsage.CopyDestination, "history-ring"),
        historyCount: 1,
        stableId);

    private static GraphExecution WriteFrame(
        NullDevice device,
        RenderGraph graph,
        Guid stableId,
        BufferHandle upload,
        QueueSelection queue,
        ulong size)
    {
        GraphBuilder builder = graph.Begin();
        BufferId temporal = builder.CreateBuffer(Temporal(stableId, size));
        BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
        PassBuilder write = builder.AddPass("history-write", queue);
        BufferAccess sourceAccess = write.Read(source, BufferUse.CopySource, new BufferRange(0, size));
        BufferAccess destinationAccess = write.Write(temporal, BufferUse.CopyDestination, new BufferRange(0, size));
        write.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
            resources.Get(sourceAccess), 0, resources.Get(destinationAccess), 0, size));
        return graph.Execute(ref builder);
    }

    private static GraphExecution ReadPreviousAndWriteCurrent(
        NullDevice device,
        RenderGraph graph,
        Guid stableId,
        BufferHandle upload,
        BufferHandle output,
        QueueSelection queue,
        ulong size)
    {
        GraphBuilder builder = graph.Begin();
        BufferId temporal = builder.CreateBuffer(Temporal(stableId, size));
        BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
        BufferId destination = builder.ImportBuffer(output, BufferUse.CopyDestination, BufferUse.CopyDestination, contentsAvailable: false);

        PassBuilder read = builder.AddPass("history-read", queue);
        BufferAccess history = read.Read(temporal.History(1), BufferUse.CopySource, new BufferRange(0, size));
        BufferAccess result = read.Write(destination, BufferUse.CopyDestination, new BufferRange(0, size));
        read.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
            resources.Get(history), 0, resources.Get(result), 0, size));

        PassBuilder write = builder.AddPass("history-write", queue);
        BufferAccess sourceAccess = write.Read(source, BufferUse.CopySource, new BufferRange(0, size));
        BufferAccess current = write.Write(temporal, BufferUse.CopyDestination, new BufferRange(0, size));
        write.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
            resources.Get(sourceAccess), 0, resources.Get(current), 0, size));
        return graph.Execute(ref builder);
    }

    private static void ReadPrevious(
        NullDevice device,
        RenderGraph graph,
        Guid stableId,
        BufferHandle output,
        QueueSelection queue,
        ulong size)
    {
        GraphBuilder builder = graph.Begin();
        BufferId temporal = builder.CreateBuffer(Temporal(stableId, size));
        BufferId destination = builder.ImportBuffer(output, BufferUse.CopyDestination, BufferUse.CopyDestination, contentsAvailable: false);
        PassBuilder read = builder.AddPass("history-read-after-reset", queue);
        BufferAccess history = read.Read(temporal.History(1), BufferUse.CopySource, new BufferRange(0, size));
        BufferAccess result = read.Write(destination, BufferUse.CopyDestination, new BufferRange(0, size));
        read.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
            resources.Get(history), 0, resources.Get(result), 0, size));
        _ = graph.Execute(ref builder);
    }
}
