// PROPOSAL ONLY: not inserted, compiled, or executed.
// Candidate destination: tests/SomeEngine.RenderGraph.Tests/RasterCommandRecoveryTests.cs
// Raw RHI means the public receiver without RenderGraph. The ordinary ValidationLayer
// enables native diagnostics; normal End rejection is wrapper-level runtime evidence.
using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit.Abstractions;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RasterCommandRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void DiscardingOpenRenderingAllowsTheSameContextToSubmitAgain()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            IGraphicsBackend backend = support.Backend;
            using (Texture texture = backend.CreateTexture(support.Device, new TextureDesc(
                TextureDimension.Texture2D, 16, 16, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment)))
            {
                TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
                using ColorAttachmentView view = backend.CreateColorAttachmentView(support.Device,
                    new ColorAttachmentViewDesc(texture, range, Format.R8G8B8A8UNorm,
                        TextureViewDimension.Texture2D));
                using CommandContext context = backend.CreateCommandContext(support.Device,
                    new CommandContextDesc(QueueType.Graphics, 0, 1));
                ColorAttachmentDesc[] colors =
                [
                    new(view, LoadType.Clear, StoreType.Store, new Vector4(0.125f, 0.25f, 0.5f, 1f)),
                ];

                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range,
                    PipelineSync.None, PipelineSync.RenderTarget,
                    ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
                    TextureLayout.Undefined, TextureLayout.RenderTarget));
                backend.BeginRendering(context, new RenderingDesc(colors, null, 16, 16));
                Assert.Null(Record.Exception(() => backend.Discard(context)));

                // The first recording was never submitted, so the texture is still undefined.
                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range,
                    PipelineSync.None, PipelineSync.RenderTarget,
                    ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
                    TextureLayout.Undefined, TextureLayout.RenderTarget));
                backend.BeginRendering(context, new RenderingDesc(colors, null, 16, 16));
                backend.EndRendering(context);
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = backend.Submit(support.GraphicsQueue,
                    new QueueSubmitDesc([], [], [commands], [], []));
                Assert.NotEqual(default, completion);
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(support.Device);
            }

            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }

        Assert.Null(diagnostics.TeardownFailure);
    }

    [Fact]
    public void EndingOpenRenderingIsRejectedAndDiscardAllowsTheSameContextToSubmitAgain()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            IGraphicsBackend backend = support.Backend;
            using (Texture texture = backend.CreateTexture(support.Device, new TextureDesc(
                TextureDimension.Texture2D, 16, 16, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment)))
            {
                TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
                using ColorAttachmentView view = backend.CreateColorAttachmentView(support.Device,
                    new ColorAttachmentViewDesc(texture, range, Format.R8G8B8A8UNorm,
                        TextureViewDimension.Texture2D));
                using CommandContext context = backend.CreateCommandContext(support.Device,
                    new CommandContextDesc(QueueType.Graphics, 0, 1));
                ColorAttachmentDesc[] colors =
                [
                    new(view, LoadType.Clear, StoreType.Store, new Vector4(0.125f, 0.25f, 0.5f, 1f)),
                ];

                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range,
                    PipelineSync.None, PipelineSync.RenderTarget,
                    ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
                    TextureLayout.Undefined, TextureLayout.RenderTarget));
                backend.BeginRendering(context, new RenderingDesc(colors, null, 16, 16));
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using RecordedCommands unexpectedCommands = backend.End(context);
                });
                Assert.Null(Record.Exception(() => backend.Discard(context)));

                backend.Begin(context);
                backend.Barrier(context, new TextureBarrier(texture, range,
                    PipelineSync.None, PipelineSync.RenderTarget,
                    ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
                    TextureLayout.Undefined, TextureLayout.RenderTarget));
                backend.BeginRendering(context, new RenderingDesc(colors, null, 16, 16));
                backend.EndRendering(context);
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = backend.Submit(support.GraphicsQueue,
                    new QueueSubmitDesc([], [], [commands], [], []));
                Assert.NotEqual(default, completion);
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(support.Device);
            }

            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }

        Assert.Null(diagnostics.TeardownFailure);
    }

    private D3D12Diagnostics RequireNativeValidation(WarpGraphTestSupport support)
    {
        if (!support.Backend.TryGetCapability(support.Device, out D3D12Diagnostics? diagnostics) ||
            diagnostics is null)
        {
            throw new NotSupportedException(
                "BLOCKED_NATIVE_VALIDATION: public D3D12 diagnostics are unavailable.");
        }

        output.WriteLine(
            "DebugLayer={0}; GBV={1}; SynchronizedQueueValidation={2}; DRED={3}",
            diagnostics.DebugLayerEnabled,
            diagnostics.GpuBasedValidationEnabled,
            diagnostics.SynchronizedQueueValidationEnabled,
            diagnostics.DredEnabled);
        if (!diagnostics.DebugLayerEnabled ||
            !diagnostics.GpuBasedValidationEnabled ||
            !diagnostics.SynchronizedQueueValidationEnabled)
        {
            throw new NotSupportedException(
                "BLOCKED_NATIVE_VALIDATION: debug layer, GBV and synchronized queue validation are required.");
        }

        return diagnostics;
    }
}
