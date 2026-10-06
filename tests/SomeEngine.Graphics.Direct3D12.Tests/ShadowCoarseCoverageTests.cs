using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class ShadowCoarseCoverageTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void DirectionalCoarseShadowsCoverTheCameraOriginWithinItsSnappingCell()
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.CreateScale(1f / 256f, 1f / 256f, 1f),
            VirtualResolution = 1024, PageSize = 128, AtlasSize = 256, PhysicalPagesPerRow = 2,
            MaxPhysicalPages = 4, VirtualPageOriginX = -4, VirtualPageOriginY = -4,
            CacheGeneration = 7, ClipmapLevel = 6, FirstClipmapLevel = 6, ClipmapLevelCount = 1,
            MipLevelCount = 1, WorldUnitsPerCentimeter = 1,
            ClipmapWorldOrigin = new(51.2f, 51.2f, .5f), Flags = VirtualShadowViewFlags.CoarseClipLevel,
        }];
        uint[] viewData = MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray();
        var compute = new D3D12TestCompute(compiler);
        uint[][] requests = compute.Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
        [
            new BufferInput("CoarsePageDemand", [1, 64, 7, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", viewData, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedShadowPages", new uint[64], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageFlags", new uint[64], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageIndices", new uint[64], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", new uint[128], ResourceBindingType.BufferUav, 8),
        ]);
        uint[] pageLists = new uint[20];
        for (int page = 0; page < 4; page++) pageLists[5 + page] = (uint)page;
        pageLists[9] = 4;
        uint[][] allocated = compute.Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(1, 1, 1),
        [
            new BufferInput("PageAllocation", [64, 64, 4, 1, 7, 0, 15, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", viewData, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedPageIndices", requests[2], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", requests[3], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", requests[1], ResourceBindingType.BufferSrv, 4),
            new BufferInput("ShadowPageTable", new uint[256], ResourceBindingType.BufferUav, 16),
            new BufferInput("PhysicalPageOwners", Enumerable.Repeat(uint.MaxValue, 4).ToArray(), ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", new uint[16], ResourceBindingType.BufferUav, 16),
            new BufferInput("ShadowPageLists", pageLists, ResourceBindingType.BufferUav, 4),
            new BufferInput("ActivePhysicalPages", new uint[16], ResourceBindingType.BufferUav, 16),
            new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("RasterTargetPageHierarchy", new uint[15], ResourceBindingType.BufferUav, 4),
            new BufferInput("ShadowAllocationFailures", new uint[2], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", requests[4], ResourceBindingType.BufferUav, 8),
        ]);
        uint[][] completed = compute.Execute("vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth", new(1, 1, 1),
        [
            new BufferInput("ActivePhysicalPages", allocated[4], ResourceBindingType.BufferSrv, 16),
            new BufferInput("CompletePageCount", allocated[5], ResourceBindingType.BufferSrv, 4),
            new BufferInput("CompletedShadowPageTable", allocated[0], ResourceBindingType.BufferUav, 16),
            new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 0),
            new BufferInput("CompletedPageMetadata", allocated[2], ResourceBindingType.BufferUav, 16),
        ]);
        // Native first6 has radius128cm. Camera51.2cm stays in its snapped center0.
        // Its NDC(.2,.2) becomes page(4.8,3.2): surrounding pages are x4/5,y2/3.
        Vector3[] receivers = [new(25.6f, 25.6f, .5f), new(89.6f, 89.6f, .5f),
            new(25.6f, 89.6f, .5f), new(89.6f, 25.6f, .5f)];
        uint[][] sampled = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            StructuredBuffer<float3> Receivers;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(4, 1, 1)]
            void ReadCoarseShadow(uint3 thread : SV_DispatchThreadID)
            {
                float3 receiver = Receivers[thread.x];
                Visibility[thread.x] = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas,
                    0u, 1u, 0.0, receiver, receiver + float3(.1,0,0), receiver + float3(0,.1,0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadCoarseShadow", new(1, 1, 1),
            [(viewData, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (completed[0], ResourceBindingType.BufferSrv, 16),
             (MemoryMarshal.Cast<Vector3, uint>(receivers.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 12),
             (new uint[4], ResourceBindingType.BufferUav, 4)],
            new SampledImage(256, 256, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.75f), 256 * 256).ToArray(), 2));
        Assert.Equal(new float[] { 0, 0, 0, 0 }, sampled[0].Select(BitConverter.UInt32BitsToSingle).ToArray());
    }
}
