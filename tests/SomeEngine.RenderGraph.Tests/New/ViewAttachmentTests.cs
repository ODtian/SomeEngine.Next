using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ViewAttachmentTests
{
    [Fact]
    public void Transient_color_attachment_view_is_realized_before_worker_recording()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        TextureId texture = builder.CreateTexture(new TextureDesc(
            16,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource));
        TextureViewId view = builder.CreateTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment);
        PassBuilder pass = builder.AddPass("clear", QueueSelection.Graphics);
        output.Root(ref builder, ref pass);
        ColorAttachmentAccess color = pass.ColorAttachment(0, view, LoadAction.Clear, new Vector4(0.1f, 0.2f, 0.3f, 1f));
        int recordThread = 0;
        pass.Execute((ICommandContext commands, in PassResources resources) =>
        {
            recordThread = Environment.CurrentManagedThreadId;
            Assert.True(resources.Get(color).IsValid);
            Assert.Throws<InvalidOperationException>(() => commands.Barriers([]));
            Assert.Throws<InvalidOperationException>(() => commands.BeginRendering(default));
            Assert.Throws<InvalidOperationException>(() => commands.Finish());
        });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.True(execution.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotEqual(0, recordThread);
        Assert.Equal(1, device.Statistics.Submissions);
        Assert.True(device.Statistics.RecordedCommands >= 3);
    }

    [Fact]
    public void Shader_view_accesses_resolve_only_through_declared_pass_tokens()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferId buffer = builder.CreateBuffer(new BufferDesc(64, BufferUsage.ShaderWrite));
        BufferViewId bufferView = builder.CreateBufferView(
            buffer,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            stride: 16);
        TextureId texture = builder.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Storage));
        TextureViewId textureView = builder.CreateTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.Storage);

        PassBuilder produce = builder.AddPass("produce-views", new QueueSelection(QueueType.Compute));
        BufferViewAccess bufferWrite = produce.Write(bufferView);
        TextureViewAccess textureWrite = produce.Write(textureView);
        produce.Execute((ICommandContext _, in PassResources resources) =>
        {
            Assert.True(resources.Get(bufferWrite).IsValid);
            Assert.True(resources.Get(textureWrite).IsValid);
        });

        PassBuilder consume = builder.AddPass("consume-views", new QueueSelection(QueueType.Compute));
        output.Root(ref builder, ref consume);
        BufferViewAccess bufferRead = consume.Read(bufferView);
        TextureViewAccess textureRead = consume.Read(textureView);
        consume.Execute((ICommandContext _, in PassResources resources) =>
        {
            Assert.True(resources.Get(bufferRead).IsValid);
            Assert.True(resources.Get(textureRead).IsValid);
        });

        GraphExecution execution = graph.Execute(ref builder);
        Assert.True(execution.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, device.Statistics.Submissions);
        Assert.Equal(2, device.Statistics.SubmittedCommandLists);
    }

    [Fact]
    public void Color_attachment_slots_must_be_dense_from_zero()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        TextureId texture = builder.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment));
        TextureViewId view = builder.CreateTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment);
        PassBuilder pass = builder.AddPass("slot-gap", QueueSelection.Graphics);
        _ = pass.ColorAttachment(1, view, LoadAction.Clear);
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
        Assert.Contains("contiguous starting at zero", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void View_token_from_another_graph_is_rejected_during_authoring()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph firstGraph = new(device);
        using RenderGraph secondGraph = new(device);
        GraphBuilder first = firstGraph.Begin();
        TextureId firstTexture = first.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled));
        TextureViewId foreignView = first.CreateTextureView(
            firstTexture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource);

        GraphBuilder second = secondGraph.Begin();
        PassBuilder pass = second.AddPass("foreign-view", QueueSelection.Graphics);
        ArgumentException? error = null;
        try
        {
            _ = pass.Read(foreignView);
        }
        catch (ArgumentException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains("different graph invocation", error.Message, StringComparison.Ordinal);
        second.Dispose();
        first.Dispose();
    }

    [Fact]
    public void Attachment_clear_value_is_invocation_data_not_canonical_structure()
    {
        using NullDevice device = new(new NullOptions());
        FrozenGraph red = FreezeAttachment(device, new Vector4(1, 0, 0, 1));
        FrozenGraph green = FreezeAttachment(device, new Vector4(0, 1, 0, 1));

        Assert.True(red.Canonical.Equals(green.Canonical));
        Assert.Equal(red.Canonical.Bytes, green.Canonical.Bytes);
        Assert.NotEqual(red.Passes[0].ColorAttachments[0].ClearColor, green.Passes[0].ColorAttachments[0].ClearColor);
    }

    [Fact]
    public void Discard_attachment_invalidates_previously_available_contents()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        TextureDesc desc = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource);
        TextureHandle physical = device.CreateTexture(desc);
        try
        {
            GraphBuilder builder = graph.Begin();
            TextureId texture = builder.ImportTexture(
                physical,
                TextureUse.ColorAttachment,
                TextureUse.CopySource,
                contentsAvailable: true);
            TextureViewId view = builder.CreateTextureView(
                texture,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ColorAttachment);
            PassBuilder discard = builder.AddPass("discard", QueueSelection.Graphics);
            _ = discard.ColorAttachment(0, view, LoadAction.Discard);
            discard.Execute(static (ICommandContext _, in PassResources _) => { });
            PassBuilder read = builder.AddPass("read-after-discard", new QueueSelection(QueueType.Copy));
            _ = read.Read(texture, TextureUse.CopySource);
            read.Execute(static (ICommandContext _, in PassResources _) => { });

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
        finally
        {
            device.DestroyTexture(physical);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Imported_resource_from_another_device_domain_is_rejected_before_realization()
    {
        using NullDevice sourceDevice = new(new NullOptions());
        using NullDevice targetDevice = new(new NullOptions());
        using RenderGraph graph = new(targetDevice);
        BufferDesc desc = new(32, BufferUsage.CopySource);
        BufferHandle foreign = sourceDevice.CreateBuffer(desc);
        try
        {
            GraphBuilder builder = graph.Begin();
            ArgumentException? error = null;
            try
            {
                _ = builder.ImportBuffer(foreign, BufferUse.CopySource, BufferUse.CopySource);
            }
            catch (ArgumentException exception)
            {
                error = exception;
            }
            Assert.NotNull(error);
            Assert.Contains("cross-device", error.Message, StringComparison.Ordinal);
            builder.Dispose();
        }
        finally
        {
            sourceDevice.DestroyBuffer(foreign);
            sourceDevice.CollectGarbage();
        }
    }

    private static FrozenGraph FreezeAttachment(IDevice device, Vector4 clear)
    {
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(4, 4, Format.R8G8B8A8UNorm, TextureUsage.ColorAttachment),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment,
            Format.Unknown,
            "name-is-not-canonical");
        int pass = recording.AddPass("attachment", QueueSelection.Graphics);
        _ = recording.AddColorAttachment(pass, 0, view, LoadAction.Clear, clear);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        return recording.Freeze(device);
    }

    private sealed class ObservableOutput : IDisposable
    {
        private readonly NullDevice _device;
        private readonly BufferHandle _buffer;

        public ObservableOutput(NullDevice device)
        {
            _device = device;
            _buffer = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination));
        }

        public void Root(ref GraphBuilder builder, ref PassBuilder pass)
        {
            BufferId id = builder.ImportBuffer(
                _buffer,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            _ = pass.Write(id, BufferUse.CopyDestination);
        }

        public void Dispose()
        {
            _device.DestroyBuffer(_buffer);
            _device.CollectGarbage();
        }
    }
}
