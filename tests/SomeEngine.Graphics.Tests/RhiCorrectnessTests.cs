using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class RhiCorrectnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cpu_visible_fixed_state_barriers_are_no_ops_for_committed_and_placed_buffers(bool placed)
    {
        using Device device = new();
        BufferDesc uploadDesc = new(
            64,
            BufferUsage.CopySource | BufferUsage.Constant | BufferUsage.ShaderRead |
            BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Indirect);
        BufferDesc readbackDesc = new(64, BufferUsage.CopyDestination);
        (BufferHandle upload, HeapHandle uploadHeap) = CreateCpuBuffer(device, uploadDesc, MemoryType.Upload, placed);
        (BufferHandle readback, HeapHandle readbackHeap) = CreateCpuBuffer(device, readbackDesc, MemoryType.Readback, placed);
        byte[] expected = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(value * 17))).ToArray();
        device.WriteBuffer(upload, 0, expected);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.Barriers([
                ResourceBarrier.Transition(upload.Resource, ResourceState.CopySource, ResourceState.ShaderResource),
                ResourceBarrier.Transition(upload.Resource, ResourceState.ShaderResource, ResourceState.VertexOrConstantBuffer),
                ResourceBarrier.Transition(upload.Resource, ResourceState.VertexOrConstantBuffer, ResourceState.IndexBuffer),
                ResourceBarrier.Transition(upload.Resource, ResourceState.IndexBuffer, ResourceState.IndirectArgument),
                ResourceBarrier.Transition(upload.Resource, ResourceState.IndirectArgument, ResourceState.CopySource),
                ResourceBarrier.Transition(readback.Resource, ResourceState.CopyDestination, ResourceState.CopyDestination),
            ]);
            commands.CopyBuffer(upload, 0, readback, 0, (ulong)expected.Length);
            GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.Zero));
        }

        byte[] actual = new byte[expected.Length];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(expected, actual);

        DestroyCpuBuffer(device, readback, readbackHeap);
        DestroyCpuBuffer(device, upload, uploadHeap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cpu_visible_fixed_state_buffers_reject_illegal_transitions_while_recording(bool placed)
    {
        using Device device = new();
        (BufferHandle upload, HeapHandle uploadHeap) = CreateCpuBuffer(
            device,
            new BufferDesc(64, BufferUsage.CopySource),
            MemoryType.Upload,
            placed);
        (BufferHandle readback, HeapHandle readbackHeap) = CreateCpuBuffer(
            device,
            new BufferDesc(64, BufferUsage.CopyDestination),
            MemoryType.Readback,
            placed);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            Assert.Throws<InvalidOperationException>(() => commands.Barriers([
                ResourceBarrier.Transition(upload.Resource, ResourceState.CopySource, ResourceState.CopyDestination),
            ]));
            Assert.Throws<InvalidOperationException>(() => commands.Barriers([
                ResourceBarrier.Transition(readback.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]));
        }

        DestroyCpuBuffer(device, readback, readbackHeap);
        DestroyCpuBuffer(device, upload, uploadHeap);
    }

    [Fact]
    public void Cpu_access_waits_for_the_exact_submitted_last_use()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferHandle upload = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);
        device.WriteBuffer(upload, 0, Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray());

        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.CopyBuffer(upload, 0, readback, 0, 16);
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }

        Assert.Throws<InvalidOperationException>(() => device.WriteBuffer(upload, 0, [99]));
        Assert.Throws<InvalidOperationException>(() => device.ReadBuffer(readback, 0, new byte[1]));
        device.AdvanceCompletion(completion);
        device.WriteBuffer(upload, 0, [99]);
        device.ReadBuffer(readback, 0, new byte[1]);

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
    }

    [Fact]
    public void Texture_barrier_ranges_reject_negative_and_out_of_bounds_indices_without_clamping()
    {
        using Device device = new();
        TextureHandle texture = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopyDestination,
            MipLevels: 2,
            ArrayLayers: 2));
        TextureSubresourceRange[] invalid =
        [
            new(-1, 1, 0, 1, TextureAspect.Color),
            new(2, 1, 0, 1, TextureAspect.Color),
            new(0, 3, 0, 1, TextureAspect.Color),
            new(0, 1, -1, 1, TextureAspect.Color),
            new(0, 1, 2, 1, TextureAspect.Color),
            new(0, 1, 0, 3, TextureAspect.Color),
        ];

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            foreach (TextureSubresourceRange range in invalid)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => commands.Barriers([
                    ResourceBarrier.Transition(
                        texture.Resource,
                        ResourceState.Common,
                        ResourceState.CopyDestination,
                        range),
                ]));
            }
        }

        device.DestroyTexture(texture);
    }

    [Fact]
    public void Non_array_view_dimensions_are_layer_zero_only_while_array_dimensions_select_any_layer()
    {
        using Device device = new();
        List<(TextureHandle Texture, TextureViewHandle View)> accepted = [];

        TextureHandle oneDimensional = device.CreateTexture(new TextureDesc(
            8,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 2,
            Dimension: TextureDimension.Texture1D));
        AssertLayerRule(
            device,
            oneDimensional,
            TextureViewDimension.Texture1D,
            TextureViewDimension.Texture1DArray,
            accepted);

        TextureHandle twoDimensional = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 2));
        AssertLayerRule(
            device,
            twoDimensional,
            TextureViewDimension.Texture2D,
            TextureViewDimension.Texture2DArray,
            accepted);

        TextureHandle multisampled = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            ArrayLayers: 2,
            SampleCount: 4));
        AssertLayerRule(
            device,
            multisampled,
            TextureViewDimension.Texture2DMS,
            TextureViewDimension.Texture2DMSArray,
            accepted);

        foreach ((TextureHandle texture, TextureViewHandle view) in accepted)
        {
            device.DestroyTextureView(view);
            device.DestroyTexture(texture);
        }
    }

    [Fact]
    public void Resolve_requires_explicitly_two_dimensional_source_and_destination_resources()
    {
        using Device device = new();
        TextureHandle oneDimensionalSource = device.CreateTexture(new TextureDesc(
            4,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource,
            Dimension: TextureDimension.Texture1D));
        TextureHandle twoDimensionalDestination = device.CreateTexture(new TextureDesc(
            4,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopyDestination));
        TextureHandle multisampledSource = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource,
            SampleCount: 4));
        TextureHandle threeDimensionalDestination = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopyDestination,
            Depth: 1,
            Dimension: TextureDimension.Texture3D));

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<NotSupportedException>(() => commands.ResolveTexture(
                new TextureResolveRegion(oneDimensionalSource, twoDimensionalDestination)));
            Assert.Throws<NotSupportedException>(() => commands.ResolveTexture(
                new TextureResolveRegion(multisampledSource, threeDimensionalDestination)));
        }

        device.DestroyTexture(threeDimensionalDestination);
        device.DestroyTexture(multisampledSource);
        device.DestroyTexture(twoDimensionalDestination);
        device.DestroyTexture(oneDimensionalSource);
    }

    [Fact]
    public void Barriers_and_buffer_copies_are_rejected_inside_a_rendering_scope()
    {
        using Device device = new();
        TextureHandle color = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment));
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            color,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment,
            Dimension: TextureViewDimension.Texture2D));
        BufferHandle source = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics))
        {
            commands.BeginRendering(new RenderingInfo(
                new ColorAttachment[] { new(view, LoadAction.Load, StoreAction.Store) },
                null,
                4,
                4));
            Assert.Throws<InvalidOperationException>(() => commands.Barriers([
                ResourceBarrier.Transition(color.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ]));
            Assert.Throws<InvalidOperationException>(() => commands.CopyBuffer(source, 0, destination, 0, 4));
            commands.EndRendering();
        }

        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
        device.DestroyTextureView(view);
        device.DestroyTexture(color);
    }

    private static void AssertLayerRule(
        Device device,
        TextureHandle texture,
        TextureViewDimension nonArray,
        TextureViewDimension array,
        List<(TextureHandle Texture, TextureViewHandle View)> accepted)
    {
        TextureSubresourceRange secondLayer = new(0, 1, 1, 1, TextureAspect.Color);
        Assert.Throws<ArgumentException>(() => device.CreateTextureView(new TextureViewDesc(
            texture,
            secondLayer,
            TextureViewUsage.ShaderResource,
            Dimension: nonArray)));
        accepted.Add((texture, device.CreateTextureView(new TextureViewDesc(
            texture,
            secondLayer,
            TextureViewUsage.ShaderResource,
            Dimension: array))));
    }

    private static (BufferHandle Buffer, HeapHandle Heap) CreateCpuBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType,
        bool placed)
    {
        if (!placed) return (device.CreateBuffer(desc, memoryType), default);
        ResourceRequirements requirements = device.GetBufferRequirements(desc, memoryType);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            requirements.Size,
            memoryType,
            ResourceHeapClass.Buffer));
        return (device.CreatePlacedBuffer(heap, 0, desc), heap);
    }

    private static void DestroyCpuBuffer(Device device, BufferHandle buffer, HeapHandle heap)
    {
        device.DestroyBuffer(buffer);
        if (heap.IsValid) device.DestroyHeap(heap);
    }
}
