using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpCopyTests
{
    [Fact]
    public void Copy_footprints_report_exact_uncompressed_and_block_rows()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void Copy_footprints_validate_permitted_view_formats_before_copy_regions()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        BufferTextureCopy copy = new(
            null!, 0, 0, 0, null!, 0, 0, TextureAspects.Color,
            0, 0, 0, 8, 4, 1);
        TextureCopyFootprint ordinary = backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.CopyDestination),
            copy);
        TextureCopyFootprint permitted = backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.CopyDestination,
                [Format.R8G8B8A8UNormSrgb]),
            copy);
        Assert.Equal(ordinary, permitted);

        BufferTextureCopy invalidMip = copy with { MipLevel = 1 };
        Assert.Throws<ArgumentException>("desc", () => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.CopyDestination,
                [Format.R32Float]),
            invalidMip));
        Assert.Throws<ArgumentException>("desc", () => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.CopyDestination,
                [Format.R8G8B8A8UNormSrgb, Format.R8G8B8A8UNormSrgb]),
            invalidMip));
        Assert.Throws<ArgumentOutOfRangeException>("mip", () => backend.GetTextureCopyFootprint(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.CopyDestination,
                [Format.R8G8B8A8UNormSrgb]),
            invalidMip));
    }

    [Fact]
    public void Color_attachments_preserve_sample_counts_when_recreated_in_interleaved_order()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        FormatSupport support =
            device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        Assert.True(
            (support.Features & FormatFeatures.MultisampleColorAttachment) != 0);
        Assert.True(support.SupportsSampleCount(4));
        TextureDesc singleSampleDescription = new(
            TextureDimension.Texture2D, 4, 4, 1, 1, 2, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment);
        TextureDesc multisampleDescription = new(
            TextureDimension.Texture2D, 4, 4, 1, 1, 2, 4,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment);

        using (Texture singleSample = backend.CreateTexture(
                   device, singleSampleDescription))
        using (Texture multisampled = backend.CreateTexture(
                   device, multisampleDescription))
        {
            Assert.Equal(1u, singleSample.Info.SampleCount);
            Assert.Equal(4u, multisampled.Info.SampleCount);
        }

        using Texture nextSingleSample = backend.CreateTexture(
            device, singleSampleDescription);
        using Texture nextMultisampled = backend.CreateTexture(
            device, multisampleDescription);
        Assert.Equal(1u, nextSingleSample.Info.SampleCount);
        Assert.Equal(4u, nextMultisampled.Info.SampleCount);
    }

    [Theory]
    [InlineData(Format.B8G8R8A8UNorm, Format.R8G8B8A8UNorm)]
    [InlineData(Format.R8G8B8A8UNorm, Format.B8G8R8A8UNorm)]
    public void Resolving_incompatible_format_families_is_rejected(
        Format destinationFormat,
        Format resolveFormat)
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        FormatSupport support =
            device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        const FormatFeatures required =
            FormatFeatures.MultisampleColorAttachment |
            FormatFeatures.MultisampleResolve;
        Assert.Equal(required, support.Features & required);
        Assert.True(support.SupportsSampleCount(4));

        using Texture source = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 4, 4, 1, 1, 2, 4,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Texture destination = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 8, 1, 2, 3, 1,
                destinationFormat, TextureUsages.CopyDestination));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        try
        {
            backend.Barrier(context, new TextureBarrier(
                source, new TextureSubresourceRange(0, 1, 1, 1, TextureAspects.Color),
                PipelineSync.None, PipelineSync.Resolve,
                ResourceAccess.NoAccess, ResourceAccess.ResolveSource,
                TextureLayout.Undefined, TextureLayout.ResolveSource));
            backend.Barrier(context, new TextureBarrier(
                destination, new TextureSubresourceRange(1, 1, 2, 1, TextureAspects.Color),
                PipelineSync.None, PipelineSync.Resolve,
                ResourceAccess.NoAccess, ResourceAccess.ResolveDestination,
                TextureLayout.Undefined, TextureLayout.ResolveDestination));
            Assert.Throws<ArgumentException>(() =>
                backend.ResolveTexture(context, new TextureResolve(
                    source, 0, 1, destination, 1, 2, resolveFormat)));
        }
        finally
        {
            backend.Discard(context);
        }
    }

    [Fact]
    public void Resolving_a_multisampled_array_layer_writes_the_selected_destination_mip_and_layer()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        FormatSupport support =
            device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        const FormatFeatures required =
            FormatFeatures.MultisampleColorAttachment |
            FormatFeatures.MultisampleResolve;
        Assert.Equal(required, support.Features & required);
        Assert.True(support.SupportsSampleCount(4));

        using Texture source = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 4, 4, 1, 1, 2, 4,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Texture destination = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 8, 8, 1, 2, 3, 1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(1_024, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange sourceRange =
            new(0, 1, 1, 1, TextureAspects.Color);
        TextureSubresourceRange destinationRange =
            new(1, 1, 2, 1, TextureAspects.Color);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            source, sourceRange,
            PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
            TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(
            context, source, sourceRange, new Vector4(1, 0, 0, 1));
        backend.Barrier(context, new TextureBarrier(
            source, sourceRange,
            PipelineSync.RenderTarget, PipelineSync.Resolve,
            ResourceAccess.RenderTarget, ResourceAccess.ResolveSource,
            TextureLayout.RenderTarget, TextureLayout.ResolveSource));
        backend.Barrier(context, new TextureBarrier(
            destination, destinationRange,
            PipelineSync.None, PipelineSync.Resolve,
            ResourceAccess.NoAccess, ResourceAccess.ResolveDestination,
            TextureLayout.Undefined, TextureLayout.ResolveDestination));
        backend.ResolveTexture(context, new TextureResolve(
            source, 0, 1, destination, 1, 2, Format.R8G8B8A8UNorm));
        backend.Barrier(context, new TextureBarrier(
            destination, destinationRange,
            PipelineSync.Resolve, PipelineSync.Copy,
            ResourceAccess.ResolveDestination, ResourceAccess.CopySource,
            TextureLayout.ResolveDestination, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, 256, 4,
            destination, 1, 2, TextureAspects.Color,
            0, 0, 0, 4, 4, 1));

        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueCompletion completion = backend.Submit(
            queue, new QueueSubmitDesc([], [], commands, [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result =
            backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, 1_024));
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            Assert.Equal(
                0xff0000ffu,
                BitConverter.ToUInt32(
                    result.Bytes.Slice(row * 256 + column * 4, 4)));
        }
    }

    [Fact]
    public void Warp_roundtrips_rows_through_explicit_buffer_texture_footprints()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
    public void Warp_copies_a_narrow_texture_region_with_its_own_row_pitch()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(512, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(768, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                128,
                1,
                1,
                1,
                1,
                1,
                Format.R32UInt,
                TextureUsages.CopySource | TextureUsages.CopyDestination));
        using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            for (int index = 0; index < 128; index++)
                BitConverter.GetBytes(index).CopyTo(mapping.Bytes[(index * sizeof(uint))..]);
            mapping.Flush(new BufferRange(0, 512));
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        BufferTextureCopy uploadCopy = new(
            upload,
            0,
            512,
            1,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            128,
            1,
            1);
        BufferTextureCopy readbackCopy = new(
            readback,
            512,
            256,
            1,
            texture,
            0,
            0,
            TextureAspects.Color,
            32,
            0,
            0,
            64,
            1,
            1);

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
        backend.CopyBufferToTexture(context, uploadCopy);
        backend.Barrier(context, new TextureBarrier(
            texture,
            range,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceAccess.CopySource,
            TextureLayout.CopyDestination,
            TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, readbackCopy);
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(512, 256));
        for (int index = 0; index < 64; index++)
        {
            Assert.Equal(
                checked((uint)(index + 32)),
                BitConverter.ToUInt32(result.Bytes.Slice(512 + index * sizeof(uint), sizeof(uint))));
        }
    }

    [Fact]
    public void Copy_context_rejects_a_first_use_texture_layout_transition()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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

        backend.Discard(context);
    }

    [Theory]
    [InlineData(Format.R8G8B8A8UNorm)]
    [InlineData(Format.D32Float)]
    public void Clearing_an_array_subrange_preserves_other_mips_and_layers(Format format)
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        bool depth = format == Format.D32Float;
        TextureAspects aspect = depth ? TextureAspects.Depth : TextureAspects.Color;
        TextureUsages usage = depth
            ? TextureUsages.DepthStencilAttachment
            : TextureUsages.ColorAttachment;
        PipelineSync clearSync = depth ? PipelineSync.DepthStencil : PipelineSync.RenderTarget;
        ResourceAccess clearAccess = depth
            ? ResourceAccess.DepthStencilWrite
            : ResourceAccess.RenderTarget;
        TextureLayout clearLayout = depth
            ? TextureLayout.DepthStencilWrite
            : TextureLayout.RenderTarget;
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D, 4, 4, 1, 3, 4, 1,
                format, usage | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(12_288, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        TextureSubresourceRange whole = new(0, 3, 0, 4, aspect);
        TextureSubresourceRange selected = new(1, 2, 1, 2, aspect);

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture, whole,
            PipelineSync.None, clearSync,
            ResourceAccess.NoAccess, clearAccess,
            TextureLayout.Undefined, clearLayout));
        if (depth)
            backend.ClearDepthStencil(context, texture, whole, 0.25f);
        else
            backend.ClearTexture(context, texture, whole, new Vector4(0, 0, 1, 1));
        backend.Barrier(context, new TextureBarrier(
            texture, selected,
            clearSync, clearSync,
            clearAccess, clearAccess,
            clearLayout, clearLayout));
        if (depth)
            backend.ClearDepthStencil(context, texture, selected, 0.75f);
        else
            backend.ClearTexture(context, texture, selected, new Vector4(1, 0, 0, 1));
        backend.Barrier(context, new TextureBarrier(
            texture, whole,
            clearSync, PipelineSync.Copy,
            clearAccess, ResourceAccess.CopySource,
            clearLayout, TextureLayout.CopySource));
        for (uint layer = 0; layer < 4; layer++)
        for (uint mip = 0; mip < 3; mip++)
        {
            uint size = 4u >> (int)mip;
            backend.CopyTextureToBuffer(context, new BufferTextureCopy(
                readback, (layer * 3 + mip) * 1_024UL, 256, size,
                texture, mip, layer, aspect,
                0, 0, 0, size, size, 1));
        }
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        QueueCompletion completion = backend.Submit(
            queue, new QueueSubmitDesc([], [], commands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, 12_288));
        for (int layer = 0; layer < 4; layer++)
        for (int mip = 0; mip < 3; mip++)
        {
            bool changed = mip >= 1 && layer >= 1 && layer <= 2;
            uint expected = depth
                ? BitConverter.SingleToUInt32Bits(changed ? 0.75f : 0.25f)
                : changed ? 0xff0000ffu : 0xffff0000u;
            int size = 4 >> mip;
            for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                int offset = (layer * 3 + mip) * 1_024 + row * 256 + column * 4;
                Assert.Equal(expected, BitConverter.ToUInt32(result.Bytes.Slice(offset, 4)));
            }
        }
    }

    [Fact]
    public void Warp_clear_texture_covers_every_selected_3D_slice()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
