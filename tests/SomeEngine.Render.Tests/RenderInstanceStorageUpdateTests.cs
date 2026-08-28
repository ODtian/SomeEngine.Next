using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceStorageUpdateTests
{
    [Fact]
    public void PartialRewritePreservesUntouchedPropertiesAcrossUploadGenerations()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RenderInstancePropertyLayout layout = CreateLayout(out var first, out var second);
        RenderInstancePropertyLayout firstOnly = CreateSubset(layout, first.Key);
        ResolvedRenderInstanceProperty<uint> firstUpdate =
            firstOnly.Resolve<uint>(first.Key);

        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = CreateWarpDevice(backend);
        using var coordinator = new RenderFrameCoordinator(backend, device);
        RenderWorld world = new();
        var instances = new RenderInstanceStorageSystem(
            backend,
            device,
            coordinator,
            world,
            layout,
            new RenderInstanceOptions
            {
                RowCapacity = 8,
                BatchCapacity = 4,
            });

        RenderInstanceBatch batch;
        Assert.True(coordinator.TryBeginPrepare(out RenderPrepareScope? firstPrepare));
        using (firstPrepare)
        {
            using (RenderInstanceWriteScope write = instances.OpenWrite(firstPrepare))
            using (RenderInstanceWriteHandle handle = instances.AllocateBatch(
                       firstPrepare,
                       write,
                       layout,
                       instanceCount: 2))
            {
                RenderInstanceWriteSlice destination = handle.OpenWrite(layout);
                destination.BindPerInstance(first);
                destination.BindPerInstance(second);
                destination.Write(first, new uint[] { 1u, 2u });
                destination.Write(second, new uint[] { 10u, 20u });
                batch = handle.Publish();
            }
            firstPrepare.Commit();
        }

        Assert.True(coordinator.TryBeginPrepare(out RenderPrepareScope? secondPrepare));
        using (secondPrepare)
        {
            using (RenderInstanceWriteScope write = instances.OpenWrite(secondPrepare))
            using (RenderInstanceWriteHandle handle = instances.RewriteBatch(
                       secondPrepare,
                       write,
                       batch,
                       firstOnly))
            {
                RenderInstanceWriteSlice destination = handle.OpenWrite(firstOnly);
                destination.BindPerInstance(firstUpdate);
                destination.Slice(1, 1).Write(firstUpdate, new uint[] { 7u });
                _ = handle.Publish();
            }
            secondPrepare.Commit();
        }

        Assert.Equal(1u, instances.Storage.ReadInstance(batch, 0, first));
        Assert.Equal(7u, instances.Storage.ReadInstance(batch, 1, first));
        Assert.Equal(10u, instances.Storage.ReadInstance(batch, 0, second));
        Assert.Equal(20u, instances.Storage.ReadInstance(batch, 1, second));

        Assert.True(coordinator.TryBeginPrepare(out RenderPrepareScope? shutdown));
        using (shutdown)
        {
            using (RenderInstanceWriteScope write = instances.OpenWrite(shutdown))
                instances.Retire(shutdown, write, batch);
            instances.Shutdown(shutdown);
            shutdown.Commit();
        }
        instances.Dispose();
    }

    private static RenderInstancePropertyLayout CreateLayout(
        out ResolvedRenderInstanceProperty<uint> first,
        out ResolvedRenderInstanceProperty<uint> second)
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<uint> firstProperty = builder.Register<uint>(
            "SomeEngine.Render.Tests.First",
            new RenderInstancePropertyKey("test.instance.first"),
            UInt32Encoding());
        RenderInstanceProperty<uint> secondProperty = builder.Register<uint>(
            "SomeEngine.Render.Tests.Second",
            new RenderInstancePropertyKey("test.instance.second"),
            UInt32Encoding());
        RenderInstancePropertyLayout layout = builder.Freeze();
        first = layout.Resolve(firstProperty);
        second = layout.Resolve(secondProperty);
        return layout;
    }

    private static RenderInstancePropertyLayout CreateSubset(
        RenderInstancePropertyLayout source,
        RenderInstancePropertyKey key)
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Include(source.Require(key, nameof(key)));
        return builder.Freeze();
    }

    private static RenderInstancePropertyEncoding UInt32Encoding() => new(
        "test.instance.uint32.v1",
        valueSize: sizeof(uint),
        storageAlignment: sizeof(uint),
        storageStride: sizeof(uint),
        metadataWordCount: 1);

    private static Device CreateWarpDevice(IGraphicsBackend backend)
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
        AdapterInfo warp = adapters.FirstOrDefault(
            static adapter => !adapter.HardwareAccelerated);
        if (string.IsNullOrWhiteSpace(warp.Name))
            throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
        return backend.CreateDevice(new DeviceDesc(
            warp.Id,
            [
                new DeviceQueueDesc(QueueType.Graphics),
                new DeviceQueueDesc(QueueType.Compute),
                new DeviceQueueDesc(QueueType.Copy),
            ],
            label: "Render instance partial-update test device"));
    }
}
