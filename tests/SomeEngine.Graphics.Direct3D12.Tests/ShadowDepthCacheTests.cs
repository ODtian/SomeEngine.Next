using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowDepthCacheTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void MaskedDemandRefreshesDynamicDepthAndKeepsStaticDepth()
    {
        uint[] view = new uint[52];
        view[16] = 512; view[17] = 128; view[18] = 128; view[19] = 1;
        view[20] = 1; view[24] = 7; view[34] = 1; view[36] = 12; view[37] = 1;
        uint[] table = new uint[64];
        table[0] = 1; table[3] = 7;
        uint[] masks = new uint[32]; masks[0] = 1;
        uint[][] result = new D3D12TestCompute(compiler).Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(1, 1, 1),
        [
            new BufferInput("PageAllocation", [16, 16, 1, 1, 3, 0, 7, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", view, ResourceBindingType.BufferSrv, 208),
            new BufferInput("RequestedPageIndices", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", [1], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", Enumerable.Repeat(1u, 16).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 16),
            new BufferInput("PhysicalPageOwners", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", [2, 7, 1, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("ShadowPageLists", new uint[8], ResourceBindingType.BufferUav, 4),
            new BufferInput("ActivePhysicalPages", new uint[4], ResourceBindingType.BufferUav, 16),
            new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("RasterTargetPageHierarchy", new uint[7], ResourceBindingType.BufferUav, 4),
            new BufferInput("ShadowAllocationFailures", new uint[2], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", masks, ResourceBindingType.BufferUav, 8),
        ]);
        Assert.Equal(1u, result[5][0]);
        Assert.Equal(7u, result[2][1]);
        Assert.Equal(0u, result[0][3]);
    }

    [Fact]
    public void DynamicDepthStartsFromReusableStaticDepth()
    {
        uint[][] result = new D3D12TestCompute(compiler).ExecuteSource("""
            #include "vsm_depth_cache.slang"
            RWStructuredBuffer<uint> Depth;
            [shader("compute")]
            [numthreads(1,1,1)]
            void InitializeDepth()
            {
                Depth[0] = InitializeVirtualShadowDynamicDepth(asuint(.75), VSM_PAGE_DYNAMIC_UNCACHED);
                Depth[1] = InitializeVirtualShadowDynamicDepth(asuint(.75), VSM_PAGE_STATIC_UNCACHED | VSM_PAGE_DYNAMIC_UNCACHED);
            }
            """, "InitializeDepth", new(1, 1, 1), [(new uint[2], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal(.75f, BitConverter.UInt32BitsToSingle(result[0][0]));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(result[0][1]));
    }
}
