using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpRhiCorrectnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warp_cpu_visible_fixed_state_barriers_are_no_ops_for_committed_and_placed_buffers(bool placed)
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice(debug: true);
        BufferDesc uploadDesc = new(
            256,
            BufferUsage.CopySource | BufferUsage.Constant | BufferUsage.ShaderRead |
            BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Indirect);
        BufferDesc readbackDesc = new(256, BufferUsage.CopyDestination);
        (BufferHandle upload, HeapHandle uploadHeap) = CreateCpuBuffer(device, uploadDesc, MemoryType.Upload, placed);
        (BufferHandle readback, HeapHandle readbackHeap) = CreateCpuBuffer(device, readbackDesc, MemoryType.Readback, placed);
        byte[] expected = Enumerable.Range(0, 256).Select(static value => unchecked((byte)(value * 29))).ToArray();
        device.WriteBuffer(upload, 0, expected);

        GpuCompletion completion;
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
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] actual = new byte[expected.Length];
        device.ReadBuffer(readback, 0, actual);
        Assert.Equal(expected, actual);

        DestroyCpuBuffer(device, readback, readbackHeap);
        DestroyCpuBuffer(device, upload, uploadHeap);
        device.CollectGarbage();
        AssertNoNativeErrors(device);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warp_cpu_visible_fixed_state_buffers_reject_illegal_transitions_while_recording(bool placed)
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
        (BufferHandle upload, HeapHandle uploadHeap) = CreateCpuBuffer(
            device,
            new BufferDesc(256, BufferUsage.CopySource),
            MemoryType.Upload,
            placed);
        (BufferHandle readback, HeapHandle readbackHeap) = CreateCpuBuffer(
            device,
            new BufferDesc(256, BufferUsage.CopyDestination),
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
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_three_dimensional_odd_height_copy_uses_aligned_slice_placements_and_round_trips()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice(debug: true);
        const int width = 3;
        const int height = 3;
        const int depth = 3;
        TextureDesc desc = new(
            width,
            height,
            Format.R8G8B8A8UNorm,
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            Depth: depth,
            Dimension: TextureDimension.Texture3D);
        TextureCopyRegion region = new(0, 0, TextureAspect.Color, width, height, depth);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(desc, region, requestedBufferOffset: 37);
        Assert.True(footprint.Layout.RowsPerImage >= height);
        Assert.Equal(0UL, ((ulong)footprint.Layout.BytesPerRow * footprint.Layout.RowsPerImage) & 511UL);

        TextureHandle texture = device.CreateTexture(desc);
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        byte[] expected = new byte[checked((int)footprint.RequiredBufferSize)];
        int rowBytes = width * 4;
        for (int slice = 0; slice < depth; slice++)
        for (int row = 0; row < height; row++)
        for (int column = 0; column < rowBytes; column++)
        {
            int index = checked(
                (int)footprint.Layout.Offset +
                slice * (int)footprint.Layout.RowsPerImage * (int)footprint.Layout.BytesPerRow +
                row * (int)footprint.Layout.BytesPerRow +
                column);
            expected[index] = unchecked((byte)(11 + slice * 47 + row * 13 + column * 3));
        }
        device.WriteBuffer(upload, 0, expected);

        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBufferToTexture(new BufferTextureCopy(upload, footprint.Layout, texture, region));
            commands.Barriers([
                ResourceBarrier.Transition(texture.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyTextureToBuffer(new TextureBufferCopy(texture, region, readback, footprint.Layout));
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] actual = new byte[expected.Length];
        device.ReadBuffer(readback, 0, actual);
        for (int slice = 0; slice < depth; slice++)
        for (int row = 0; row < height; row++)
        {
            int index = checked(
                (int)footprint.Layout.Offset +
                slice * (int)footprint.Layout.RowsPerImage * (int)footprint.Layout.BytesPerRow +
                row * (int)footprint.Layout.BytesPerRow);
            Assert.Equal(expected.AsSpan(index, rowBytes).ToArray(), actual.AsSpan(index, rowBytes).ToArray());
        }

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.DestroyTexture(texture);
        device.CollectGarbage();
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_texture_barrier_ranges_reject_negative_and_out_of_bounds_indices_without_clamping()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
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
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_non_array_view_dimensions_are_layer_zero_only_while_array_dimensions_select_any_layer()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice(debug: true);
        List<(TextureHandle Texture, TextureViewHandle View)> accepted = [];

        TextureHandle oneDimensional = device.CreateTexture(new TextureDesc(
            8,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 2,
            Dimension: TextureDimension.Texture1D));
        AssertLayerRule(device, oneDimensional, TextureViewDimension.Texture1D, TextureViewDimension.Texture1DArray, accepted);

        TextureHandle twoDimensional = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 2));
        AssertLayerRule(device, twoDimensional, TextureViewDimension.Texture2D, TextureViewDimension.Texture2DArray, accepted);

        TextureHandle multisampled = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            ArrayLayers: 2,
            SampleCount: 4));
        AssertLayerRule(device, multisampled, TextureViewDimension.Texture2DMS, TextureViewDimension.Texture2DMSArray, accepted);

        foreach ((TextureHandle texture, TextureViewHandle view) in accepted)
        {
            device.DestroyTextureView(view);
            device.DestroyTexture(texture);
        }
        device.CollectGarbage();
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Warp_resolve_requires_explicitly_two_dimensional_source_and_destination_resources()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
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
            TextureUsage.CopySource | TextureUsage.ColorAttachment,
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
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_barriers_and_buffer_copies_are_rejected_inside_a_rendering_scope()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
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
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_cpu_access_rejects_an_incomplete_submitted_last_use()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
        const ulong size = 8UL * 1024 * 1024;
        BufferHandle upload = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopyDestination), MemoryType.Readback);

        GpuCompletion completion = default;
        bool observedIncomplete = false;
        for (int attempt = 0; attempt < 4 && !observedIncomplete; attempt++)
        {
            using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy);
            for (int copy = 0; copy < 256; copy++) commands.CopyBuffer(upload, 0, readback, 0, size);
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
            observedIncomplete = device.GetCompletedValue(QueueType.Copy) < completion.Value;
        }
        Assert.True(observedIncomplete, "WARP completed every stress submission synchronously; no in-flight CPU-access window was observed.");

        Assert.Throws<InvalidOperationException>(() => device.WriteBuffer(upload, 0, [7]));
        Assert.Throws<InvalidOperationException>(() => device.ReadBuffer(readback, 0, new byte[1]));
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(30)));
        device.WriteBuffer(upload, 0, [7]);
        device.ReadBuffer(readback, 0, new byte[1]);

        device.DestroyBuffer(readback);
        device.DestroyBuffer(upload);
        device.CollectGarbage();
    }

    [Fact]
    public void Warp_direct_rhi_aliasing_barrier_hands_one_placed_allocation_to_the_next_buffer()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice(debug: true);
        const int size = 256;
        BufferDesc placedDesc = new(size, BufferUsage.CopySource | BufferUsage.CopyDestination);
        ResourceRequirements requirements = device.GetBufferRequirements(placedDesc, MemoryType.DeviceLocal);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            requirements.Size,
            MemoryType.DeviceLocal,
            ResourceHeapClass.Buffer));
        BufferHandle first = device.CreatePlacedBuffer(heap, 0, placedDesc);
        BufferHandle second = device.CreatePlacedBuffer(heap, 0, placedDesc);
        BufferHandle firstUpload = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle secondUpload = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle firstReadback = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopyDestination), MemoryType.Readback);
        BufferHandle secondReadback = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopyDestination), MemoryType.Readback);
        byte[] firstExpected = Enumerable.Range(0, size).Select(static value => unchecked((byte)(value * 7))).ToArray();
        byte[] secondExpected = Enumerable.Range(0, size).Select(static value => checked((byte)(255 - value))).ToArray();
        device.WriteBuffer(firstUpload, 0, firstExpected);
        device.WriteBuffer(secondUpload, 0, secondExpected);

        GpuCompletion completion;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            commands.Barriers([
                ResourceBarrier.Transition(first.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBuffer(firstUpload, 0, first, 0, size);
            commands.Barriers([
                ResourceBarrier.Transition(first.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyBuffer(first, 0, firstReadback, 0, size);
            commands.Barriers([ResourceBarrier.Aliasing(first.Resource, second.Resource)]);
            commands.Barriers([
                ResourceBarrier.Transition(second.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ]);
            commands.CopyBuffer(secondUpload, 0, second, 0, size);
            commands.Barriers([
                ResourceBarrier.Transition(second.Resource, ResourceState.CopyDestination, ResourceState.CopySource),
            ]);
            commands.CopyBuffer(second, 0, secondReadback, 0, size);
            completion = device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] firstActual = new byte[size];
        byte[] secondActual = new byte[size];
        device.ReadBuffer(firstReadback, 0, firstActual);
        device.ReadBuffer(secondReadback, 0, secondActual);
        Assert.Equal(firstExpected, firstActual);
        Assert.Equal(secondExpected, secondActual);

        device.DestroyBuffer(secondReadback);
        device.DestroyBuffer(firstReadback);
        device.DestroyBuffer(secondUpload);
        device.DestroyBuffer(firstUpload);
        device.DestroyBuffer(second);
        device.DestroyBuffer(first);
        device.DestroyHeap(heap);
        device.CollectGarbage();
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Persistent_cpu_descriptors_share_pages_and_reuse_retired_slots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice(debug: true);
        BufferHandle buffer = device.CreateBuffer(new BufferDesc(64, BufferUsage.ShaderRead));
        BufferViewHandle[] views = new BufferViewHandle[300];
        for (int index = 0; index < views.Length; index++)
        {
            views[index] = device.CreateBufferView(new BufferViewDesc(
                buffer,
                BufferRange.Whole,
                BindingKind.ReadOnlyBuffer,
                Stride: 4));
        }

        Assert.Equal(300, device.OutstandingCpuDescriptorCount);
        Assert.Equal(1, device.CpuDescriptorHeapCount);
        foreach (BufferViewHandle view in views) device.DestroyBufferView(view);
        Assert.Equal(0, device.OutstandingCpuDescriptorCount);

        BufferViewHandle reused = device.CreateBufferView(new BufferViewDesc(
            buffer,
            BufferRange.Whole,
            BindingKind.ReadOnlyBuffer,
            Stride: 4));
        Assert.Equal(1, device.OutstandingCpuDescriptorCount);
        Assert.Equal(1, device.CpuDescriptorHeapCount);
        device.DestroyBufferView(reused);
        device.DestroyBuffer(buffer);
        device.CollectGarbage();
        Assert.Equal(0, device.OutstandingCpuDescriptorCount);
        AssertNoNativeErrors(device);
    }

    [Fact]
    public void Allocation_requirement_queries_are_cached_independently_of_debug_names()
    {
        if (!OperatingSystem.IsWindows()) return;

        using Device device = CreateDevice();
        ResourceRequirements firstBuffer = device.GetBufferRequirements(
            new BufferDesc(4096, BufferUsage.CopyDestination, "first-buffer"));
        ResourceRequirements secondBuffer = device.GetBufferRequirements(
            new BufferDesc(4096, BufferUsage.CopyDestination, "second-buffer"));
        Assert.Equal(firstBuffer, secondBuffer);
        Assert.Equal(1, device.NativeBufferRequirementQueryCount);

        ResourceRequirements firstTexture = device.GetTextureRequirements(new TextureDesc(
            64,
            64,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            Name: "first-texture"));
        ResourceRequirements secondTexture = device.GetTextureRequirements(new TextureDesc(
            64,
            64,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            Name: "second-texture"));
        Assert.Equal(firstTexture, secondTexture);
        Assert.Equal(1, device.NativeTextureRequirementQueryCount);
    }

    private static Device CreateDevice(bool debug = false) => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = debug,
        EnableGpuValidation = false,
    });

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

    private static void AssertNoNativeErrors(Device device) => Assert.DoesNotContain(
        device.DrainDiagnostics(),
        static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
}
