using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class HierarchicalDepthTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void EveryMipMatchesTheMaximumDepthIncludingOddEdgesAndArrayLayers()
    {
        string directory = ShaderDirectory();
        D3D12TestShader initialize = Compile("hiz_init.slang", "InitHiZ");
        D3D12TestShader pair = Compile("hiz_reduce_pair.slang", "ReduceHiZPair");
        D3D12TestShader tail = Compile("hiz_reduce.slang", "ReduceHiZ");
        using IGraphicsBackend backend = new SomeEngine.Graphics.Validation.ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline firstPipeline = backend.CreateComputePipeline(device, new(initialize.SlangCode, initialize.Reflection, initialize.GetEntryPoint(0), initialize.GetEntryCode(0)));
        using Pipeline pairPipeline = backend.CreateComputePipeline(device, new(pair.SlangCode, pair.Reflection, pair.GetEntryPoint(0), pair.GetEntryCode(0)));
        using Pipeline tailPipeline = backend.CreateComputePipeline(device, new(tail.SlangCode, tail.Reflection, tail.GetEntryPoint(0), tail.GetEntryCode(0)));
        foreach ((int width, int height) in new[]
        {
            (1, 1), (1, 65), (7, 3), (16, 16), (16, 17), (17, 16),
            (31, 47), (32, 33), (33, 32), (48, 48), (129, 65), (1280, 720), (2051, 1031),
        })
            Check(width, height);

        D3D12TestShader Compile(string file, string entry) => shaderCompiler.CompileWithSearchPaths(
            entry, $"#include \"{file}\"", [new(entry, SlangStage.Compute)], "sm_6_0", directory);

        void Check(int width, int height)
        {
            const int layers = 2;
            uint mipCount = (uint)BitOperations.Log2((uint)Math.Max(width, height)) + 1;
            uint pitch = ((uint)width * 4 + 255u) & ~255u;
            ulong layerBytes = (pitch * (ulong)height + 511u) & ~511ul;
            using Buffer upload = backend.CreateBuffer(device, new(layerBytes * layers, BufferUsages.CopySource), MemoryType.Upload);
            float[][] pixels = new float[layers][];
            using (MappedBuffer mapping = backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                for (int layer = 0; layer < layers; layer++)
                {
                    pixels[layer] = new float[width * height];
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        float value = ((x * 17 + y * 31 + layer * 127) % 4093) / 4096f;
                        if (x == width - 1 || y == height - 1) value = 1f;
                        pixels[layer][y * width + x] = value;
                        MemoryMarshal.Write(mapping.Bytes.Slice((int)(layerBytes * (ulong)layer + (ulong)y * pitch + (ulong)x * 4), 4), in value);
                    }
                }
                mapping.Flush(mapping.Range);
            }
            using Texture depth = backend.CreateTexture(device, new(TextureDimension.Texture2D, (uint)width, (uint)height,
                1, 1, layers, 1, Format.R32Float, TextureUsages.CopyDestination | TextureUsages.Sampled));
            using TextureSrv depthView = backend.CreateTextureSrv(device, new(depth,
                new(0, 1, 0, layers, TextureAspects.Color), Format.R32Float, TextureViewDimension.Texture2DArray));
            using Texture hierarchy = backend.CreateTexture(device, new(TextureDimension.Texture2D, (uint)width, (uint)height,
                1, mipCount, layers, 1, Format.R32Float, TextureUsages.Storage | TextureUsages.CopySource));
            var views = new List<TextureUav>();
            var regions = new List<(uint Mip, uint Layer, uint Width, uint Height, uint Pitch, ulong Offset)>();
            ulong totalBytes = 0;
            for (uint layer = 0; layer < layers; layer++)
            for (uint mip = 0; mip < mipCount; mip++)
            {
                uint w = Math.Max((uint)width >> (int)mip, 1u), h = Math.Max((uint)height >> (int)mip, 1u);
                uint rowPitch = (w * 4 + 255u) & ~255u;
                regions.Add((mip, layer, w, h, rowPitch, totalBytes));
                totalBytes += (rowPitch * (ulong)h + 511u) & ~511ul;
            }
            using Buffer readback = backend.CreateBuffer(device, new(totalBytes, BufferUsages.CopyDestination), MemoryType.Readback);
            try
            {
                for (uint mip = 0; mip < mipCount; mip++)
                    views.Add(backend.CreateTextureUav(device, new(hierarchy, new(mip, 1, 0, layers, TextureAspects.Color),
                        Format.R32Float, TextureViewDimension.Texture2DArray)));
                using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
                backend.Begin(context, new CommandRecordingDesc(96, 8, 96));
                TextureSubresourceRange depthRange = new(0, 1, 0, layers, TextureAspects.Color);
                TextureSubresourceRange hierarchyRange = new(0, mipCount, 0, layers, TextureAspects.Color);
                backend.Barrier(context, new TextureBarrier(depth, depthRange, PipelineSync.None, PipelineSync.Copy,
                    ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
                for (uint layer = 0; layer < layers; layer++)
                    backend.CopyBufferToTexture(context, new(upload, layerBytes * layer, pitch, (uint)height,
                        depth, 0, layer, TextureAspects.Color, 0, 0, 0, (uint)width, (uint)height, 1));
                backend.Barrier(context, new TextureBarrier(depth, depthRange, PipelineSync.Copy, PipelineSync.ComputeShading,
                    ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
                backend.Barrier(context, new TextureBarrier(hierarchy, hierarchyRange, PipelineSync.None, PipelineSync.ComputeShading,
                    ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess, TextureLayout.Undefined, TextureLayout.UnorderedAccess));
                var bindings = new List<ResourceBinding> { ResourceBinding.SampledTexture(depthView) };
                for (int mip = 0; mip < 5; mip++) bindings.Add(ResourceBinding.StorageTexture(views[Math.Min(mip, views.Count - 1)]));
                Dispatch(firstPipeline, initialize, new(Math.Max((uint)width >> 4, 1u), Math.Max((uint)height >> 4, 1u), layers));
                int sourceMip = Math.Min(4, views.Count - 1);
                while (sourceMip + 1 < views.Count)
                {
                    backend.Barrier(context, new TextureBarrier(hierarchy, hierarchyRange, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess, TextureLayout.UnorderedAccess));
                    bool finish = Math.Max(width >> (sourceMip + 1), 1) <= 64 && Math.Max(height >> (sourceMip + 1), 1) <= 64;
                    int count = finish ? 7 : 2;
                    bindings.Clear();
                    bindings.Add(ResourceBinding.StorageTexture(views[sourceMip]));
                    for (int mip = 1; mip <= count; mip++) bindings.Add(ResourceBinding.StorageTexture(views[Math.Min(sourceMip + mip, views.Count - 1)]));
                    sourceMip = Math.Min(sourceMip + count, views.Count - 1);
                    Dispatch(finish ? tailPipeline : pairPipeline, finish ? tail : pair,
                        finish ? new(1, 1, layers) : new((uint)(Math.Max(width >> sourceMip, 1) + 7) / 8,
                            (uint)(Math.Max(height >> sourceMip, 1) + 7) / 8, layers));
                }
                backend.Barrier(context, new TextureBarrier(hierarchy, hierarchyRange, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource, TextureLayout.UnorderedAccess, TextureLayout.CopySource));
                foreach (var region in regions)
                    backend.CopyTextureToBuffer(context, new(readback, region.Offset, region.Pitch, region.Height, hierarchy,
                        region.Mip, region.Layer, TextureAspects.Color, 0, 0, 0, region.Width, region.Height, 1));
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
                using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
                result.Invalidate(result.Range);
                foreach (var region in regions)
                {
                    float[] expected = pixels[region.Layer];
                    if (region.Mip != 0)
                    {
                        int previousWidth = Math.Max(width >> ((int)region.Mip - 1), 1);
                        int previousHeight = Math.Max(height >> ((int)region.Mip - 1), 1);
                        expected = new float[region.Width * region.Height];
                        for (int y = 0; y < region.Height; y++)
                        for (int x = 0; x < region.Width; x++)
                            for (int sy = y * 2; sy < (y + 1 == region.Height ? previousHeight : y * 2 + 2); sy++)
                            for (int sx = x * 2; sx < (x + 1 == region.Width ? previousWidth : x * 2 + 2); sx++)
                                expected[y * region.Width + x] = Math.Max(expected[y * region.Width + x], pixels[region.Layer][sy * previousWidth + sx]);
                        pixels[region.Layer] = expected;
                    }
                    for (int y = 0; y < region.Height; y++)
                    for (int x = 0; x < region.Width; x++)
                    {
                        float actual = MemoryMarshal.Read<float>(result.Bytes.Slice((int)(region.Offset + (ulong)y * region.Pitch + (ulong)x * 4), 4));
                        Assert.True(actual == expected[y * region.Width + x], $"{width}x{height}, layer {region.Layer}, mip {region.Mip}, ({x},{y}): {actual} != {expected[y * region.Width + x]}");
                    }
                }

                void Dispatch(Pipeline pipeline, D3D12TestShader shader, DispatchArguments arguments)
                {
                    backend.SetPipeline(context, pipeline);
                    backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, CollectionsMarshal.AsSpan(bindings), []));
                    backend.Dispatch(context, arguments);
                }
            }
            finally { foreach (TextureUav view in views) view.Dispose(); }
        }
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders"))) return Path.Combine(directory.FullName, "assets", "Shaders");
        throw new DirectoryNotFoundException();
    }
}
