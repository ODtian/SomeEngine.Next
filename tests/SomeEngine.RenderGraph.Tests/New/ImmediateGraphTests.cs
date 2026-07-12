using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ImmediateGraphTests
{
    [Fact]
    public void Immediate_graph_records_submits_and_reads_back_through_warp()
    {

        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });
        using RenderGraph graph = new(device);
        BufferHandle upload = device.CreateBuffer(new BufferDesc(256, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(256, BufferUsage.CopyDestination), MemoryType.Readback);

        byte[] expected = new byte[256];
        for (int index = 0; index < expected.Length; index++) expected[index] = unchecked((byte)(index * 29 + 7));
        device.WriteBuffer(upload, 0, expected);

        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId destination = builder.ImportBuffer(readback, BufferUse.CopyDestination, BufferUse.CopyDestination, contentsAvailable: false);
            PassBuilder pass = builder.AddPass("native-copy", new QueueSelection(QueueType.Copy));
            BufferAccess sourceAccess = pass.Read(source, BufferUse.CopySource);
            BufferAccess destinationAccess = pass.Write(destination, BufferUse.CopyDestination);
            pass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(sourceAccess), 0, resources.Get(destinationAccess), 0, 256));

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.FromSeconds(5)));
            byte[] actual = new byte[expected.Length];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(expected, actual);

            RenderGraphStatistics statistics = graph.Statistics;
            Assert.Equal(1, statistics.Recordings);
            Assert.Equal(1, statistics.ConservativeCompilations);
            Assert.Equal(1, statistics.CommandListsRecorded);
            Assert.Equal(1, statistics.Submissions);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Optimized_alias_plan_executes_internal_acquire_batches_on_warp()
    {

        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });
        using RenderGraph graph = new(device, new RenderGraphOptions { EnableTransientAliasing = true });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(256, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(512, BufferUsage.CopyDestination), MemoryType.Readback);
        byte[] expected = new byte[256];
        for (int index = 0; index < expected.Length; index++) expected[index] = unchecked((byte)(index * 17 + 31));
        device.WriteBuffer(upload, 0, expected);

        try
        {
            bool selectedAliasPlan = false;
            for (int attempt = 0; attempt < 32 && !selectedAliasPlan; attempt++)
            {
                GraphBuilder builder = graph.Begin();
                BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
                BufferId output = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);
                BufferDesc transientDesc = new(256, BufferUsage.CopySource | BufferUsage.CopyDestination);
                BufferId first = builder.CreateBuffer(transientDesc with { Name = "alias-first" });
                BufferId second = builder.CreateBuffer(transientDesc with { Name = "alias-second" });

                PassBuilder firstWrite = builder.AddPass("first-write", QueueSelection.Copy);
                BufferAccess firstInput = firstWrite.Read(source, BufferUse.CopySource);
                BufferAccess firstDestination = firstWrite.Write(first, BufferUse.CopyDestination);
                firstWrite.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
                    resources.Get(firstInput), 0, resources.Get(firstDestination), 0, 256));

                PassBuilder firstRead = builder.AddPass("first-read", QueueSelection.Copy);
                BufferAccess firstSource = firstRead.Read(first, BufferUse.CopySource);
                BufferAccess firstOutput = firstRead.Write(output, BufferUse.CopyDestination, new BufferRange(0, 256));
                firstRead.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
                    resources.Get(firstSource), 0, resources.Get(firstOutput), 0, 256));

                PassBuilder secondWrite = builder.AddPass("second-write", QueueSelection.Copy);
                BufferAccess secondInput = secondWrite.Read(source, BufferUse.CopySource);
                BufferAccess secondDestination = secondWrite.Write(second, BufferUse.CopyDestination);
                secondWrite.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
                    resources.Get(secondInput), 0, resources.Get(secondDestination), 0, 256));

                PassBuilder secondRead = builder.AddPass("second-read", QueueSelection.Copy);
                BufferAccess secondSource = secondRead.Read(second, BufferUse.CopySource);
                BufferAccess secondOutput = secondRead.Write(output, BufferUse.CopyDestination, new BufferRange(256, 256));
                secondRead.Execute((ICommandContext commands, in PassResources resources) => commands.CopyBuffer(
                    resources.Get(secondSource), 0, resources.Get(secondOutput), 256, 256));

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(5)));
                selectedAliasPlan = graph.Statistics.LastAliasing.Enabled;
                if (!selectedAliasPlan) Thread.Yield();
            }

            Assert.True(selectedAliasPlan);
            Assert.True(graph.Statistics.LastAliasing.AliasSavingsBytes > 0);
            Assert.True(
                graph.Statistics.LastAliasing.PlannedHeapBytes <
                graph.Statistics.LastAliasing.NonAliasedPlacedBytes);
            Assert.Equal(1, graph.Statistics.LastAliasing.AliasAcquireCount);
            byte[] actual = new byte[512];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(expected, actual.AsSpan(0, 256).ToArray());
            Assert.Equal(expected, actual.AsSpan(256, 256).ToArray());
            Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
                diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Reading_unproduced_transient_content_is_rejected()
    {

        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferId transient = builder.CreateBuffer(new BufferDesc(64, BufferUsage.CopySource | BufferUsage.CopyDestination));
        PassBuilder pass = builder.AddPass("invalid-read", new QueueSelection(QueueType.Copy));
        _ = pass.Read(transient, BufferUse.CopySource);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        InvalidOperationException? error = null;
        try
        {
            _ = graph.Execute(ref builder);
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }
        Assert.NotNull(error);
        Assert.Contains("has not been imported or fully produced", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_joins_async_flight_without_terminal_publication_or_eviction_events()
    {

        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });
        FrozenGraph frozen = CreateFrozenCopy(device);
        List<CompilationEvent> events = new();
        CompilationCache cache = new(
            device,
            16,
            1024 * 1024,
            true,
            events.Add,
            compilerPolicy: 1);

        CompiledGraphLease lease = cache.Acquire(frozen, device.Compilation);
        lease.Release();

        Assert.Equal(1, events.Count(item => item == CompilationEvent.FlightStarted));
        Assert.Equal(0, events.Count(item => item == CompilationEvent.CandidatePublished));
        cache.Dispose();
        Assert.Equal(0, events.Count(item => item == CompilationEvent.CandidatePublished));
        Assert.Equal(0, events.Count(item => item == CompilationEvent.EntryEvicted));
        Assert.Equal(0, events.Count(item => item == CompilationEvent.EntryRetired));
        Assert.Equal(0, cache.ResidentEntryCount);
    }

    private static FrozenGraph CreateFrozenCopy(IDevice device)
    {
        GraphRecording recording = new();
        BufferDesc sourceDesc = new(64, BufferUsage.CopySource);
        BufferDesc destinationDesc = new(64, BufferUsage.CopyDestination);
        BufferHandle sourceHandle = device.CreateBuffer(sourceDesc);
        BufferHandle destinationHandle = device.CreateBuffer(destinationDesc);
        BufferId source = recording.AddBuffer(
            sourceDesc,
            new ImportedBuffer(sourceHandle, device.GetBufferMetadata(sourceHandle), BufferUse.CopySource, BufferUse.CopySource, true));
        BufferId destination = recording.AddBuffer(
            destinationDesc,
            new ImportedBuffer(destinationHandle, device.GetBufferMetadata(destinationHandle), BufferUse.CopyDestination, BufferUse.CopyDestination, false));
        int pass = recording.AddPass("copy", new QueueSelection(QueueType.Copy));
        _ = recording.AddBufferAccess(pass, source, ResourceEffect.Read, BufferUse.CopySource, BufferRange.Whole, PriorContents.Required, WriteCoverage.Partial);
        _ = recording.AddBufferAccess(pass, destination, ResourceEffect.Write, BufferUse.CopyDestination, BufferRange.Whole, PriorContents.Discard, WriteCoverage.Full);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        FrozenGraph frozen = recording.Freeze(device);
        device.DestroyBuffer(destinationHandle);
        device.DestroyBuffer(sourceHandle);
        device.CollectGarbage();
        return frozen;
    }
}
