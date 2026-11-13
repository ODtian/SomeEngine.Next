using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowFrameTests(VirtualShadowGpuResources gpuResources)
{
    [Theory]
    [InlineData(257)]
    [InlineData(1025)]
    public void FrameResetEmptiesWorkAndPageRectanglesWithDifferentResourceLengths(uint gridWords)
    {
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("shadow_frame_reset","#include \"vsm_frame_reset.slang\"");
        var counters = new List<VirtualShadowGpuGraph.Readback<uint>>();
        VirtualShadowGpuGraph.Readback<PageRectangle> uncached,allocated;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            (string Name,uint Count)[] lengths =
            [
                ("ActivePageCount",1),("RasterPageMasks",gridWords),("AllocatedPageHierarchy",4),
                ("ClusterDispatchArguments",80),("PageDispatchArguments",2),("PageRequestDispatchArguments",gridWords - 13),
                ("AllocationFailures",7),("PolicyRasterWorkCount",33),("DepthWorkCount",5),
            ];
            var resources = new Dictionary<string,GraphParameterResourceBinding>();
            var buffers = new List<(GraphBufferId Id,uint Count)>();
            foreach ((string name,uint count) in lengths)
            {
                GraphBufferId buffer = scene.Data(ref edit,Enumerable.Repeat(9u,(int)count).ToArray());
                buffers.Add((buffer,count));
                resources[name] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(buffer),
                    PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete);
            }
            GraphBufferId uncachedRects = scene.Data(ref edit,Enumerable.Repeat(new PageRectangle(1,2,7,8),3).ToArray());
            GraphBufferId allocatedRects = scene.Data(ref edit,Enumerable.Repeat(new PageRectangle(3,4,9,10),5).ToArray());
            resources["UncachedPageRectBounds"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(uncachedRects,structureStride:16),
                PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete);
            resources["AllocatedPageRectBounds"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(allocatedRects,structureStride:16),
                PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete);
            GraphPassId reset = scene.Add(ref edit,shader,"ResetVirtualShadowFrame",new((gridWords + 255u) / 256u,1,1),resources);
            foreach ((GraphBufferId id,uint count) in buffers) counters.Add(scene.Read<uint>(ref edit,id,count,reset));
            uncached = scene.Read<PageRectangle>(ref edit,uncachedRects,3,reset);
            allocated = scene.Read<PageRectangle>(ref edit,allocatedRects,5,reset);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        Assert.All(counters,readback => Assert.All(readback.Values,value => Assert.Equal(0u,value)));
        Assert.All(uncached.Values,rectangle => Assert.True(rectangle.IsEmpty));
        Assert.All(allocated.Values,rectangle => Assert.True(rectangle.IsEmpty));
    }

    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageRectangle(uint Left,uint Top,uint Right,uint Bottom)
    {
        internal bool IsEmpty => Left > Right || Top > Bottom;
    }
}
