using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCommonLayoutBarrierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Queue_specific_common_access_transitions_close_and_submit(bool validation)
    {
        using IGraphicsBackend backend = CreateBackend(validation);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        TextureDesc description = new(
            TextureDimension.Texture2D,
            16,
            16,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.Storage);
        using Texture first = backend.CreateTexture(device, description);
        using Texture second = backend.CreateTexture(device, description);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);

        backend.Begin(context);
        TextureBarrier[] activation =
        [
            new(
                first,
                range,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess,
                TextureLayout.Undefined,
                TextureLayout.General),
            new(
                second,
                range,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess,
                TextureLayout.Undefined,
                TextureLayout.General),
        ];
        backend.Barrier(context, new BarrierBatch([], [], [], activation, []));
        TextureBarrier[] access =
        [
            new(
                first,
                range,
                PipelineSync.ComputeShading,
                PipelineSync.PixelShading,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.ShaderResource,
                TextureLayout.General,
                TextureLayout.General),
            new(
                second,
                range,
                PipelineSync.ComputeShading,
                PipelineSync.PixelShading,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.ShaderResource,
                TextureLayout.General,
                TextureLayout.General),
        ];
        backend.Barrier(context, new BarrierBatch([], [], [], access, []));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));

        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
    }

    private static IGraphicsBackend CreateBackend(bool validation)
    {
        IGraphicsBackend native = D3D12GraphicsBackend.Create(new D3D12BackendOptions(
            UseQueueSpecificCommonLayouts: true));
        if (!validation)
            return native;
        return new ValidationLayer(
            native,
            new ValidationOptions(new DelegateValidationMessageSink(static _ => { })));
    }
}
