using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowDepthCacheTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void MaskedDemandRefreshesDynamicDepthAndKeepsStaticDepth()
    {
        var shadowView = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1, CacheGeneration = 7,
            MipLevelCount = 1, Flags = VirtualShadowViewFlags.UseReceiverMask |
                VirtualShadowViewFlags.PreviousProjectionValid, LightKind = VirtualShadowLightKind.Point,
        };
        uint[] view = MemoryMarshal.Cast<byte, uint>(MemoryMarshal.AsBytes(new[] { shadowView }.AsSpan())).ToArray();
        uint[] table = new uint[4];
        table[0] = 1; table[3] = 7;
        uint[] masks = [1, 0];
        var compute = new D3D12TestCompute(compiler);
        uint[][] allocation = compute.Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(1, 1, 1),
        [
            new BufferInput("PageAllocation", [1, 1, 1, 1, 3, 0, 1, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedPageIndices", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", [1], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", [1], ResourceBindingType.BufferSrv, 4),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 16),
            new BufferInput("PhysicalPageOwners", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", [2, 7, 1, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("ShadowPageLists", new uint[8], ResourceBindingType.BufferUav, 4),
            new BufferInput("ActivePhysicalPages", new uint[4], ResourceBindingType.BufferUav, 16),
            new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("RasterTargetPageHierarchy", new uint[1], ResourceBindingType.BufferUav, 4),
            new BufferInput("ShadowAllocationFailures", new uint[2], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", masks, ResourceBindingType.BufferUav, 8),
        ]);
        uint[][] depth = compute.ExecuteImages("vsm_clear_pages.slang", "ClearVirtualShadowPages", new(8, 8, 1),
        [
            new("PageClear", [128, 1, 128, 0], ResourceBindingType.ConstantBuffer, 0),
            new("ActivePhysicalPages", allocation[4], ResourceBindingType.BufferSrv, 16),
            new("ActivePhysicalPageCount", allocation[5], ResourceBindingType.BufferSrv, 4),
            new("ClearPageMetadata", allocation[2], ResourceBindingType.BufferSrv, 16),
        ],
        [
            new(128, 128, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.9f), 16384).ToArray(), 0,
                Type: ResourceBindingType.TextureUav, Name: "ShadowPhysicalAtlas"),
            new(128, 128, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.75f), 16384).ToArray(), 1,
                Type: ResourceBindingType.TextureUav, Name: "ShadowStaticAtlas"),
        ]);
        uint[][] completion = compute.Execute("vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth", new(1, 1, 1),
        [
            new BufferInput("ActivePhysicalPages", allocation[4], ResourceBindingType.BufferSrv, 16),
            new BufferInput("CompletePageCount", allocation[5], ResourceBindingType.BufferSrv, 4),
            new BufferInput("CompletedShadowPageTable", allocation[0], ResourceBindingType.BufferUav, 16),
            new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("CompletedPageMetadata", allocation[2], ResourceBindingType.BufferUav, 16),
        ]);
        uint[][] sampled = compute.ExecuteSource("""
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            StructuredBuffer<uint2> Residents;
            Texture2D<uint> DynamicAtlas;
            Texture2D<uint> StaticAtlas;
            RWStructuredBuffer<float4> Depths;
            [shader("compute")]
            [numthreads(1,1,1)]
            void ReadRefreshedDepth()
            {
                float dynamicDepth, staticDepth;
                uint dynamicMip, staticMip;
                bool dynamicValid = TryReadVirtualShadowResidentDepth(Views[0], Pages, Residents,
                    DynamicAtlas, 0u, float2(.01,.01), dynamicDepth, dynamicMip);
                bool staticValid = TryReadVirtualShadowResidentDepth(Views[0], Pages, Residents,
                    StaticAtlas, 0u, float2(.01,.01), staticDepth, staticMip);
                Depths[0] = float4(dynamicValid ? 1.0 : 0.0, dynamicDepth,
                    staticValid ? 1.0 : 0.0, staticDepth);
            }
            """, "ReadRefreshedDepth", new(1, 1, 1),
            [(allocation[9], ResourceBindingType.BufferSrv, 8),
             (view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (completion[0], ResourceBindingType.BufferSrv, 16),
             (new uint[] { 1, 0 }, ResourceBindingType.BufferSrv, 8),
             (new uint[4], ResourceBindingType.BufferUav, 16)],
            images: [new(128, 128, depth[0], 4), new(128, 128, depth[1], 5)]);
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(sampled[0][0]));
        Assert.InRange(BitConverter.UInt32BitsToSingle(sampled[0][1]), .2499999f, .2500001f);
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(sampled[0][2]));
        Assert.InRange(BitConverter.UInt32BitsToSingle(sampled[0][3]), .2499999f, .2500001f);
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
