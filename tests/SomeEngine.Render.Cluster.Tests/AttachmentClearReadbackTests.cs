using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class AttachmentClearReadbackTests
{
    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public void ColorClearChangesOnlyTheSelectedMipLevelsAndLayers(string backendName)
    {
        using var support = new AttachmentClearDevice(backendName);
        IGraphicsBackend backend = support.Backend;
        using Texture texture = backend.CreateTexture(support.Device, new TextureDesc(
            TextureDimension.Texture2D, 4, 4, 1, 3, 3, 1, Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(support.Device,
            new BufferDesc(9 * 1024, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(support.Device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange whole = new(0, 3, 0, 3, TextureAspects.Color);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, whole, Vector4.Zero);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.RenderTarget,
            ResourceAccess.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, new TextureSubresourceRange(1, 2, 1, 2, TextureAspects.Color),
            new Vector4(1, 0, 0, 1));
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        for (uint layer = 0; layer < 3; layer++)
        for (uint mip = 0; mip < 3; mip++)
        {
            uint size = 4u >> (int)mip;
            backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, (layer * 3 + mip) * 1024u,
                256, size, texture, mip, layer, TextureAspects.Color, 0, 0, 0, size, size, 1));
        }
        support.SubmitAndWait(context);
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        for (int layer = 0; layer < 3; layer++)
        for (int mip = 0; mip < 3; mip++)
        {
            int size = 4 >> mip;
            byte[] actual = new byte[size * size * 4];
            byte[] expected = new byte[actual.Length];
            for (int y = 0; y < size; y++)
                mapped.Bytes.Slice((layer * 3 + mip) * 1024 + y * 256, size * 4).CopyTo(actual.AsSpan(y * size * 4));
            if (layer > 0 && mip > 0)
                for (int pixel = 0; pixel < size * size; pixel++)
                    expected[pixel * 4] = expected[pixel * 4 + 3] = 255;
            ImageArtifacts.AssertEqual(new TestImage(size, size, Format.R8G8B8A8UNorm, expected),
                new TestImage(size, size, Format.R8G8B8A8UNorm, actual),
                $"{nameof(ColorClearChangesOnlyTheSelectedMipLevelsAndLayers)}-{backendName}-{layer}-{mip}");
        }
    }

    [Theory]
    [InlineData("D3D12", Format.R32UInt, 37)]
    [InlineData("Vulkan", Format.R32UInt, 37)]
    [InlineData("D3D12", Format.R32SInt, -7)]
    [InlineData("Vulkan", Format.R32SInt, -7)]
    public void IntegerClearValuesSurviveAFollowingRenderingScope(string backendName, Format format, int value)
    {
        using var support = new AttachmentClearDevice(backendName);
        IGraphicsBackend backend = support.Backend;
        using Texture texture = backend.CreateTexture(support.Device, new TextureDesc(
            TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, format,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange whole = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(support.Device,
            new ColorAttachmentViewDesc(texture, whole, format, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(support.Device,
            new BufferDesc(256, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(support.Device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, whole, new Vector4(value, 0, 0, 0));
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.RenderTarget,
            ResourceAccess.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, TextureLayout.RenderTarget));
        backend.BeginRendering(context, new RenderingDesc(
            [new ColorAttachmentDesc(view, LoadType.Load, StoreType.Store, default)], null, 2, 1));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 256, 1, texture,
            0, 0, TextureAspects.Color, 0, 0, 0, 2, 1, 1));
        support.SubmitAndWait(context);
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(value, MemoryMarshal.Read<int>(mapped.Bytes));
        Assert.Equal(value, MemoryMarshal.Read<int>(mapped.Bytes[4..]));
    }

    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public void DepthClearPreservesStencil(string backendName)
    {
        using var support = new AttachmentClearDevice(backendName);
        IGraphicsBackend backend = support.Backend;
        TextureDesc description = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.D32FloatS8UInt,
            TextureUsages.DepthStencilAttachment | TextureUsages.CopySource);
        using Texture texture = backend.CreateTexture(support.Device, description);
        using Buffer readback = backend.CreateBuffer(support.Device,
            new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(support.Device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange whole = new(0, 1, 0, 1, TextureAspects.Depth | TextureAspects.Stencil);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.DepthStencil,
            ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
        backend.ClearDepthStencil(context, texture, whole, .25f, 19);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.DepthStencil, PipelineSync.DepthStencil,
            ResourceAccess.DepthStencilWrite, ResourceAccess.DepthStencilWrite, TextureLayout.DepthStencilWrite, TextureLayout.DepthStencilWrite));
        backend.ClearDepthStencil(context, texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), .75f);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.DepthStencil, PipelineSync.Copy,
            ResourceAccess.DepthStencilWrite, ResourceAccess.CopySource, TextureLayout.DepthStencilWrite, TextureLayout.CopySource));
        BufferTextureCopy depthCopy = new(readback, 0, 0, 0, texture, 0, 0, TextureAspects.Depth, 0, 0, 0, 1, 1, 1);
        BufferTextureCopy stencilCopy = depthCopy with { BufferOffset = 512, Aspect = TextureAspects.Stencil };
        TextureCopyFootprint depthFootprint = backend.GetTextureCopyFootprint(support.Device, description, depthCopy);
        TextureCopyFootprint stencilFootprint = backend.GetTextureCopyFootprint(support.Device, description, stencilCopy, 512);
        backend.CopyTextureToBuffer(context, depthCopy with { BufferRowPitch = depthFootprint.RowPitch });
        backend.CopyTextureToBuffer(context, stencilCopy with { BufferRowPitch = stencilFootprint.RowPitch });
        support.SubmitAndWait(context);
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(.75f, MemoryMarshal.Read<float>(mapped.Bytes));
        Assert.Equal(19, mapped.Bytes[512]);
    }

    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public void StencilClearPreservesDepth(string backendName)
    {
        using var support = new AttachmentClearDevice(backendName);
        IGraphicsBackend backend = support.Backend;
        TextureDesc description = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.D32FloatS8UInt,
            TextureUsages.DepthStencilAttachment | TextureUsages.CopySource);
        using Texture texture = backend.CreateTexture(support.Device, description);
        using Buffer readback = backend.CreateBuffer(support.Device,
            new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(support.Device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange whole = new(0, 1, 0, 1, TextureAspects.Depth | TextureAspects.Stencil);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.DepthStencil,
            ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
        backend.ClearDepthStencil(context, texture, whole, .25f, 19);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.DepthStencil, PipelineSync.DepthStencil,
            ResourceAccess.DepthStencilWrite, ResourceAccess.DepthStencilWrite, TextureLayout.DepthStencilWrite, TextureLayout.DepthStencilWrite));
        backend.ClearDepthStencil(context, texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Stencil), stencil: 31);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.DepthStencil, PipelineSync.Copy,
            ResourceAccess.DepthStencilWrite, ResourceAccess.CopySource, TextureLayout.DepthStencilWrite, TextureLayout.CopySource));
        BufferTextureCopy depthCopy = new(readback, 0, 0, 0, texture, 0, 0, TextureAspects.Depth, 0, 0, 0, 1, 1, 1);
        BufferTextureCopy stencilCopy = depthCopy with { BufferOffset = 512, Aspect = TextureAspects.Stencil };
        TextureCopyFootprint depthFootprint = backend.GetTextureCopyFootprint(support.Device, description, depthCopy);
        TextureCopyFootprint stencilFootprint = backend.GetTextureCopyFootprint(support.Device, description, stencilCopy, 512);
        backend.CopyTextureToBuffer(context, depthCopy with { BufferRowPitch = depthFootprint.RowPitch });
        backend.CopyTextureToBuffer(context, stencilCopy with { BufferRowPitch = stencilFootprint.RowPitch });
        support.SubmitAndWait(context);
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(.25f, MemoryMarshal.Read<float>(mapped.Bytes));
        Assert.Equal(31, mapped.Bytes[512]);
    }

    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public void VolumeColorClearPreservesUnselectedDepthSlices(string backendName)
    {
        using var support = new AttachmentClearDevice(backendName);
        IGraphicsBackend backend = support.Backend;
        using Texture texture = backend.CreateTexture(support.Device, new TextureDesc(
            TextureDimension.Texture3D, 2, 2, 3, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Buffer readback = backend.CreateBuffer(support.Device,
            new BufferDesc(1536, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(support.Device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        TextureSubresourceRange whole = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, new TextureSubresourceRange(0, 1, 0, 3, TextureAspects.Color), Vector4.Zero);
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.RenderTarget,
            ResourceAccess.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, TextureLayout.RenderTarget));
        backend.ClearTexture(context, texture, new TextureSubresourceRange(0, 1, 1, 1, TextureAspects.Color), new Vector4(1, 0, 0, 1));
        backend.Barrier(context, new TextureBarrier(texture, whole, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 256, 2, texture,
            0, 0, TextureAspects.Color, 0, 0, 0, 2, 2, 3));
        support.SubmitAndWait(context);
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        for (int slice = 0; slice < 3; slice++)
        {
            byte[] actual = new byte[16];
            byte[] expected = new byte[16];
            for (int y = 0; y < 2; y++)
                mapped.Bytes.Slice(slice * 512 + y * 256, 8).CopyTo(actual.AsSpan(y * 8));
            if (slice == 1)
                for (int pixel = 0; pixel < 4; pixel++)
                    expected[pixel * 4] = expected[pixel * 4 + 3] = 255;
            ImageArtifacts.AssertEqual(new TestImage(2, 2, Format.R8G8B8A8UNorm, expected),
                new TestImage(2, 2, Format.R8G8B8A8UNorm, actual),
                $"{nameof(VolumeColorClearPreservesUnselectedDepthSlices)}-{backendName}-{slice}");
        }
    }

    private sealed class AttachmentClearDevice : IDisposable
    {
        internal IGraphicsBackend Backend { get; }
        internal Device Device { get; }

        internal AttachmentClearDevice(string backendName)
        {
            bool nativeValidation = backendName == "Vulkan" &&
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LAYER_PATH"));
            Backend = new ValidationLayer(backendName == "Vulkan"
                ? VulkanGraphicsBackend.Create(new VulkanBackendOptions(EnableValidation: nativeValidation))
                : D3D12GraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: backendName == "Vulkan" && !nativeValidation));
            var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
            Backend.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            Backend.TryEnumerateAdapters(options, adapters, out _);
            AdapterInfo adapter = adapters.First(value => value.HardwareAccelerated == (backendName == "Vulkan"));
            Device = Backend.CreateDevice(new DeviceDesc(adapter.Id, [new(QueueType.Graphics)]));
        }

        internal void SubmitAndWait(CommandContext context)
        {
            using RecordedCommands commands = Backend.End(context);
            QueueCompletion completion = Backend.Submit(Backend.GetQueue(Device, QueueType.Graphics),
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }

        public void Dispose()
        {
            Device.Dispose();
            Backend.Dispose();
        }
    }
}
