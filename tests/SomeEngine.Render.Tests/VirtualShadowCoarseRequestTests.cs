using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowCoarseRequestTests(VirtualShadowGpuResources gpuResources)
{
    [Fact]
    public void CoarseRequestsCoverTheDirectionalCenterAndEachLocalProjection()
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("coarse_shadow_coverage","""
            #include "vsm_mark_coarse_pages.slang"
            RWStructuredBuffer<uint4> Result;
            [shader("compute")] [numthreads(256,1,1)]
            void ObserveCoarseCoverage(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= CoarsePageDemand.PageTableEntryCount) return;
                uint viewIndex = VirtualShadowViewFromPageTableIndex(ShadowViews,CoarsePageDemand.ViewCount,id.x);
                uint mip;
                uint2 page;
                VirtualShadowDecodePageTableIndex(ShadowViews[viewIndex],id.x,mip,page);
                Result[id.x] = uint4(RequestedPageFlags[id.x] != 0u ? ShadowViews[viewIndex].LightKind + 1u : 0u,mip,page);
            }
            """);
        VirtualShadowView[] views = new VirtualShadowView[3];
        VirtualShadowRange[] ranges = new VirtualShadowRange[4];
        ranges[0] = new(0,0,0,uint.MaxValue);
        for (uint index = 0; index < 3; index++)
        {
            views[index] = new()
            {
                LightViewProjection = Matrix4x4.Identity,VirtualResolution = 2048,PageSize = 128,
                AtlasSize = 512,AtlasPagesPerRow = 4,MaxPages = 16,MipLevelCount = 5,
                CacheGeneration = 7,RangeId = index + 1u,RangeGeneration = 3,PageTableOffset = index * 341u,
                LightKind = (VirtualShadowLightKind)index,Flags = VirtualShadowViewFlags.CoarseClipLevel,
                VirtualPageOriginX = -17,VirtualPageOriginY = 5,WorldPageOriginX = -17,WorldPageOriginY = 5,
                WorldUnitsPerCentimeter = 1,
            };
            ranges[index + 1u] = new(index * 341u,341,3,index);
        }
        VirtualShadowGpuGraph.Readback<CoarsePage> observed;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphBufferId flags = scene.Data(ref edit,new uint[1023]);
            GraphBufferId indices = scene.Data(ref edit,new uint[1023]);
            GraphBufferId count = scene.Data(ref edit,new uint[1]);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<CoarsePage>(ref edit,1023);
            GraphTextureId mask = scene.Image(ref edit,1,1,1,new uint[1]);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["CoarsePageDemand"] = GraphParameterResourceBinding.ConstantBuffer(scene.Constants(ref edit,new CoarseParameters(3,1023,9,0)),PipelineSync.ComputeShading),
                ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,views),PipelineSync.ComputeShading),
                ["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,ranges),PipelineSync.ComputeShading),
                ["RequestedPageFlags"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(flags,structureStride:4),PipelineSync.ComputeShading),
                ["RequestedPageIndices"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(indices,structureStride:4),PipelineSync.ComputeShading),
                ["RequestedPageCount"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(count,structureStride:4),PipelineSync.ComputeShading),
                ["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(edit.CreateTextureUav(mask,dimension:TextureViewDimension.Texture2D),PipelineSync.ComputeShading),
                ["Result"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:16),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphPassId request = scene.Add(ref edit,shader,"MarkVirtualShadowCoarsePages",new(1,1,1),resources);
            GraphPassId observe = scene.Add(ref edit,shader,"ObserveCoarseCoverage",new(4,1,1),resources,request);
            observed = scene.Read<CoarsePage>(ref edit,output,1023,observe);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        CoarsePage[] expected =
        [
            new(1,0,7,7),new(1,0,7,8),new(1,0,8,7),new(1,0,8,8),new(2,4,0,0),new(3,4,0,0),
        ];
        Assert.Equal(expected.OrderBy(page => (page.Light,page.Mip,page.X,page.Y)),
            observed.Values.Where(page => page.Light != 0).OrderBy(page => (page.Light,page.Mip,page.X,page.Y)));
    }

    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct CoarsePage(uint Light,uint Mip,uint X,uint Y);
    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct CoarseParameters(uint ViewCount,uint PageTableEntryCount,uint FrameIndex,uint Padding);
}
