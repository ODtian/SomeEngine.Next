using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpReceiverTests
{
    [Fact]
    public void Warp_copy_round_trips_upload_and_readback_memory()
    {
        Assert.True(OperatingSystem.IsWindows());
        using IGraphicsBackend backend = new D3D12Backend();
        byte[] source = CreatePattern(1024);

        byte[] result = D3D12TestSupport.ExecuteCopyChain(backend, source);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Generic_and_interface_receiver_chains_produce_identical_native_results()
    {
        Assert.True(OperatingSystem.IsWindows());
        byte[] source = CreatePattern(257);

        byte[] genericResult;
        using (var graphics = new Graphics<D3D12Backend>(new D3D12Backend()))
            genericResult = ExecuteGeneric(graphics, source);

        byte[] interfaceResult;
        using (IGraphicsBackend backend = new D3D12Backend())
            interfaceResult = ExecuteInterface(backend, source);

        Assert.Equal(source, genericResult);
        Assert.Equal(genericResult, interfaceResult);
    }

    [Fact]
    public void Stable_empty_submit_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        QueueSubmitDesc submit = new([], [], [], [], []);

        QueueCompletion warmup = backend.Submit(queue, submit);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueCompletion measured = backend.Submit(queue, submit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Stable_nonempty_submit_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using RecordedCommands recorded = backend.End(context);
        commands[0] = recorded;
        QueueSubmitDesc submit = new([], [], commands, [], []);

        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueCompletion measured = backend.Submit(queue, submit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_copy_frame_allocates_no_managed_memory_between_begin_and_submit()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 256));
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_rendering_frame_allocates_no_managed_memory_between_begin_and_submit()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                range,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var colors = new[]
        {
            new ColorAttachmentDesc(
                view,
                LoadType.Clear,
                StoreType.Store,
                new Vector4(0.25f, 0.5f, 0.75f, 1)),
        };
        var viewports = new[] { new Viewport(0, 0, 64, 64) };
        var scissors = new[] { new ScissorRect(0, 0, 64, 64) };
        var commands = new RecordedCommands[1];

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            range,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        backend.BeginRendering(context, new RenderingDesc(colors, null, 64, 64));
        backend.EndRendering(context);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.SetViewports(context, viewports);
        backend.SetScissors(context, scissors);
        backend.BeginRendering(context, new RenderingDesc(colors, null, 64, 64));
        backend.EndRendering(context);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Stable_clear_buffer_uses_retained_upload_storage_without_managed_allocation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(1024, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var commands = new RecordedCommands[1];
        const uint pattern = 0xA1B2C3D4;

        backend.Begin(context);
        backend.ClearBuffer(context, destination, BufferRange.Whole, pattern);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.ClearBuffer(context, destination, BufferRange.Whole, pattern);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        BufferRange readRange = new(0, 1024);
        using MappedBuffer mapping = backend.Map(destination, MapType.Read, readRange);
        mapping.Invalidate(readRange);
        for (int offset = 0; offset < mapping.Bytes.Length; offset += sizeof(uint))
            Assert.Equal(pattern, BitConverter.ToUInt32(mapping.Bytes.Slice(offset, sizeof(uint))));
    }

    [Fact]
    public void Stable_clear_texture_reuses_command_slot_attachment_descriptors()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                64,
                64,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        var commands = new RecordedCommands[1];
        Vector4 clear = new(0.125f, 0.25f, 0.5f, 1);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target,
            range,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.ClearTexture(context, target, range, clear);
        using (RecordedCommands warmupCommands = backend.End(context))
        {
            commands[0] = warmupCommands;
            QueueCompletion warmup = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], commands, [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(warmup, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        RecordedCommands recorded = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        backend.Begin(context);
        backend.ClearTexture(context, target, range, clear);
        recorded = backend.End(context);
        commands[0] = recorded;
        QueueCompletion measured = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorded.Dispose();
        Assert.Equal(0, allocated);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void State_shadow_uses_public_normalized_float_equality_and_one_native_setter()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        float firstNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0001));
        float secondNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_1234));
        Viewport[] firstViewport = [new(firstNaN, +0.0f, 64, 64, -0.0f, 1)];
        Viewport[] equivalentViewport = [new(secondNaN, -0.0f, 64, 64, +0.0f, 1)];
        ScissorRect[] scissors = [new(0, 0, 64, 64)];

        backend.Begin(context);
        backend.SetViewports(context, firstViewport);
        backend.SetViewports(context, equivalentViewport);
        backend.SetScissors(context, scissors);
        backend.SetScissors(context, scissors);
        using RecordedCommands commands = backend.End(context);

        D3D12CommandStatistics statistics = diagnostics!.GetCommandStatistics(commands);
        Assert.Equal(0, statistics.PipelineSetters);
        Assert.Equal(0, statistics.PersistentBindingSetters);
        Assert.Equal(1, statistics.ViewportSetters);
        Assert.Equal(1, statistics.ScissorSetters);
    }

    private static byte[] ExecuteGeneric<TBackend>(
        Graphics<TBackend> graphics,
        ReadOnlySpan<byte> source)
        where TBackend : class, IGraphicsBackend
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = graphics.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo adapter = Assert.Single(adapters, static value => !value.HardwareAccelerated);
        Assert.False(string.IsNullOrWhiteSpace(adapter.DriverVersion));
        Assert.NotEqual("unavailable", adapter.DriverVersion);
        DeviceQueueDesc[] queues = [new(QueueType.Copy)];

        using Device device = graphics.CreateDevice(new DeviceDesc(
            adapter.Id,
            RetirementType.Automatic,
            queues,
            label: "generic receiver proof"));
        using Buffer upload = graphics.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = graphics.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)source.Length), BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, checked((ulong)source.Length));
        using (MappedBuffer mapping = graphics.Map(upload, MapType.Write, range))
        {
            source.CopyTo(mapping.Bytes);
            mapping.Flush(range);
        }

        using CommandContext context = graphics.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        graphics.Begin(context);
        graphics.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        using RecordedCommands recorded = graphics.End(context);
        RecordedCommands[] commands = [recorded];
        QueueSubmitDesc submit = new([], [], commands, [], []);
        QueueCompletion completion = graphics.Submit(graphics.GetQueue(device, QueueType.Copy), submit);
        Assert.Equal(WaitStatus.Completed, graphics.WaitCpu(completion, TimeSpan.FromSeconds(10)));

        byte[] result = new byte[source.Length];
        using MappedBuffer read = graphics.Map(readback, MapType.Read, range);
        read.Invalidate(range);
        read.Bytes.CopyTo(result);
        graphics.CollectCompleted(device);
        return result;
    }

    private static byte[] ExecuteInterface(IGraphicsBackend backend, ReadOnlySpan<byte> source) =>
        D3D12TestSupport.ExecuteCopyChain(backend, source);

    private static byte[] CreatePattern(int length)
    {
        var result = new byte[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = unchecked((byte)(17 + index * 37));
        return result;
    }
}
