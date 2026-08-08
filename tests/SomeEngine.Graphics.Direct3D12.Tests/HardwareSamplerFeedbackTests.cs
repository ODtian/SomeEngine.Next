using System.Reflection;
using SomeEngine.Graphics.Direct3D12;
using Xunit;
using Xunit.Sdk;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class HardwareSamplerFeedbackTests
{
    [Theory]
    [SamplerFeedbackHardwareData]
    public void Clear_decode_and_parent_cascade_execute_on_advertised_hardware(
        bool hardwareAvailable)
    {
        Assert.True(hardwareAvailable);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateSamplerFeedbackHardwareDevice(backend);
        AssertClearDecodeAndParentCascade(backend, device);
    }

    internal static void AssertClearDecodeAndParentCascade(
        IGraphicsBackend backend,
        Device device)
    {
        Assert.True(backend.TryGetCapability(device, out SamplerFeedback? capability));
        Assert.NotNull(capability);
        Assert.True(capability.SupportedFormats.Contains(Format.R8G8B8A8UNorm));

        Texture sampled = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                32,
                32,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));
        SamplerFeedbackTexture feedback = backend.CreateSamplerFeedbackTexture(
            device,
            new SamplerFeedbackTextureDesc(
                sampled,
                SamplerFeedbackType.MinimumMip,
                4,
                4));
        TextureSubresourceRange feedbackRange = new(
            0,
            feedback.Info.MipLevelCount,
            0,
            feedback.Info.ArrayLayerCount,
            TextureAspects.Color);
        SamplerFeedbackUav uav = backend.CreateSamplerFeedbackUav(
            device,
            feedback,
            new TextureUavDesc(
                feedback,
                feedbackRange,
                Format.R8UInt,
                TextureViewDimension.Texture2D));

        const ulong decodedByteCount = 8 * 8;
        using Buffer decoded = backend.CreateBuffer(
            device,
            new BufferDesc(
                decodedByteCount,
                BufferUsages.CopyDestination | BufferUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(decodedByteCount, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            feedback,
            feedbackRange,
            PipelineSync.None,
            PipelineSync.Clear,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined,
            TextureLayout.UnorderedAccess));
        backend.ClearSamplerFeedback(context, uav);
        backend.Barrier(context, new TextureBarrier(
            feedback,
            feedbackRange,
            PipelineSync.Clear,
            PipelineSync.Resolve,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.ResolveSource,
            TextureLayout.UnorderedAccess,
            TextureLayout.ResolveSource));
        backend.Barrier(context, new BufferBarrier(
            decoded,
            PipelineSync.None,
            PipelineSync.Resolve,
            ResourceAccess.NoAccess,
            ResourceAccess.ResolveDestination));
        backend.ResolveSamplerFeedback(
            context,
            feedback,
            decoded,
            new BufferRange(0, decodedByteCount));
        backend.Barrier(context, new BufferBarrier(
            decoded,
            PipelineSync.Resolve,
            PipelineSync.Copy,
            ResourceAccess.ResolveDestination,
            ResourceAccess.CopySource));
        backend.CopyBuffer(
            context,
            new BufferCopy(decoded, 0, readback, 0, decodedByteCount));
        using RecordedCommands commands = backend.End(context);
        RecordedCommands[] batch = [commands];
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], batch, [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        using (MappedBuffer mapping = backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, decodedByteCount)))
        {
            mapping.Invalidate(new BufferRange(0, decodedByteCount));
            Assert.All(mapping.Bytes.ToArray(), static value => Assert.Equal(0xFF, value));
        }
        backend.CollectCompleted(device);

        sampled.Dispose();
        Assert.True(sampled.IsDisposed);
        Assert.True(feedback.IsDisposed);
        Assert.True(uav.IsDisposed);
        uav.Dispose();
        feedback.Dispose();
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class SamplerFeedbackHardwareDataAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        try
        {
            using IGraphicsBackend backend = new D3D12Backend();
            using Device _ = D3D12TestSupport.CreateSamplerFeedbackHardwareDevice(backend);
            return [[true]];
        }
        catch (Exception exception) when (
            exception is NotSupportedException or PlatformNotSupportedException)
        {
            Skip = exception.Message;
            return [];
        }
    }
}
