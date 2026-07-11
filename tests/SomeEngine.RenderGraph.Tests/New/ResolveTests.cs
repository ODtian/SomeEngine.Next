using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ResolveTests
{
    private static readonly TextureSubresourceRange ColorSubresource =
        new(0, 1, 0, 1, TextureAspect.Color);

    [Fact]
    public void Compiler_emits_exact_resolve_states_and_ranges()
    {
        using Device device = new();
        GraphRecording recording = new();
        TextureDesc sourceDesc = SourceDescription("barrier-source");
        TextureDesc destinationDesc = DestinationDescription("barrier-destination");
        TextureId source = recording.AddTexture(sourceDesc, default);
        TextureId destination = recording.AddTexture(destinationDesc, default);
        TextureViewId sourceView = recording.AddTextureView(
            source,
            ColorSubresource,
            TextureViewUsage.ColorAttachment,
            Format.Unknown,
            "barrier-source-rtv");

        int clearPass = recording.AddPass("clear-msaa", QueueSelection.Graphics);
        _ = recording.AddColorAttachment(clearPass, 0, sourceView, LoadAction.Clear, new Vector4(0.25f));
        recording.SetExecution(clearPass, Noop);

        int resolvePass = recording.AddPass("resolve-msaa", QueueSelection.Graphics);
        _ = recording.AddTextureAccess(
            resolvePass,
            source,
            ResourceEffect.Read,
            TextureUse.ResolveSource,
            ColorSubresource,
            PriorContents.Required,
            WriteCoverage.Partial);
        _ = recording.AddTextureAccess(
            resolvePass,
            destination,
            ResourceEffect.Write,
            TextureUse.ResolveDestination,
            ColorSubresource,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(resolvePass, Noop);

        BufferDesc outputDesc = new(4, BufferUsage.CopyDestination, "resolve-barrier-root");
        BufferHandle outputHandle = device.CreateBuffer(outputDesc);
        try
        {
            BufferId output = recording.AddBuffer(outputDesc, new ImportedBuffer(
                outputHandle,
                device.GetBufferMetadata(outputHandle),
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                ContentsAvailable: false));
            int copyPass = recording.AddPass("publish-resolve", QueueSelection.Graphics);
            _ = recording.AddTextureAccess(
                copyPass,
                destination,
                ResourceEffect.Read,
                TextureUse.CopySource,
                ColorSubresource,
                PriorContents.Required,
                WriteCoverage.Partial);
            _ = recording.AddBufferAccess(
                copyPass,
                output,
                ResourceEffect.Write,
                BufferUse.CopyDestination,
                BufferRange.Whole,
                PriorContents.Discard,
                WriteCoverage.Full);
            recording.SetExecution(copyPass, Noop);

            FrozenGraph frozen = recording.Freeze(device);
            CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: false);

            Assert.Contains(compiled.BeforeBarriers[resolvePass], barrier =>
                barrier.Kind == BarrierKind.Transition &&
                barrier.Resource == source.Ordinal &&
                barrier.Before == ResourceState.RenderTarget &&
                barrier.After == ResourceState.ResolveSource &&
                barrier.TextureRange == ColorSubresource);
            Assert.Contains(compiled.BeforeBarriers[resolvePass], barrier =>
                barrier.Kind == BarrierKind.Transition &&
                barrier.Resource == destination.Ordinal &&
                barrier.Before == ResourceState.Common &&
                barrier.After == ResourceState.ResolveDestination &&
                barrier.TextureRange == ColorSubresource);
            Assert.Contains(compiled.BeforeBarriers[copyPass], barrier =>
                barrier.Kind == BarrierKind.Transition &&
                barrier.Resource == destination.Ordinal &&
                barrier.Before == ResourceState.ResolveDestination &&
                barrier.After == ResourceState.CopySource &&
                barrier.TextureRange == ColorSubresource);
        }
        finally
        {
            device.DestroyBuffer(outputHandle);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Null_graph_clears_resolves_and_reads_back_msaa_color()
    {
        using Device device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });
        const int width = 3;
        const int height = 2;
        const float expected = 0.375f;
        TextureDesc sourceDesc = new(
            width,
            height,
            Format.R32Float,
            TextureUsage.ColorAttachment | TextureUsage.CopySource,
            SampleCount: 4,
            Name: "rg-resolve-source");
        TextureDesc destinationDesc = new(
            width,
            height,
            Format.R32Float,
            TextureUsage.CopyDestination | TextureUsage.CopySource,
            Name: "rg-resolve-destination");
        TextureCopyRegion copyRegion = new(0, 0, TextureAspect.Color, width, height);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(destinationDesc, copyRegion);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination, "rg-resolve-readback"),
            MemoryType.Readback);
        try
        {
            GraphBuilder builder = graph.Begin();
            TextureId source = builder.CreateTexture(sourceDesc);
            TextureId destination = builder.CreateTexture(destinationDesc);
            TextureViewId sourceView = builder.CreateTextureView(
                source,
                ColorSubresource,
                TextureViewUsage.ColorAttachment,
                name: "rg-resolve-source-rtv");
            BufferId output = builder.ImportBuffer(
                readback,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);

            PassBuilder clear = builder.AddPass("clear-msaa", QueueSelection.Graphics);
            _ = clear.ColorAttachment(0, sourceView, LoadAction.Clear, new Vector4(expected));
            clear.Execute(Noop);

            PassBuilder resolve = builder.AddPass("resolve-msaa", QueueSelection.Graphics);
            TextureAccess sourceAccess = resolve.Read(source, TextureUse.ResolveSource, ColorSubresource);
            TextureAccess destinationAccess = resolve.Write(
                destination,
                TextureUse.ResolveDestination,
                ColorSubresource);
            resolve.Execute((ICommandContext commands, in PassResources resources) =>
                commands.ResolveTexture(new TextureResolveRegion(
                    resources.Get(sourceAccess),
                    resources.Get(destinationAccess))));

            PassBuilder copy = builder.AddPass("readback-resolve", QueueSelection.Graphics);
            TextureAccess copySource = copy.Read(destination, TextureUse.CopySource, ColorSubresource);
            BufferAccess copyDestination = copy.Write(output, BufferUse.CopyDestination);
            copy.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyTextureToBuffer(new TextureBufferCopy(
                    resources.Get(copySource),
                    copyRegion,
                    resources.Get(copyDestination),
                    footprint.Layout)));

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.FromSeconds(1)));
            GpuCompletion completion = Assert.Single(execution.Completions);
            Assert.Equal(QueueType.Graphics, completion.Queue);

            byte[] actual = new byte[checked(width * height * sizeof(float))];
            device.ReadBuffer(readback, footprint.Layout.Offset, actual);
            for (int pixel = 0; pixel < width * height; pixel++)
                Assert.Equal(expected, BitConverter.ToSingle(actual, pixel * sizeof(float)));
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.CollectGarbage();
        }
    }

    [Theory]
    [InlineData(QueueType.Compute)]
    [InlineData(QueueType.Copy)]
    public void Live_resolve_declaration_rejects_non_graphics_queue(QueueType queue)
    {
        using Device device = new();
        TextureDesc sourceDesc = SourceDescription("queue-source");
        TextureDesc destinationDesc = DestinationDescription("queue-destination");
        TextureHandle sourceHandle = device.CreateTexture(sourceDesc);
        TextureHandle destinationHandle = device.CreateTexture(destinationDesc);
        try
        {
            using RenderGraph graph = new(device);
            GraphBuilder builder = graph.Begin();
            TextureId source = builder.ImportTexture(
                sourceHandle,
                TextureUse.ResolveSource,
                TextureUse.ResolveSource);
            TextureId destination = builder.ImportTexture(
                destinationHandle,
                TextureUse.ResolveDestination,
                TextureUse.ResolveDestination,
                contentsAvailable: false);
            PassBuilder resolve = builder.AddPass("invalid-resolve-queue", new QueueSelection(queue));
            _ = resolve.Read(source, TextureUse.ResolveSource, ColorSubresource);
            _ = resolve.Write(destination, TextureUse.ResolveDestination, ColorSubresource);
            resolve.Execute(Noop);

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            Assert.Contains($"selects {queue}", error.Message, StringComparison.Ordinal);
            Assert.Contains("ResolveSource", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyTexture(destinationHandle);
            device.DestroyTexture(sourceHandle);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void ResolveTexture_rejects_a_source_not_declared_with_resolve_use()
    {
        using Device device = new();
        TextureDesc sourceDesc = SourceDescription("undeclared-source");
        TextureDesc destinationDesc = DestinationDescription("undeclared-destination");
        TextureHandle sourceHandle = device.CreateTexture(sourceDesc);
        TextureHandle destinationHandle = device.CreateTexture(destinationDesc);
        try
        {
            using RenderGraph graph = new(device);
            GraphBuilder builder = graph.Begin();
            TextureId source = builder.ImportTexture(
                sourceHandle,
                TextureUse.CopySource,
                TextureUse.CopySource);
            TextureId destination = builder.ImportTexture(
                destinationHandle,
                TextureUse.ResolveDestination,
                TextureUse.ResolveDestination,
                contentsAvailable: false);
            PassBuilder resolve = builder.AddPass("undeclared-resolve-use", QueueSelection.Graphics);
            TextureAccess sourceAccess = resolve.Read(source, TextureUse.CopySource, ColorSubresource);
            TextureAccess destinationAccess = resolve.Write(
                destination,
                TextureUse.ResolveDestination,
                ColorSubresource);
            resolve.Execute((ICommandContext commands, in PassResources resources) =>
                commands.ResolveTexture(new TextureResolveRegion(
                    resources.Get(sourceAccess),
                    resources.Get(destinationAccess))));

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            Assert.Contains("declared Read/ResolveSource", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyTexture(destinationHandle);
            device.DestroyTexture(sourceHandle);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void ResolveTexture_rejects_a_captured_texture_outside_the_graph_invocation()
    {
        using Device device = new();
        TextureHandle capturedSource = device.CreateTexture(SourceDescription("captured-source"));
        TextureHandle destinationHandle = device.CreateTexture(DestinationDescription("captured-destination"));
        try
        {
            using RenderGraph graph = new(device);
            GraphBuilder builder = graph.Begin();
            TextureId destination = builder.ImportTexture(
                destinationHandle,
                TextureUse.ResolveDestination,
                TextureUse.ResolveDestination,
                contentsAvailable: false);
            PassBuilder resolve = builder.AddPass("captured-resolve-source", QueueSelection.Graphics);
            TextureAccess destinationAccess = resolve.Write(
                destination,
                TextureUse.ResolveDestination,
                ColorSubresource);
            resolve.Execute((ICommandContext commands, in PassResources resources) =>
                commands.ResolveTexture(new TextureResolveRegion(
                    capturedSource,
                    resources.Get(destinationAccess))));

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            Assert.Contains("not declared by this render-graph invocation", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyTexture(destinationHandle);
            device.DestroyTexture(capturedSource);
            device.CollectGarbage();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveTexture_rejects_source_or_destination_layer_outside_declared_range(bool sourceIsWrong)
    {
        using Device device = new();
        TextureDesc sourceDesc = SourceDescription("range-source") with { ArrayLayers = 2 };
        TextureDesc destinationDesc = DestinationDescription("range-destination") with { ArrayLayers = 2 };
        TextureHandle sourceHandle = device.CreateTexture(sourceDesc);
        TextureHandle destinationHandle = device.CreateTexture(destinationDesc);
        try
        {
            using RenderGraph graph = new(device);
            GraphBuilder builder = graph.Begin();
            TextureId source = builder.ImportTexture(
                sourceHandle,
                TextureUse.ResolveSource,
                TextureUse.ResolveSource);
            TextureId destination = builder.ImportTexture(
                destinationHandle,
                TextureUse.ResolveDestination,
                TextureUse.ResolveDestination,
                contentsAvailable: false);
            PassBuilder resolve = builder.AddPass("resolve-range-envelope", QueueSelection.Graphics);
            TextureAccess sourceAccess = resolve.Read(source, TextureUse.ResolveSource, ColorSubresource);
            TextureAccess destinationAccess = resolve.Write(
                destination,
                TextureUse.ResolveDestination,
                ColorSubresource);
            resolve.Execute((ICommandContext commands, in PassResources resources) =>
                commands.ResolveTexture(new TextureResolveRegion(
                    resources.Get(sourceAccess),
                    resources.Get(destinationAccess),
                    SourceArrayLayer: sourceIsWrong ? 1 : 0,
                    DestinationArrayLayer: sourceIsWrong ? 0 : 1)));

            InvalidOperationException error = ExecuteExpecting<InvalidOperationException>(graph, ref builder);

            string expectedUse = sourceIsWrong ? "Read/ResolveSource" : "Write/ResolveDestination";
            Assert.Contains($"declared {expectedUse}", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            device.DestroyTexture(destinationHandle);
            device.DestroyTexture(sourceHandle);
            device.CollectGarbage();
        }
    }

    private static TextureDesc SourceDescription(string name) => new(
        4,
        4,
        Format.R32Float,
        TextureUsage.ColorAttachment | TextureUsage.CopySource,
        SampleCount: 4,
        Name: name);

    private static TextureDesc DestinationDescription(string name) => new(
        4,
        4,
        Format.R32Float,
        TextureUsage.CopyDestination | TextureUsage.CopySource,
        Name: name);

    private static void Noop(ICommandContext unusedCommands, in PassResources unusedResources) { }

    private static TException ExecuteExpecting<TException>(RenderGraph graph, ref GraphBuilder builder)
        where TException : Exception
    {
        try
        {
            _ = graph.Execute(ref builder);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new Xunit.Sdk.XunitException($"Expected {typeof(TException).Name}.");
    }
}
