using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowHiZTests(VirtualShadowGpuResources gpuResources)
{
    [Fact]
    public void ReorderedActivePagesProduceTheSameDepthPyramidAsTheirAtlasDepth()
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader initialize = scene.Load("page_hiz_initialize","#include \"vsm_page_hiz.slang\"");
        Shader reduce = scene.Load("page_hiz_reduce","#include \"vsm_page_hiz_reduce.slang\"");
        Shader read = scene.Load("page_hiz_observe","""
            import depth_page_hiz;
            Texture2DArray<float> HiZ;
            RWStructuredBuffer<float2> Result;
            [shader("compute")] [numthreads(64,1,1)]
            void ReadDepthPyramid(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= 2u * 5461u) return;
                uint page = id.x / 5461u;
                uint pixel = id.x % 5461u;
                uint mip = 0u;
                uint size = 64u;
                while (pixel >= size * size) { pixel -= size * size; size >>= 1u; mip++; }
                uint3 address = CalculateDepthPageHiZPixel(page,uint2(pixel % size,pixel / size),size,2u);
                Result[id.x] = float2(HiZ.Load(int4(address,mip)),HiZ.Load(int4(address + uint3(0,0,1),mip)));
            }
            """);
        float[][] depth = new float[2][];
        uint[] pixels = new uint[256 * 128 * 2];
        for (int page = 0; page < 2; page++)
        {
            depth[page] = new float[128 * 128];
            for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float value = ((x * 3 + y * 5 + page * 257) % 1024) / 1024f;
                if (x == 117 && y == 97) value = 1f;
                depth[page][y * 128 + x] = value;
                pixels[256 * 128 + y * 256 + page * 128 + x] = BitConverter.SingleToUInt32Bits(1f - value);
            }
        }
        VirtualShadowGpuGraph.Readback<Vector2> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphTextureId atlas = scene.Image(ref edit,256,128,2,pixels);
            GraphTextureId pyramid = edit.CreatePersistentTexture(new(TextureDimension.Texture2D,128,128,1,7,2,1,
                Format.R32Float,TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment));
            GraphPassId clear = edit.AddCopyFramePass("Clear depth pyramid",PassQueueSelection.AnyOfType(QueueType.Graphics),pyramid,
                new PassOptions(Culling:PassCullingMode.NeverCull),
                static(ref PassDefinition pass,ref GraphTextureId image) => pass.Write(image,new(0,7,0,2,TextureAspects.Color),
                    PipelineSync.RenderTarget,ResourceAccess.RenderTarget,TextureLayout.RenderTarget,WriteCoverage.Complete),
                static(ref CopyPassCommandScope commands,in GraphTextureId image) =>
                    commands.ClearTexture(commands.GetTexture(image),new(0,7,0,2,TextureAspects.Color),Vector4.Zero));
            GraphBufferSrvId active = scene.Input(ref edit,new[] { new PageWork(1,101,uint.MaxValue,7),new PageWork(0,999,uint.MaxValue,7) });
            GraphBufferSrvId count = scene.Input(ref edit,new[] { 2u });
            GraphBufferSrvId metadata = scene.Input(ref edit,new VirtualShadowPageMetadata[2]);
            var first = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["PageHiZ"] = GraphParameterResourceBinding.ConstantBuffer(scene.Constants(ref edit,new HiZParameters(128,2,0,0,1)),PipelineSync.ComputeShading),
                ["ActivePhysicalPages"] = GraphParameterResourceBinding.ReadOnlyBuffer(active,PipelineSync.ComputeShading),
                ["ActivePhysicalPageCount"] = GraphParameterResourceBinding.ReadOnlyBuffer(count,PipelineSync.ComputeShading),
                ["PageHiZMetadata"] = GraphParameterResourceBinding.ReadOnlyBuffer(metadata,PipelineSync.ComputeShading),
                ["ShadowPhysicalAtlas"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(atlas,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
            };
            string[] destinations = ["VirtualShadowPageHiZDestination","VirtualShadowPageHiZDestination1",
                "VirtualShadowPageHiZDestination2","VirtualShadowPageHiZDestination3"];
            for (uint mip = 0; mip < 4; mip++)
                first[destinations[mip]] = GraphParameterResourceBinding.StorageTexture(
                    edit.CreateTextureUav(pyramid,new(mip,1,0,2,TextureAspects.Color),Format.R32Float,TextureViewDimension.Texture2DArray),
                    PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Partial);
            GraphPassId initialized = scene.Add(ref edit,initialize,"InitializeVirtualShadowPageHiZ",new(2,8,8),first,clear);
            var second = new Dictionary<string,GraphParameterResourceBinding>(first)
            {
                ["PageHiZ"] = GraphParameterResourceBinding.ConstantBuffer(scene.Constants(ref edit,new HiZParameters(128,2,3,4,1)),PipelineSync.ComputeShading),
                ["VirtualShadowPageHiZSource"] = GraphParameterResourceBinding.SampledTexture(
                    edit.CreateTextureSrv(pyramid,new(3,1,0,2,TextureAspects.Color),Format.R32Float,TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
            };
            for (uint step = 0; step < 4; step++)
                second[destinations[step]] = GraphParameterResourceBinding.StorageTexture(
                    edit.CreateTextureUav(pyramid,new(Math.Min(4u + step,6u),1,0,2,TextureAspects.Color),Format.R32Float,TextureViewDimension.Texture2DArray),
                    PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Partial);
            GraphPassId reduced = scene.Add(ref edit,reduce,"ReduceVirtualShadowPageHiZ",new(2,1,1),second,initialized);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<Vector2>(ref edit,10922);
            var reading = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["HiZ"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(pyramid,dimension:TextureViewDimension.Texture2DArray),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:8),
                    PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId copied = scene.Add(ref edit,read,"ReadDepthPyramid",new(171,1,1),reading,reduced);
            observed = scene.Read<Vector2>(ref edit,output,10922,copied);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        int offset = 0;
        foreach (float[] page in depth)
        {
            float[] previous = page;
            for (int size = 64; size != 0; size >>= 1)
            {
                float[] expected = new float[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int source = y * 2 * size * 2 + x * 2;
                    float value = Math.Max(Math.Max(previous[source],previous[source + 1]),
                        Math.Max(previous[source + size * 2],previous[source + size * 2 + 1]));
                    expected[y * size + x] = value;
                    Assert.Equal(new Vector2(value),observed.Values[offset++]);
                }
                previous = expected;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageWork(uint Page,uint TableIndex,uint PreviousPage,uint Generation);

    [StructLayout(LayoutKind.Sequential,Pack = 4,Size = 32)]
    private readonly record struct HiZParameters(uint PageSize,uint AtlasPagesPerRow,uint SourceMip,uint DestinationMip,uint StaticLayerOffset);
}
