using System.Buffers.Binary;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowHiZTests
{
    [Fact]
    public void Two_dispatches_match_a_cpu_max_pyramid_for_reordered_physical_pages()
    {
        using D3D12TestShaderProgram initialize = D3D12TestShaderProgram.CompileWithSearchPaths(
            "shadow_hiz_initialize", "#include \"vsm_page_hiz.slang\"",
            [new("InitializeVirtualShadowPageHiZ", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        using D3D12TestShaderProgram reduce = D3D12TestShaderProgram.CompileWithSearchPaths(
            "shadow_hiz_reduce", "#include \"vsm_page_hiz_reduce.slang\"",
            [new("ReduceVirtualShadowPageHiZ", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline first = backend.CreateComputePipeline(device, new ComputePipelineDesc(initialize.Program, initialize.GetEntryPoint(0)));
        using Pipeline second = backend.CreateComputePipeline(device, new ComputePipelineDesc(reduce.Program, reduce.GetEntryPoint(0)));
        using Buffer uniforms0 = Upload(backend, device, [128u, 2u, 0u, 0u], BufferUsages.Constant, 256);
        using Buffer uniforms1 = Upload(backend, device, [128u, 2u, 3u, 4u], BufferUsages.Constant, 256);
        using BufferCbv cbv0 = backend.CreateBufferCbv(device, new BufferCbvDesc(uniforms0, new BufferRange(0, 256)));
        using BufferCbv cbv1 = backend.CreateBufferCbv(device, new BufferCbvDesc(uniforms1, new BufferRange(0, 256)));
        using Buffer active = Upload(backend, device, [1u, 101u, 0u, 999u], BufferUsages.ShaderRead, 16);
        using BufferSrv activeView = backend.CreateBufferSrv(device, new BufferSrvDesc(active, BufferRange.Whole, StructureStride: 8));
        var depth = new float[2][];
        var pixels = new uint[256 * 128];
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
        using Buffer upload = Upload(backend, device, pixels, BufferUsages.CopySource, pixels.Length * sizeof(uint));
        using Texture atlas = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            256, 128, 1, 1, 1, 1, Format.R32UInt, TextureUsages.Sampled | TextureUsages.CopyDestination));
        using TextureSrv atlasView = backend.CreateTextureSrv(device, new TextureSrvDesc(atlas,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D));
        using Texture hiz = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            64, 64, 1, 7, 2, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource));
        using TextureSrv source = backend.CreateTextureSrv(device, new TextureSrvDesc(hiz,
            new TextureSubresourceRange(3, 1, 0, 2, TextureAspects.Color), Format.R32Float, TextureViewDimension.Texture2DArray));
        TextureUav[] destinations = new TextureUav[7];
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(2u * 128u * 256u, BufferUsages.CopyDestination), MemoryType.Readback);
        try
        {
            for (uint mip = 0; mip < 7; mip++)
                destinations[mip] = backend.CreateTextureUav(device, new TextureUavDesc(hiz,
                    new TextureSubresourceRange(mip, 1, 0, 2, TextureAspects.Color), Format.R32Float, TextureViewDimension.Texture2DArray));
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
            backend.Barrier(context, new TextureBarrier(atlas, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination,
                TextureLayout.Undefined, TextureLayout.CopyDestination));
            backend.CopyBufferToTexture(context, new BufferTextureCopy(upload, 0, 1024, 128, atlas,
                0, 0, TextureAspects.Color, 0, 0, 0, 256, 128, 1));
            backend.Barrier(context, new TextureBarrier(atlas, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(0, 7, 0, 2, TextureAspects.Color),
                PipelineSync.None, PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
                TextureLayout.Undefined, TextureLayout.UnorderedAccess));
            backend.SetPipeline(context, first);
            backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                initialize.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null,
                [ResourceBinding.ConstantBuffer(cbv0), ResourceBinding.ReadOnlyBuffer(activeView), ResourceBinding.SampledTexture(atlasView),
                    ResourceBinding.StorageTexture(destinations[0]), ResourceBinding.StorageTexture(destinations[1]),
                    ResourceBinding.StorageTexture(destinations[2]), ResourceBinding.StorageTexture(destinations[3])], []));
            backend.Dispatch(context, new DispatchArguments(2, 8, 8));
            backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(3, 1, 0, 2, TextureAspects.Color),
                PipelineSync.ComputeShading, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.ShaderResource,
                TextureLayout.UnorderedAccess, TextureLayout.ShaderResource));
            backend.SetPipeline(context, second);
            backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                reduce.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null,
                [ResourceBinding.ConstantBuffer(cbv1), ResourceBinding.ReadOnlyBuffer(activeView), ResourceBinding.SampledTexture(source),
                    ResourceBinding.StorageTexture(destinations[4]), ResourceBinding.StorageTexture(destinations[5]),
                    ResourceBinding.StorageTexture(destinations[6]), ResourceBinding.StorageTexture(destinations[6])], []));
            backend.Dispatch(context, new DispatchArguments(2, 1, 1));
            ulong offset = 0;
            for (uint layer = 0; layer < 2; layer++)
            for (uint mip = 0; mip < 7; mip++)
            {
                uint size = 64u >> checked((int)mip);
                backend.Barrier(context, new TextureBarrier(hiz, new TextureSubresourceRange(mip, 1, layer, 1, TextureAspects.Color),
                    PipelineSync.ComputeShading, PipelineSync.Copy,
                    mip == 3 ? ResourceAccess.ShaderResource : ResourceAccess.UnorderedAccess, ResourceAccess.CopySource,
                    mip == 3 ? TextureLayout.ShaderResource : TextureLayout.UnorderedAccess, TextureLayout.CopySource));
                backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, offset, 256, size, hiz,
                    mip, layer, TextureAspects.Color, 0, 0, 0, size, size, 1));
                offset += Math.Max(size, 2u) * 256u;
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
                    var expected = new float[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int p = (y * 2) * (size * 2) + x * 2;
                        expected[y * size + x] = Math.Max(Math.Max(previous[p], previous[p + 1]),
                            Math.Max(previous[p + size * 2], previous[p + size * 2 + 1]));
                        float actual = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(
                            mapping.Bytes.Slice(readOffset + y * 256 + x * 4, 4)));
                        Assert.Equal(expected[y * size + x], actual);
                    }
                    readOffset += Math.Max(size, 2) * 256;
                    previous = expected;
                }
            }
        }
        finally
        {
            foreach (TextureUav? view in destinations) view?.Dispose();
        }
    }

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
