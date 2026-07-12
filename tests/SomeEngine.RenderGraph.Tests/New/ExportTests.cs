using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ExportTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Extracted_resource_publishes_only_after_completion_and_transfers_ownership_once()
    {
        using NullDevice device = new(new NullOptions { AutoCompleteSubmissions = false });
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        device.WriteBuffer(upload, 0, new byte[] { 3, 1, 4, 1 });
        ResourceExport published = default;
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId output = builder.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource | BufferUsage.CopyDestination));
            PassBuilder pass = builder.AddPass("export-producer", QueueSelection.Copy);
            BufferAccess read = pass.Read(source, BufferUse.CopySource);
            BufferAccess write = pass.Write(output, BufferUse.CopyDestination);
            pass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(read), 0, resources.Get(write), 0, 4));
            builder.Export(output);

            GraphExecution execution = graph.Execute(ref builder);
            Assert.Throws<InvalidOperationException>(() => _ = execution.Exports);
            Assert.Equal(1, graph.Statistics.LastCulling.LivePasses);
            Assert.Equal(0, graph.Statistics.LastCulling.CulledPasses);
            foreach (GpuCompletion completion in execution.Completions) device.AdvanceCompletion(completion);
            published = Assert.Single(execution.Exports);
            Assert.Same(execution.Exports, execution.Exports);
            Assert.True(published.IsBuffer);
            Assert.Equal(ResourceState.Common, published.FinalState);
            Assert.All(published.Completion.Completions, completion =>
                Assert.True(device.GetCompletedValue(completion.Queue) >= completion.Value));
        }
        finally
        {
            if (published.Buffer.IsValid) device.DestroyBuffer(published.Buffer);
            device.DestroyBuffer(upload);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Export_is_a_culling_root_and_unexported_work_is_removed()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        device.WriteBuffer(upload, 0, new byte[] { 2, 4, 6, 8 });
        ResourceExport published = default;
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId exported = builder.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource | BufferUsage.CopyDestination));
            BufferId dead = builder.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination));

            PassBuilder livePass = builder.AddPass("export-root", QueueSelection.Copy);
            BufferAccess liveRead = livePass.Read(source, BufferUse.CopySource);
            BufferAccess liveWrite = livePass.Write(exported, BufferUse.CopyDestination);
            livePass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(liveRead), 0, resources.Get(liveWrite), 0, 4));

            PassBuilder deadPass = builder.AddPass("unexported-dead-work", QueueSelection.Copy);
            BufferAccess deadRead = deadPass.Read(source, BufferUse.CopySource);
            BufferAccess deadWrite = deadPass.Write(dead, BufferUse.CopyDestination);
            deadPass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(deadRead), 0, resources.Get(deadWrite), 0, 4));
            builder.Export(exported);

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.Zero));
            published = Assert.Single(execution.Exports);
            Assert.Equal(1, graph.Statistics.LastCulling.LivePasses);
            Assert.Equal(1, graph.Statistics.LastCulling.CulledPasses);
        }
        finally
        {
            if (published.Buffer.IsValid) device.DestroyBuffer(published.Buffer);
            device.DestroyBuffer(upload);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Published_export_can_be_reimported_with_its_exact_completion_and_final_state()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        device.WriteBuffer(upload, 0, new byte[] { 2, 7, 1, 8 });
        ResourceExport export = default;
        try
        {
            GraphBuilder produce = graph.Begin();
            BufferId source = produce.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId output = produce.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource | BufferUsage.CopyDestination));
            PassBuilder producer = produce.AddPass("produce-export", QueueSelection.Copy);
            BufferAccess input = producer.Read(source, BufferUse.CopySource);
            BufferAccess result = producer.Write(output, BufferUse.CopyDestination);
            producer.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(input), 0, resources.Get(result), 0, 4));
            produce.Export(output);
            GraphExecution first = graph.Execute(ref produce);
            Assert.True(first.Wait(TimeSpan.Zero));
            export = Assert.Single(first.Exports);

            GraphBuilder consume = graph.Begin();
            BufferId exported = consume.ImportBuffer(export, BufferUse.CopySource);
            BufferId destination = consume.ImportBuffer(readback, BufferUse.CopyDestination, BufferUse.CopyDestination, contentsAvailable: false);
            PassBuilder copy = consume.AddPass("consume-export", QueueSelection.Copy);
            BufferAccess exportedRead = copy.Read(exported, BufferUse.CopySource);
            BufferAccess destinationWrite = copy.Write(destination, BufferUse.CopyDestination);
            copy.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(resources.Get(exportedRead), 0, resources.Get(destinationWrite), 0, 4));
            GraphExecution second = graph.Execute(ref consume);
            Assert.True(second.Wait(TimeSpan.Zero));

            byte[] actual = new byte[4];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(new byte[] { 2, 7, 1, 8 }, actual);
        }
        finally
        {
            if (export.Buffer.IsValid) device.DestroyBuffer(export.Buffer);
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Failed_invocation_never_publishes_an_export()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions { CompileOptimizedPlansAsynchronously = false });
        GraphBuilder builder = graph.Begin();
        BufferId output = builder.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination));
        PassBuilder producer = builder.AddPass("failed-export", QueueSelection.Copy);
        _ = producer.Write(output, BufferUse.CopyDestination);
        producer.Execute(static (ICommandContext _, in PassResources _) =>
            throw new InvalidOperationException("expected export failure"));
        builder.Export(output);

        Exception? error = null;
        try
        {
            _ = graph.Execute(ref builder);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains("expected export failure", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, device.Statistics.BufferCreates);
        Assert.True(device.CollectGarbage() >= 1);
    }
}
