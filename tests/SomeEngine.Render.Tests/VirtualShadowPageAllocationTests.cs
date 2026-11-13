using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowPageAllocationTests(VirtualShadowGpuResources gpuResources)
{
    [Theory]
    [InlineData(7)]
    [InlineData(63)]
    public void AllocationReusesAvailablePagesOnceAndReleasesTheirPreviousMappings(uint availableCount)
    {
        const uint capacity = 256,requests = 255,entries = 1024;
        using var scene = new VirtualShadowGpuGraph(gpuResources);
        Shader shader = scene.Load("shadow_page_pressure","""
            #include "vsm_allocate_pages.slang"
            StructuredBuffer<uint> InitialAvailablePages;
            RWStructuredBuffer<uint> InitialRequestFlags;
            RWStructuredBuffer<uint2> Mappings;
            [shader("compute")] [numthreads(256,1,1)]
            void InitializeAllocationInputs(uint3 id : SV_DispatchThreadID)
            {
                uint capacity = PageAllocation.MaxPages;
                if (id.x <= capacity)
                {
                    for (uint list = 0u; list < VSM_PAGE_LIST_COUNT; list++)
                        ShadowPageLists[ShadowPageListStart(list,capacity) + id.x] = 0u;
                    uint count,stride;
                    InitialAvailablePages.GetDimensions(count,stride);
                    if (id.x < count)
                        ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_AVAILABLE,capacity) + id.x] = InitialAvailablePages[id.x];
                    if (id.x == capacity)
                        ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_AVAILABLE,capacity)] = count;
                }
                if (id.x < PageAllocation.PageTableEntryCount) InitialRequestFlags[id.x] = VSM_PAGE_REQUEST_DETAIL;
            }
            [shader("compute")] [numthreads(256,1,1)]
            void ObservePageMappings(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= PageAllocation.PageTableEntryCount) return;
                uint page;
                bool mapped = DecodeVirtualShadowPage(ShadowPageTable[id.x],ShadowViews[0].AtlasPagesPerRow,page);
                Mappings[id.x] = uint2(mapped ? 1u : 0u,mapped ? page : 0u);
            }
            """);
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.Identity,VirtualResolution = 4096,PageSize = 128,
            AtlasSize = 2048,AtlasPagesPerRow = 16,MaxPages = capacity,MipLevelCount = 1,
            CacheGeneration = 5,WorldUnitsPerCentimeter = 1,RangeId = 1,RangeGeneration = 4,
        };
        uint[] table = new uint[entries],assignments = Enumerable.Repeat(uint.MaxValue,(int)capacity).ToArray();
        VirtualShadowPageMetadata[] metadata = new VirtualShadowPageMetadata[capacity];
        for (uint page = 0; page < availableCount; page++)
        {
            uint index = 512u + page;
            table[index] = VirtualShadowPageEntry.Encode(page,16);
            assignments[page] = index;
            metadata[page] = new()
            {
                LastRequestedFrame = 1,StaticDepthGeneration = 5,Generation = 5,RangeId = 1,RangeGeneration = 4,
                WorldPageX = (int)(index % 32u),WorldPageY = (int)(index / 32u),
            };
        }
        VirtualShadowGpuGraph.Readback<PageMapping> observed;
        VirtualShadowGpuGraph.Readback<VirtualShadowPageMetadata> pageData;
        RenderGraphEdit edit = scene.Graph.BeginEdit();
        try
        {
            GraphBufferId pageTable = scene.Data(ref edit,table);
            GraphBufferId pageAssignments = scene.Data(ref edit,assignments);
            GraphBufferId pageMetadata = scene.Data(ref edit,metadata);
            GraphBufferId lists = scene.Data(ref edit,new uint[4 * (capacity + 1u)]);
            GraphBufferId flags = scene.Data(ref edit,new uint[entries]);
            GraphBufferId output = VirtualShadowGpuGraph.Buffer<PageMapping>(ref edit,entries);
            var resources = new Dictionary<string,GraphParameterResourceBinding>
            {
                ["PageAllocation"] = GraphParameterResourceBinding.ConstantBuffer(scene.Constants(ref edit,
                    new AllocationParameters(entries,entries,capacity,1,10,-1,63,1)),PipelineSync.ComputeShading),
                ["ShadowViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { view }),PipelineSync.ComputeShading),
                ["ShadowRanges"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[]
                {
                    new VirtualShadowRange(0,0,0,uint.MaxValue),new VirtualShadowRange(0,entries,4,0),
                }),PipelineSync.ComputeShading),
                ["RequestedPageIndices"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,
                    Enumerable.Range(0,(int)requests).Select(index => (uint)index).ToArray()),PipelineSync.ComputeShading),
                ["RequestedPageCount"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,new[] { requests }),PipelineSync.ComputeShading),
                ["RequestedPageFlags"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(flags,structureStride:4),PipelineSync.ComputeShading),
                ["InitialRequestFlags"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(flags,structureStride:4),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
                ["InitialAvailablePages"] = GraphParameterResourceBinding.ReadOnlyBuffer(scene.Input(ref edit,
                    Enumerable.Range(0,(int)availableCount).Select(page => (uint)page).ToArray()),PipelineSync.ComputeShading),
                ["ShadowPageTable"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(pageTable,structureStride:4),PipelineSync.ComputeShading),
                ["PageAssignments"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(pageAssignments,structureStride:4),PipelineSync.ComputeShading),
                ["PageMetadata"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(pageMetadata,structureStride:36),PipelineSync.ComputeShading),
                ["ShadowPageLists"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(lists,structureStride:4),PipelineSync.ComputeShading),
                ["ActivePhysicalPages"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(scene.Data(ref edit,new PageWork[capacity]),structureStride:16),PipelineSync.ComputeShading),
                ["ActivePhysicalPageCount"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(scene.Data(ref edit,new uint[1]),structureStride:4),PipelineSync.ComputeShading),
                ["UncachedPageRectBounds"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(scene.Data(ref edit,
                    new[] { new PageRectangle(uint.MaxValue,uint.MaxValue,0,0) }),structureStride:16),PipelineSync.ComputeShading),
                ["RasterTargetPageHierarchy"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(scene.Data(ref edit,new PageMask[63]),structureStride:8),PipelineSync.ComputeShading),
                ["ShadowAllocationFailures"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(scene.Data(ref edit,new uint[3]),structureStride:4),PipelineSync.ComputeShading),
                ["Mappings"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(output,structureStride:8),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
            };
            GraphTextureId receiverMask = scene.Image(ref edit,32,64,1,new uint[32 * 64]);
            resources["PageReceiverMasks"] = GraphParameterResourceBinding.StorageTexture(
                edit.CreateTextureUav(receiverMask,dimension:TextureViewDimension.Texture2D),PipelineSync.ComputeShading);
            for (uint mip = 1; mip <= 7; mip++)
            {
                uint width = Math.Max(32u >> (int)mip,1u),height = Math.Max(64u >> (int)mip,1u);
                GraphTextureId image = scene.Image(ref edit,width,height,1,new uint[width * height]);
                resources["PageReceiverMaskMip" + mip] = GraphParameterResourceBinding.StorageTexture(
                    edit.CreateTextureUav(image,dimension:TextureViewDimension.Texture2D),PipelineSync.ComputeShading);
            }
            GraphPassId initialize = scene.Add(ref edit,shader,"InitializeAllocationInputs",new(4,1,1),resources);
            GraphPassId allocate = scene.Add(ref edit,shader,"AllocateVirtualShadowPages",new(4,1,1),resources,initialize);
            GraphPassId observe = scene.Add(ref edit,shader,"ObservePageMappings",new(4,1,1),resources,allocate);
            observed = scene.Read<PageMapping>(ref edit,output,entries,observe);
            pageData = scene.Read<VirtualShadowPageMetadata>(ref edit,pageMetadata,capacity,observe);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        scene.Run();
        PageMapping[] mapped = observed.Values.Take((int)requests).Where(mapping => mapping.Mapped != 0u).ToArray();
        Assert.Equal((int)availableCount,mapped.Length);
        Assert.Equal((int)availableCount,mapped.Select(mapping => mapping.Page).Distinct().Count());
        Assert.All(observed.Values.Skip(512).Take((int)availableCount),mapping => Assert.Equal(0u,mapping.Mapped));
        Assert.All(mapped,mapping =>
        {
            Assert.InRange(mapping.Page,0u,availableCount - 1u);
            Assert.Equal(1u,pageData.Values[mapping.Page].RangeId);
            Assert.Equal(4u,pageData.Values[mapping.Page].RangeGeneration);
            Assert.Equal(10u,pageData.Values[mapping.Page].LastRequestedFrame);
        });
    }

    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct AllocationParameters(uint PageTableEntryCount,uint PageTableEntriesPerView,uint MaxPages,
        uint ViewCount,uint FrameIndex,int DeferredInvalidationBudget,uint HierarchyWordsPerView,uint MipLevelCount);
    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageMapping(uint Mapped,uint Page);
    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageWork(uint Page,uint TableIndex,uint PreviousPage,uint Generation);
    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageMask(uint Dynamic,uint Static);
    [StructLayout(LayoutKind.Sequential,Pack = 4)]
    private readonly record struct PageRectangle(uint Left,uint Top,uint Right,uint Bottom);
}
