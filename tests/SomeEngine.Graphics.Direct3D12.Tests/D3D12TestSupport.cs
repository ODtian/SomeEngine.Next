using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static class D3D12TestSupport
{
    internal static AdapterInfo SelectWarp(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        if (count == 0)
            throw new NotSupportedException("No Direct3D 12 adapter is available.");

        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }

        return adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated) is { Name.Length: > 0 } warp
            ? warp
            : throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
    }

    internal static Device CreateWarpDevice(IGraphicsBackend backend)
    {
        AdapterInfo adapter = SelectWarp(backend);
        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        return backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            optionalFeatures: DeviceFeatures.Presentation |
                              DeviceFeatures.SparseResources |
                              DeviceFeatures.SamplerFeedback |
                              DeviceFeatures.Residency |
                              DeviceFeatures.RayTracing |
                              DeviceFeatures.MeshShaders |
                              DeviceFeatures.VariableRateShading |
                              DeviceFeatures.WorkGraphs |
                              DeviceFeatures.IndirectCommands |
                              DeviceFeatures.CalibratedTimestamps |
                              DeviceFeatures.LinkedAdapters |
                              DeviceFeatures.ExternalResources |
                              DeviceFeatures.ExternalTimelines,
            label: "WARP integration device"));
    }

    internal static Device CreateSamplerFeedbackHardwareDevice(
        IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        AdapterInfo[] adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }

        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        List<string> failures = [];
        foreach (AdapterInfo adapter in adapters)
        {
            if (!adapter.HardwareAccelerated)
                continue;
            try
            {
                return backend.CreateDevice(new DeviceDesc(
                    adapter.Id,
                    queues,
                    requiredFeatures: DeviceFeatures.SamplerFeedback,
                    label: "sampler-feedback hardware integration device"));
            }
            catch (GraphicsException exception)
            {
                failures.Add(
                    $"{adapter.Name} [{adapter.Id.Low:X16}:{adapter.Id.High:X16}]: " +
                    exception.Message);
            }
        }

        throw new NotSupportedException(
            "No enumerated hardware D3D12 adapter exposes sampler feedback. " +
            string.Join(" | ", failures));
    }

    internal static byte[] ExecuteCopyChain(IGraphicsBackend backend, ReadOnlySpan<byte> source)
    {
        using Device device = CreateWarpDevice(backend);
        return ExecuteCopyChain(backend, device, source);
    }

    internal static byte[] ExecuteCopyChain(
        IGraphicsBackend backend,
        Device device,
        ReadOnlySpan<byte> source)
    {
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopySource, "copy upload"),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopyDestination, "copy readback"),
            MemoryType.Readback);
        BufferRange range = new(0, checked((ulong)source.Length));
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, range))
        {
            source.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1, Label: "copy context"));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueSubmitDesc submit = new([], [], commands, [], []);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        QueueCompletion completion = backend.Submit(queue, submit);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        byte[] result = new byte[source.Length];
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(result);
        }
        backend.CollectCompleted(device);
        return result;
    }

    internal static void UploadSinglePixelR32Float(
        IGraphicsBackend backend,
        Device device,
        Texture texture,
        float value)
    {
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource, "single-pixel upload"),
            MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapping.Bytes.Clear();
            BitConverter.GetBytes(value).CopyTo(mapping.Bytes);
            mapping.Flush(new BufferRange(0, 4));
        }

        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1, Label: "single-pixel upload"));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination,
            TextureLayout.Undefined,
            TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(context, new BufferTextureCopy(
            upload,
            0,
            256,
            1,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            1,
            1,
            1));
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.Copy,
            PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination,
            ResourceAccess.ShaderResource,
            TextureLayout.CopyDestination,
            TextureLayout.ShaderResource));
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }
}
