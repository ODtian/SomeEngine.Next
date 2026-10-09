using System.Buffers.Binary;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class VirtualShadowHiZTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void Two_dispatches_match_a_cpu_max_pyramid_for_reordered_physical_pages()
    {
        D3D12TestShader initialize = shaderCompiler.CompileWithSearchPaths(
            "shadow_hiz_initialize", "#include \"vsm_page_hiz.slang\"",
            [new("InitializeVirtualShadowPageHiZ", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        D3D12TestShader reduce = shaderCompiler.CompileWithSearchPaths(
            "shadow_hiz_reduce", "#include \"vsm_page_hiz_reduce.slang\"",
            [new("ReduceVirtualShadowPageHiZ", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline first = backend.CreateComputePipeline(device, new ComputePipelineDesc(initialize.SlangCode, initialize.Reflection, initialize.GetEntryPoint(0), initialize.GetEntryCode(0)));
        using Pipeline second = backend.CreateComputePipeline(device, new ComputePipelineDesc(reduce.SlangCode, reduce.Reflection, reduce.GetEntryPoint(0), reduce.GetEntryCode(0)));
        using Buffer uniforms0 = Upload(backend, device, [128u, 2u, 0u, 0u, 0u, 0u, 0u, 0u], BufferUsages.Constant, 256);
        using Buffer uniforms1 = Upload(backend, device, [128u, 2u, 3u, 4u, 0u, 0u, 0u, 0u], BufferUsages.Constant, 256);
        using BufferCbv cbv0 = backend.CreateBufferCbv(device, new BufferCbvDesc(uniforms0, new BufferRange(0, 256)));
        using BufferCbv cbv1 = backend.CreateBufferCbv(device, new BufferCbvDesc(uniforms1, new BufferRange(0, 256)));
        using Buffer active = Upload(backend, device, [1u, 101u, 0u, 999u, 0u, 202u, 0u, 999u], BufferUsages.ShaderRead, 32);
        using BufferSrv activeView = backend.CreateBufferSrv(device, new BufferSrvDesc(active, BufferRange.Whole, StructureStride: 16));
        using Buffer activeCount = Upload(backend, device, [2u], BufferUsages.ShaderRead, 4);
        using BufferSrv activeCountView = backend.CreateBufferSrv(device, new BufferSrvDesc(activeCount, BufferRange.Whole, StructureStride: 4));
        using Buffer metadata = Upload(backend, device, [0u, 0u, 0u, 8u, 0u, 0u, 999u, 0u,
            0u, 0u, 0u, 8u, 0u, 0u, 999u, 0u], BufferUsages.ShaderRead, 64);
        using BufferSrv metadataView = backend.CreateBufferSrv(device, new BufferSrvDesc(metadata, BufferRange.Whole, StructureStride: 32));
        var depth = new float[2][];
        var pixels = new uint[2 * 256 * 128];
        for (int layer = 0; layer < 2; layer++)
        {
            depth[layer] = new float[128 * 128];
            for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float value = ((x * 3 + y * 5 + layer * 257) % 1024) / 1024f;
                if (x == 117 && y == 97) value = 1f;
                depth[layer][y * 128 + x] = value;
                pixels[y * 256 + layer * 128 + x] = BitConverter.SingleToUInt32Bits(1f - value);
            }
        }
        Array.Copy(pixels, 0, pixels, 256 * 128, 256 * 128);
        using Buffer upload = Upload(backend, device, pixels, BufferUsages.CopySource, pixels.Length * sizeof(uint));
        using Texture atlas = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            256, 128, 1, 1, 2, 1, Format.R32UInt, TextureUsages.Sampled | TextureUsages.CopyDestination));
        using TextureSrv atlasView = backend.CreateTextureSrv(device, new TextureSrvDesc(atlas,
            new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2DArray));
        using Texture hiz = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            128, 128, 1, 7, 1, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource));
        using TextureSrv source = backend.CreateTextureSrv(device, new TextureSrvDesc(hiz,
            new TextureSubresourceRange(3, 1, 0, 1, TextureAspects.Color), Format.R32Float, TextureViewDimension.Texture2DArray));
        TextureUav[] destinations = new TextureUav[7];
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(2u * 128u * 512u, BufferUsages.CopyDestination), MemoryType.Readback);
        try
        {
            for (uint mip = 0; mip < 7; mip++)
                destinations[mip] = backend.CreateTextureUav(device, new TextureUavDesc(hiz,
                    new TextureSubresourceRange(mip, 1, 0, 1, TextureAspects.Color), Format.R32Float, TextureViewDimension.Texture2DArray));
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
            backend.Barrier(context, new TextureBarrier(atlas, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color),
                PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination,
                TextureLayout.Undefined, TextureLayout.CopyDestination));
            for (uint layer = 0; layer < 2; layer++)
                backend.CopyBufferToTexture(context, new BufferTextureCopy(upload, layer * 1024u * 128u, 1024, 128, atlas,
                    0, layer, TextureAspects.Color, 0, 0, 0, 256, 128, 1));
            backend.Barrier(context, new TextureBarrier(atlas, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color),
                PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(0, 7, 0, 1, TextureAspects.Color),
                PipelineSync.None, PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
                TextureLayout.Undefined, TextureLayout.UnorderedAccess));
            backend.SetPipeline(context, first);
            backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                initialize.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                [ResourceBinding.ConstantBuffer(cbv0), ResourceBinding.ReadOnlyBuffer(activeView), ResourceBinding.ReadOnlyBuffer(activeCountView), ResourceBinding.ReadOnlyBuffer(metadataView),
                    ResourceBinding.SampledTexture(atlasView),
                    ResourceBinding.StorageTexture(destinations[0]), ResourceBinding.StorageTexture(destinations[1]),
                    ResourceBinding.StorageTexture(destinations[2]), ResourceBinding.StorageTexture(destinations[3])], []));
            backend.Dispatch(context, new DispatchArguments(2, 8, 8));
            backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(3, 1, 0, 1, TextureAspects.Color),
                PipelineSync.ComputeShading, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.ShaderResource,
                TextureLayout.UnorderedAccess, TextureLayout.ShaderResource));
            backend.SetPipeline(context, second);
            backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                reduce.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                [ResourceBinding.ConstantBuffer(cbv1), ResourceBinding.ReadOnlyBuffer(activeView), ResourceBinding.ReadOnlyBuffer(activeCountView), ResourceBinding.ReadOnlyBuffer(metadataView),
                    ResourceBinding.SampledTexture(source),
                    ResourceBinding.StorageTexture(destinations[4]), ResourceBinding.StorageTexture(destinations[5]),
                    ResourceBinding.StorageTexture(destinations[6]), ResourceBinding.StorageTexture(destinations[6])], []));
            backend.Dispatch(context, new DispatchArguments(2, 1, 1));
            for (uint mip = 0; mip < 7; mip++)
            {
                backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(mip, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.ComputeShading, PipelineSync.Copy,
                    mip == 3 ? ResourceAccess.ShaderResource : ResourceAccess.UnorderedAccess, ResourceAccess.CopySource,
                    mip == 3 ? TextureLayout.ShaderResource : TextureLayout.UnorderedAccess, TextureLayout.CopySource));
            }
            ulong offset = 0;
            for (uint page = 0; page < 2; page++)
            for (uint mip = 0; mip < 7; mip++)
            {
                uint size = 64u >> checked((int)mip);
                uint pitch = (size * sizeof(uint) + 255u) & ~255u;
                backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, offset, pitch, size, hiz,
                    mip, 0, TextureAspects.Color, page * size, 0, 0, size, size, 1));
                offset += Math.Max(size, 2u) * pitch;
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(new BufferRange(0, offset));
            int readOffset = 0;
            for (int layer = 0; layer < 2; layer++)
            {
                float[] previous = depth[layer];
                for (int size = 64; size != 0; size >>= 1)
                {
                    int pitch = (size * sizeof(uint) + 255) & ~255;
                    var expected = new float[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int p = (y * 2) * (size * 2) + x * 2;
                        expected[y * size + x] = Math.Max(Math.Max(previous[p], previous[p + 1]),
                            Math.Max(previous[p + size * 2], previous[p + size * 2 + 1]));
                        float actual = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(
                            mapping.Bytes.Slice(readOffset + y * pitch + x * 4, 4)));
                        Assert.Equal(expected[y * size + x], actual);
                    }
                    readOffset += Math.Max(size, 2) * pitch;
                    previous = expected;
                }
            }
        }
        finally
        {
            foreach (TextureUav? view in destinations) view?.Dispose();
        }
    }

    [Fact]
    public void Dynamic_only_pages_keep_each_existing_static_mip()
    {
        const uint pageSize = 16;
        uint[] atlas = new uint[32 * 16 * 2];
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 32; x++)
            atlas[32 * 16 + y * 32 + x] = BitConverter.SingleToUInt32Bits(x < 16 ? .25f : .75f);
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteImages(
            "vsm_page_hiz.slang", "InitializeVirtualShadowPageHiZ", new(2, 1, 1),
            HiZInputs(pageSize, 2, 0, 0, 0, [4u, 8u]),
            [new(32, 16, atlas, 4, ViewDimension: TextureViewDimension.Texture2DArray,
                Layers: 2, Name: "ShadowPhysicalAtlas"),
             HiZOutput(8, 2, 1, .5f, 0), HiZOutput(4, 2, 1, .5625f, 1),
             HiZOutput(2, 2, 1, .625f, 2), HiZOutput(1, 2, 1, .6875f, 3)]);

        // Distinct saved values prove the cached page was not rewritten, while
        // the neighboring static-dirty page must read and reduce the new atlas.
        for (int mip = 0; mip < 4; mip++)
        {
            int size = 8 >> mip;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size * 2; x++)
                Assert.Equal(x < size ? .5f + mip * .0625f : .25f,
                    BitConverter.UInt32BitsToSingle(result[mip][y * size * 2 + x]));
        }
    }

    [Fact]
    public void Dynamic_only_pages_keep_their_existing_top_static_mips()
    {
        uint[] source = new uint[16 * 8];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 16; x++)
            source[y * 16 + x] = BitConverter.SingleToUInt32Bits(x < 8 ? .25f : .375f);
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteImages(
            "vsm_page_hiz_reduce.slang", "ReduceVirtualShadowPageHiZ", new(2, 1, 1),
            HiZInputs(128, 2, 3, 4, 0, [4u, 8u]),
            [new(16, 8, source, 4, PixelFormat: Format.R32Float,
                ViewDimension: TextureViewDimension.Texture2DArray, Name: "VirtualShadowPageHiZSource"),
             HiZOutput(4, 2, 1, .5f, 0), HiZOutput(2, 2, 1, .5625f, 1),
             HiZOutput(1, 2, 1, .625f, 2), HiZOutput(1, 2, 1, .6875f, 3)]);

        for (int mip = 0; mip < 3; mip++)
        {
            int size = 4 >> mip;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size * 2; x++)
                Assert.Equal(x < size ? .5f + mip * .0625f : .375f,
                    BitConverter.UInt32BitsToSingle(result[mip][y * size * 2 + x]));
        }
        Assert.All(result[3], value => Assert.Equal(.6875f, BitConverter.UInt32BitsToSingle(value)));
    }

    [Fact]
    public void Dynamic_HiZ_updates_both_depth_layers_on_dynamic_only_pages()
    {
        uint[] atlas = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.875f), 16 * 16)
            .Concat(Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.25f), 16 * 16)).ToArray();
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteImages(
            "vsm_page_hiz.slang", "InitializeVirtualShadowPageHiZ", new(1, 1, 1),
            HiZInputs(16, 1, 0, 0, 1, [4u]),
            [new(16, 16, atlas, 4, ViewDimension: TextureViewDimension.Texture2DArray,
                Layers: 2, Name: "ShadowPhysicalAtlas"),
             HiZOutput(8, 1, 2, .5f, 0), HiZOutput(4, 1, 2, .5f, 1),
             HiZOutput(2, 1, 2, .5f, 2), HiZOutput(1, 1, 2, .5f, 3)]);

        for (int mip = 0; mip < 4; mip++)
        {
            int layerPixels = (8 >> mip) * Math.Max(8 >> mip, 2);
            for (int pixel = 0; pixel < (8 >> mip) * (8 >> mip); pixel++)
            {
                Assert.Equal(.125f, BitConverter.UInt32BitsToSingle(result[mip][pixel]));
                Assert.Equal(.75f, BitConverter.UInt32BitsToSingle(result[mip][layerPixels + pixel]));
            }
        }
    }

    [Fact]
    public void Dynamic_HiZ_reduces_both_top_depth_layers_on_dynamic_only_pages()
    {
        uint[] source = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.125f), 8 * 8)
            .Concat(Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.75f), 8 * 8)).ToArray();
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteImages(
            "vsm_page_hiz_reduce.slang", "ReduceVirtualShadowPageHiZ", new(1, 1, 1),
            HiZInputs(128, 1, 3, 4, 1, [4u]),
            [new(8, 8, source, 4, PixelFormat: Format.R32Float,
                ViewDimension: TextureViewDimension.Texture2DArray, Layers: 2, Name: "VirtualShadowPageHiZSource"),
             HiZOutput(4, 1, 2, .5f, 0), HiZOutput(2, 1, 2, .5f, 1),
             HiZOutput(1, 1, 2, .5f, 2), HiZOutput(1, 1, 2, .5f, 3)]);

        for (int mip = 0; mip < 3; mip++)
        {
            int layerPixels = (4 >> mip) * Math.Max(4 >> mip, 2);
            for (int pixel = 0; pixel < (4 >> mip) * (4 >> mip); pixel++)
            {
                Assert.Equal(.125f, BitConverter.UInt32BitsToSingle(result[mip][pixel]));
                Assert.Equal(.75f, BitConverter.UInt32BitsToSingle(result[mip][layerPixels + pixel]));
            }
        }
        Assert.All(result[3], value => Assert.Equal(.5f, BitConverter.UInt32BitsToSingle(value)));
    }

    private static BufferInput[] HiZInputs(uint pageSize, uint pagesPerRow, uint sourceMip,
        uint destinationMip, uint staticLayerOffset, uint[] pageFlags)
    {
        uint[] active = new uint[pageFlags.Length * 4];
        uint[] metadata = new uint[pageFlags.Length * 8];
        for (uint page = 0; page < pageFlags.Length; page++)
        {
            active[page * 4] = page;
            active[page * 4 + 1] = page;
            active[page * 4 + 3] = 7;
            metadata[page * 8 + 1] = 7;
            metadata[page * 8 + 3] = pageFlags[page];
            metadata[page * 8 + 6] = 7;
        }
        return [new("PageHiZ", [pageSize, pagesPerRow, sourceMip, destinationMip, staticLayerOffset, 0, 0, 0],
                ResourceBindingType.ConstantBuffer, 0),
            new("ActivePhysicalPages", active, ResourceBindingType.BufferSrv, 16),
            new("ActivePhysicalPageCount", [checked((uint)pageFlags.Length)], ResourceBindingType.BufferSrv, 4),
            new("PageHiZMetadata", metadata, ResourceBindingType.BufferSrv, 32)];
    }

    private static SampledImage HiZOutput(uint size, uint pagesPerRow, uint layers, float initialValue, int mip) =>
        // Keep each readback layer on a 512-byte footprint boundary.
        new(size * pagesPerRow, Math.Max(size, 2u),
            Enumerable.Repeat(BitConverter.SingleToUInt32Bits(initialValue), checked((int)(size * Math.Max(size, 2u) * pagesPerRow * layers))).ToArray(),
            5 + mip, PixelFormat: Format.R32Float, ViewDimension: TextureViewDimension.Texture2DArray,
            Type: ResourceBindingType.TextureUav, Layers: layers,
            Name: mip == 0 ? "VirtualShadowPageHiZDestination" : $"VirtualShadowPageHiZDestination{mip}");

    private static Buffer Upload(IGraphicsBackend backend, Device device, ReadOnlySpan<uint> values, BufferUsages usages, int bytes)
    {
        Buffer buffer = backend.CreateBuffer(device, new BufferDesc(checked((ulong)bytes), usages), MemoryType.Upload);
        using MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
        mapping.Bytes.Clear();
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
        mapping.Flush(new BufferRange(0, checked((ulong)bytes)));
        return buffer;
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "assets", "Shaders");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Could not find the virtual-shadow shaders.");
    }
}
