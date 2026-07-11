using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class DepthStencilTests
{
    [Fact]
    public void Depth_only_attachment_is_authored_realized_and_recorded()
    {
        using NullDevice device = new(new NullOptions());
        using ObservableOutput output = new(device);
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        TextureId texture = builder.CreateTexture(new TextureDesc(
            16,
            8,
            Format.D32Float,
            TextureUsage.DepthStencilAttachment));
        TextureViewId view = builder.CreateTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth),
            TextureViewUsage.DepthStencilAttachment);
        PassBuilder pass = builder.AddPass("depth-only", QueueSelection.Graphics);
        output.Root(ref builder, ref pass);
        DepthStencilAttachmentAccess attachment = pass.DepthStencilAttachment(
            view,
            new DepthAttachmentOps(LoadAction.Clear, ClearValue: 0.375f));
        bool recorded = false;
        pass.Execute((ICommandContext _, in PassResources resources) =>
        {
            recorded = true;
            Assert.True(attachment.IsValid);
            Assert.True(resources.Get(attachment.DepthAccess).IsValid);
            Assert.False(attachment.HasStencil);
        });

        GraphExecution execution = graph.Execute(ref builder);

        Assert.True(execution.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(recorded);
        Assert.Equal(1, device.Statistics.Submissions);
    }

    [Fact]
    public void Depth_stencil_clear_values_are_invocation_data_not_canonical_structure()
    {
        using NullDevice device = new(new NullOptions());
        FrozenGraph first = FreezeDepthStencilAttachment(device, 0.125f, 17);
        FrozenGraph second = FreezeDepthStencilAttachment(device, 0.875f, 231);

        Assert.True(first.Canonical.Equals(second.Canonical));
        Assert.Equal(first.Canonical.Bytes, second.Canonical.Bytes);
        FrozenDepthStencilAttachment firstAttachment = Assert.IsType<FrozenDepthStencilAttachment>(
            first.Passes[0].DepthStencilAttachment);
        FrozenDepthStencilAttachment secondAttachment = Assert.IsType<FrozenDepthStencilAttachment>(
            second.Passes[0].DepthStencilAttachment);
        Assert.NotEqual(firstAttachment.Depth!.Value.ClearValue, secondAttachment.Depth!.Value.ClearValue);
        Assert.NotEqual(firstAttachment.Stencil!.Value.ClearValue, secondAttachment.Stencil!.Value.ClearValue);
    }

    [Fact]
    public void Read_only_depth_stencil_planes_reject_non_load_operations_during_authoring()
    {
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(4, 4, Format.D24UNormS8UInt, TextureUsage.DepthStencilAttachment),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment,
            Format.Unknown,
            null);

        int depthPass = recording.AddPass("invalid-read-only-depth", QueueSelection.Graphics);
        ArgumentException depthError = Assert.Throws<ArgumentException>(() => recording.AddDepthStencilAttachment(
            depthPass,
            view,
            new DepthAttachmentOps(LoadAction.Clear, ReadOnly: true),
            null));
        Assert.Contains("read-only depth plane requires Load", depthError.Message, StringComparison.OrdinalIgnoreCase);

        int stencilPass = recording.AddPass("invalid-read-only-stencil", QueueSelection.Graphics);
        ArgumentException stencilError = Assert.Throws<ArgumentException>(() => recording.AddDepthStencilAttachment(
            stencilPass,
            view,
            null,
            new StencilAttachmentOps(LoadAction.Discard, ReadOnly: true)));
        Assert.Contains("read-only stencil plane requires Load", stencilError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FrozenGraph FreezeDepthStencilAttachment(IDevice device, float depthClear, byte stencilClear)
    {
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(4, 4, Format.D24UNormS8UInt, TextureUsage.DepthStencilAttachment),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth | TextureAspect.Stencil),
            TextureViewUsage.DepthStencilAttachment,
            Format.Unknown,
            "non-canonical-view-name");
        int pass = recording.AddPass("depth-stencil", QueueSelection.Graphics);
        _ = recording.AddDepthStencilAttachment(
            pass,
            view,
            new DepthAttachmentOps(LoadAction.Clear, ClearValue: depthClear),
            new StencilAttachmentOps(LoadAction.Clear, ClearValue: stencilClear));
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
