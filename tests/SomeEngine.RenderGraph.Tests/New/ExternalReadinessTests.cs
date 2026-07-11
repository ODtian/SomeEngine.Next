using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ExternalReadinessTests
{
    [Fact]
    public void Pending_published_readiness_is_forwarded_to_first_cross_queue_use_without_cpu_wait()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferDesc desc = new(64, BufferUsage.CopySource, "external-ready-buffer");
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferHandle output = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        GpuCompletion producer = Transition(
            device,
            QueueType.Copy,
            buffer.Resource,
            ResourceState.Common,
            ResourceState.CopySource);

        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferId imported = builder.ImportBuffer(
            buffer,
            BufferUse.CopySource,
            BufferUse.CopySource,
            readiness: new GpuCompletionSet([producer]));
        BufferId observable = builder.ImportBuffer(
            output,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("consume-ready-buffer", new QueueSelection(QueueType.Graphics));
        _ = pass.Read(imported, BufferUse.CopySource);
        _ = pass.Write(observable, BufferUse.CopyDestination);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.Equal(0UL, device.GetCompletedValue(QueueType.Copy));
        Assert.Equal(1, device.Statistics.SubmissionWaits);
        GpuCompletion graphCompletion = Assert.Single(execution.Completions);
        Assert.Equal(device.Domain, execution.CompletionSet.Domain);

        device.DestroyBuffer(output);
        device.DestroyBuffer(buffer);
        device.AdvanceCompletion(producer);
        device.AdvanceCompletion(graphCompletion);
        Assert.True(device.CollectGarbage() >= 1);
    }

    [Fact]
    public void Import_rejects_readiness_from_another_device_before_freeze()
    {
        using Device owner = new();
        using Device foreign = new();
        BufferDesc desc = new(64, BufferUsage.CopySource);
        BufferHandle buffer = owner.CreateBuffer(desc);
        GpuCompletion foreignCompletion = SubmitEmpty(foreign, QueueType.Copy);
        using RenderGraph graph = new(owner);
        GraphBuilder builder = graph.Begin();

        ArgumentException? error = null;
        try
        {
            _ = builder.ImportBuffer(
                buffer,
                BufferUse.CopySource,
                BufferUse.CopySource,
                readiness: new GpuCompletionSet([foreignCompletion]));
        }
        catch (ArgumentException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);

        builder.Dispose();
        owner.DestroyBuffer(buffer);
    }

    [Fact]
    public void Freeze_rejects_a_completion_value_the_device_has_not_published()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferDesc desc = new(64, BufferUsage.CopySource);
        BufferHandle buffer = device.CreateBuffer(desc);
        GpuCompletion unpublished = new(device.Domain, QueueType.Copy, 99);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferId imported = builder.ImportBuffer(
            buffer,
            BufferUse.CopySource,
            BufferUse.CopySource,
            readiness: new GpuCompletionSet([unpublished]));
        PassBuilder pass = builder.AddPass("invalid-readiness", new QueueSelection(QueueType.Copy));
        _ = pass.Read(imported, BufferUse.CopySource);
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        ArgumentException? error = null;
        try
        {
            _ = graph.Execute(ref builder);
        }
        catch (ArgumentException exception)
        {
            error = exception;
        }
        Assert.NotNull(error);
        device.DestroyBuffer(buffer);
    }

    [Fact]
    public void Readiness_values_are_invocation_data_but_queue_shape_is_canonical()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        GpuCompletion first = SubmitEmpty(device, QueueType.Copy);
        GpuCompletion second = SubmitEmpty(device, QueueType.Copy);
        GpuCompletion differentQueue = SubmitEmpty(device, QueueType.Graphics);
        FrozenGraph firstGraph = FreezeImport(device, first);
        FrozenGraph secondGraph = FreezeImport(device, second);
        FrozenGraph differentQueueGraph = FreezeImport(device, differentQueue);

        Assert.True(firstGraph.Canonical.Equals(secondGraph.Canonical));
        Assert.False(firstGraph.Canonical.Equals(differentQueueGraph.Canonical));
        GpuCompletion detached = Assert.Single(
            firstGraph.DetachForCompilation().Resources[0].ImportedBuffer.Readiness!);
        Assert.False(detached.IsValid);
        Assert.Equal(QueueType.Copy, detached.Queue);
        Assert.Equal(0UL, detached.Value);
    }

    [Fact]
    public void Execution_completion_set_can_feed_the_next_graph_import_directly()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferDesc desc = new(64, BufferUsage.CopyDestination | BufferUsage.CopySource);
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferHandle output = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        GpuCompletion initialState = Transition(
            device,
            QueueType.Copy,
            buffer.Resource,
            ResourceState.Common,
            ResourceState.CopyDestination);
        using RenderGraph graph = new(device);

        GraphBuilder producerBuilder = graph.Begin();
        BufferId produced = producerBuilder.ImportBuffer(
            buffer,
            BufferUse.CopyDestination,
            BufferUse.CopySource,
            contentsAvailable: false,
            readiness: new GpuCompletionSet([initialState]));
        PassBuilder producerPass = producerBuilder.AddPass("produce", new QueueSelection(QueueType.Copy));
        _ = producerPass.Write(produced, BufferUse.CopyDestination);
        producerPass.Execute(static (ICommandContext _, in PassResources _) => { });
        GraphExecution producerExecution = graph.Execute(ref producerBuilder);

        GraphBuilder consumerBuilder = graph.Begin();
        BufferId consumed = consumerBuilder.ImportBuffer(
            buffer,
            BufferUse.CopySource,
            BufferUse.CopySource,
            readiness: producerExecution.CompletionSet);
        BufferId observable = consumerBuilder.ImportBuffer(
            output,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        PassBuilder consumerPass = consumerBuilder.AddPass("consume", new QueueSelection(QueueType.Graphics));
        _ = consumerPass.Read(consumed, BufferUse.CopySource);
        _ = consumerPass.Write(observable, BufferUse.CopyDestination);
        consumerPass.Execute(static (ICommandContext _, in PassResources _) => { });
        GraphExecution consumerExecution = graph.Execute(ref consumerBuilder);

        Assert.Equal(2, device.Statistics.SubmissionWaits);
        device.DestroyBuffer(output);
        device.DestroyBuffer(buffer);
        device.AdvanceCompletion(initialState);
        foreach (GpuCompletion completion in producerExecution.Completions) device.AdvanceCompletion(completion);
        foreach (GpuCompletion completion in consumerExecution.Completions) device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 1);
    }

    [Fact]
    public void Disjoint_texture_subresources_wait_on_every_queue_that_first_uses_the_import()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        TextureDesc desc = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource,
            MipLevels: 2,
            Name: "external-ready-mips");
        TextureHandle texture = device.CreateTexture(desc);
        BufferHandle graphicsOutput = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        BufferHandle computeOutput = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        TextureSubresourceRange whole = new(0, 2, 0, 1, TextureAspect.Color);
        GpuCompletion producer = Transition(
            device,
            QueueType.Copy,
            texture.Resource,
            ResourceState.Common,
            ResourceState.CopySource,
            whole);

        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        TextureId imported = builder.ImportTexture(
            texture,
            TextureUse.CopySource,
            TextureUse.CopySource,
            readiness: new GpuCompletionSet([producer]));
        BufferId observableGraphics = builder.ImportBuffer(
            graphicsOutput,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        BufferId observableCompute = builder.ImportBuffer(
            computeOutput,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        PassBuilder graphics = builder.AddPass("read-mip-0", new QueueSelection(QueueType.Graphics));
        _ = graphics.Read(imported, TextureUse.CopySource, new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color));
        _ = graphics.Write(observableGraphics, BufferUse.CopyDestination);
        graphics.Execute(static (ICommandContext _, in PassResources _) => { });
        PassBuilder compute = builder.AddPass("read-mip-1", new QueueSelection(QueueType.Compute));
        _ = compute.Read(imported, TextureUse.CopySource, new TextureSubresourceRange(1, 1, 0, 1, TextureAspect.Color));
        _ = compute.Write(observableCompute, BufferUse.CopyDestination);
        compute.Execute(static (ICommandContext _, in PassResources _) => { });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.Equal(2, device.Statistics.SubmissionWaits);
        Assert.Equal(2, execution.Completions.Count);
        device.DestroyBuffer(computeOutput);
        device.DestroyBuffer(graphicsOutput);
        device.DestroyTexture(texture);
        device.AdvanceCompletion(producer);
        foreach (GpuCompletion completion in execution.Completions) device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 1);
    }

    private static FrozenGraph FreezeImport(Device device, GpuCompletion readiness)
    {
        GraphRecording recording = new();
        BufferDesc desc = new(64, BufferUsage.CopySource);
        BufferHandle handle = device.CreateBuffer(desc);
        BufferMetadata metadata = device.GetBufferMetadata(handle);
        BufferId imported = recording.AddBuffer(
            desc,
            new ImportedBuffer(handle, metadata, BufferUse.CopySource, BufferUse.CopySource, true, [readiness]));
        int pass = recording.AddPass("same-shape", new QueueSelection(QueueType.Copy));
        _ = recording.AddBufferAccess(
            pass,
            imported,
            ResourceEffect.Read,
            BufferUse.CopySource,
            BufferRange.Whole,
            PriorContents.Required,
            WriteCoverage.Partial);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        FrozenGraph frozen = recording.Freeze(device);
        device.DestroyBuffer(handle);
        device.CollectGarbage();
        return frozen;
    }

    private static GpuCompletion SubmitEmpty(Device device, QueueType queue)
    {
        using ICommandContext commands = device.AcquireCommandContext(queue);
        return device.Submit(queue, [commands.Finish()]);
    }

    private static GpuCompletion Transition(
        Device device,
        QueueType queue,
        ResourceHandle resource,
        ResourceState before,
        ResourceState after,
        TextureSubresourceRange textureRange = default)
    {
        using ICommandContext commands = device.AcquireCommandContext(queue);
        commands.Barriers([ResourceBarrier.Transition(resource, before, after, textureRange)]);
        return device.Submit(queue, [commands.Finish()]);
    }
}
