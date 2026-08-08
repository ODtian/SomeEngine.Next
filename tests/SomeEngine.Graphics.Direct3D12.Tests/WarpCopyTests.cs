using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCopyTests
{
    [Fact]
    public void Copy_footprints_report_exact_uncompressed_and_block_rows()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        BufferTextureCopy rgbaCopy = new(
            null!,
            0,
            0,
            0,
            null!,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            8,
            4,
            1);
        TextureCopyFootprint rgba = backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination),
            rgbaCopy,
            requestedBufferOffset: 512);
        Assert.Equal(512UL, rgba.Offset);
        Assert.Equal(256u, rgba.RowPitch);
        Assert.Equal(4u, rgba.RowCount);
        Assert.Equal(32UL, rgba.RowSize);
        Assert.Equal(800UL, rgba.TotalSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination),
            rgbaCopy,
            requestedBufferOffset: 1));

        BufferTextureCopy bcCopy = rgbaCopy with { Width = 8, Height = 8 };
        TextureCopyFootprint bc = backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.BC1UNorm,
                TextureUsages.CopyDestination),
            bcCopy);
        Assert.Equal(0UL, bc.Offset);
        Assert.Equal(256u, bc.RowPitch);
        Assert.Equal(2u, bc.RowCount);
        Assert.Equal(16UL, bc.RowSize);
        Assert.Equal(272UL, bc.TotalSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture3D,
                8,
                8,
                8,
                1,
                1,
                1,
                Format.R8UNorm,
                TextureUsages.CopyDestination),
            rgbaCopy with { ArrayLayer = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8UNorm,
                TextureUsages.CopyDestination),
            rgbaCopy with { Aspect = TextureAspects.Plane1 }));
    }

    [Fact]
    public void Warp_roundtrips_rows_through_explicit_buffer_texture_footprints()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(1_024, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(1_024, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopySource | TextureUsages.CopyDestination));
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapping.Bytes.Clear();
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 16; column++)
                mapping.Bytes[row * 256 + column] = checked((byte)(row * 16 + column));
            mapping.Flush(new BufferRange(0, 1_024));
        }

        using CommandContext releaseContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        using CommandContext copyContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        BufferTextureCopy uploadCopy = new(
            upload,
            0,
            256,
            4,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            4,
            4,
            1);

        RecordedCommands[] commands = new RecordedCommands[1];
        backend.Begin(releaseContext);
        backend.Barrier(releaseContext, new QueueRelease(
            texture,
            range,
            PipelineSync.None,
            ResourceAccess.NoAccess,
            TextureLayout.Undefined,
            QueueType.Copy));
        QueueCompletion releaseCompletion;
        using (RecordedCommands releaseRecorded = backend.End(releaseContext))
        {
            commands[0] = releaseRecorded;
            releaseCompletion = backend.Submit(
                graphicsQueue,
                new QueueSubmitDesc([], [], commands, [], []));
        }

        backend.Begin(copyContext);
        backend.Barrier(copyContext, new QueueAcquire(
            texture,
            range,
            QueueType.Graphics,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(copyContext, uploadCopy);
        backend.Barrier(copyContext, new TextureBarrier(
            texture,
            range,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource,
            TextureLayout.CopyDestination,
            TextureLayout.CopySource));
        backend.CopyTextureToBuffer(copyContext, uploadCopy with { Buffer = readback });
        using RecordedCommands recorded = backend.End(copyContext);
        commands[0] = recorded;
        QueueCompletion[] waits = [releaseCompletion];
        QueueCompletion completion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc(waits, [], commands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, 1_024));
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 16; column++)
        {
            Assert.Equal(
                checked((byte)(row * 16 + column)),
                result.Bytes[row * 256 + column]);
        }
    }

    [Fact]
    public void Copy_context_rejects_a_first_use_texture_layout_transition()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));

        backend.Begin(context);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            backend.Barrier(context, new TextureBarrier(
                texture,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination,
                TextureLayout.Undefined,
                TextureLayout.CopyDestination)));
        Assert.Contains("QueueRelease/QueueAcquire", failure.Message, StringComparison.Ordinal);
        backend.Discard(context);
    }

    [Fact]
    public void Warp_clear_texture_covers_every_selected_3D_slice()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture3D,
                4,
                4,
                4,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4_096, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange barrierRange = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange clearRange = new(0, 1, 0, 4, TextureAspects.Color);
        BufferTextureCopy copy = new(
            readback,
            0,
            256,
            4,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            4,
            4,
            4);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            barrierRange,
            PipelineSync.None,
            PipelineSync.RenderTarget,
            ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, clearRange, new Vector4(0.25f, 0.5f, 0.75f, 1));
        backend.Barrier(context, new TextureBarrier(
            texture,
            barrierRange,
            PipelineSync.RenderTarget,
            PipelineSync.Copy,
            ResourceAccess.RenderTarget,
            ResourceAccess.CopySource,
            TextureLayout.RenderTarget,
            TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, copy);
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, 4_096));
        byte[] expected = [64, 128, 191, 255];
        for (int slice = 0; slice < 4; slice++)
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            int offset = slice * 1_024 + row * 256 + column * 4;
            Assert.Equal(expected, result.Bytes.Slice(offset, 4).ToArray());
        }
    }
}
