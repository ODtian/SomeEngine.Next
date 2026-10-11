using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;
using System.Numerics;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources.Geometry;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class VirtualShadowPageTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(0, -17, 5)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 0)]
    public void CoarseRequestsUseCameraOriginPagesAndOneLocalFallback(uint lightKind, int originX, int originY)
    {
        const int pageTableEntryCount = 22000;
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 16384, PageSize = 128,
            VirtualPageOriginX = originX, VirtualPageOriginY = originY, ClipmapLevelCount = 1,
            MipLevelCount = lightKind == 0 ? 1u : 5u, Flags = VirtualShadowViewFlags.CoarseClipLevel,
            LightKind = (VirtualShadowLightKind)lightKind,
        }];
        uint[] view = MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray();
        var compute = new D3D12TestCompute(shaderCompiler);
        uint[][] result = compute.ExecuteImages("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
        [
            new BufferInput("CoarsePageDemand", [1, pageTableEntryCount, 7, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedShadowPages", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageFlags", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageIndices", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageCount", new uint[1], ResourceBindingType.BufferUav, 4),
        ], [ShadowTestImages.ReceiverMask(256, 384)]);
        uint[][] coverage = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Requests;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(8,8,1)]
            void ReadCoarseRequests(uint3 pixel : SV_DispatchThreadID)
            {
                bool directional = Views[0].LightKind == VSM_LIGHT_DIRECTIONAL;
                uint mip = directional ? 0u : 4u;
                uint2 page = pixel.xy + (directional ? 60u : 0u);
                Coverage[pixel.y * 8u + pixel.x] =
                    Requests[VirtualShadowPageTableIndex(Views[0], mip, page)] == 7u ? 1u : 0u;
            }
            """, "ReadCoarseRequests", new(1, 1, 1),
            [(view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (result[0], ResourceBindingType.BufferSrv, 4),
             (new uint[64], ResourceBindingType.BufferUav, 4)]);
        for (int index = 0; index < 64; index++)
        {
            bool requested = lightKind == 0
                ? index % 8 is 3 or 4 && index / 8 is 3 or 4
                : index == 0;
            Assert.Equal(requested ? 1u : 0u, coverage[0][index]);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(63)]
    public void ExhaustedPagePoolsKeepAvailableShadowsAndRemoveRecycledShadows(int freeCount)
    {
        const int capacity = 256, requests = 255;
        int recycled = 71;
        int available = freeCount + recycled;
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 256, PageSize = 8,
            AtlasSize = 128, PhysicalPagesPerRow = 16, MaxPhysicalPages = capacity,
            CacheGeneration = 5, MipLevelCount = 1,
        };
        uint[] views = MemoryMarshal.Cast<VirtualShadowView, uint>(new[] { view }).ToArray();
        var table = new uint[1024];
        var owners = Enumerable.Repeat(uint.MaxValue, capacity).ToArray();
        for (int page = freeCount; page < available; page++)
        {
            owners[page] = (uint)(512 + page);
            table[512 + page] = VirtualShadowPageEntry.Encode((uint)page, 16);
        }
        var pageLists = new uint[4 * (capacity + 1)];
        int availableStart = capacity + 1;
        for (int index = 0; index < recycled; index++)
            pageLists[availableStart + index] = (uint)(freeCount + index);
        for (int index = 0; index < freeCount; index++)
            pageLists[availableStart + recycled + index] = (uint)index;
        pageLists[2 * capacity + 1] = (uint)available;
        var compute = new D3D12TestCompute(shaderCompiler);
        uint[][] result = compute.ExecuteImages("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(4, 1, 1),
        [
            new BufferInput("PageAllocation", [1024, 1024, capacity, 1, 10, 0, 63, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedPageIndices", Enumerable.Range(0, requests).Select(index => (uint)index).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", [requests], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", Enumerable.Repeat(1u, 1024).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
            new BufferInput("PhysicalPageOwners", owners, ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", new uint[capacity * 8], ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
            new BufferInput("ShadowPageLists", pageLists, ResourceBindingType.BufferUav, 4),
            new BufferInput("ActivePhysicalPages", new uint[capacity * 4], ResourceBindingType.BufferUav, 16),
            new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("RasterTargetPageHierarchy", new uint[126], ResourceBindingType.BufferUav, 8),
            new BufferInput("ShadowAllocationFailures", new uint[3], ResourceBindingType.BufferUav, 4),
        ], ShadowTestImages.ReceiverHierarchy(64, 96));
        uint[][] completed = compute.Execute("vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth", new(4, 1, 1),
            [new BufferInput("ActivePhysicalPages", result[4], ResourceBindingType.BufferSrv, 16),
             new BufferInput("CompletePageCount", result[5], ResourceBindingType.BufferSrv, 4),
             new BufferInput("CompletedShadowPageTable", result[0], ResourceBindingType.BufferUav, 4),
             new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 0),
             new BufferInput("CompletedPageMetadata", result[2], ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes)]);
        uint[][] rendered = compute.ExecuteSourceImages("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> ActivePages;
            StructuredBuffer<uint> PageCount;
            RWTexture2DArray<uint> Atlas;
            [shader("compute")]
            [numthreads(8,8,1)]
            void DrawDistinctShadows(uint3 group : SV_GroupID, uint3 pixel : SV_GroupThreadID)
            {
                if (group.x >= PageCount[0]) return;
                uint4 page = ActivePages[group.x];
                uint2 atlasPage = uint2(page.x % Views[0].PhysicalPagesPerRow,
                    page.x / Views[0].PhysicalPagesPerRow);
                uint2 address = atlasPage * Views[0].PageSize + pixel.xy;
                Atlas[uint3(address,0u)] = asuint(float(page.y + 1u) / 256.0);
            }
            """, "DrawDistinctShadows", new(capacity, 1, 1),
            [new BufferInput("Views", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("ActivePages", result[4], ResourceBindingType.BufferSrv, 16),
             new BufferInput("PageCount", result[5], ResourceBindingType.BufferSrv, 4)],
            [new(128, 128, new uint[16384], 0, ViewDimension: TextureViewDimension.Texture2DArray,
                Type: ResourceBindingType.TextureUav, Name: "Atlas")]);
        uint[][] sampled = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<float2> Shadows;
            [shader("compute")]
            [numthreads(64,1,1)]
            void ReadPoolShadows(uint index : SV_DispatchThreadID)
            {
                uint2 pixel = uint2(index % 32u, index / 32u) * 8u + 4u;
                float depth;
                bool valid = TryReadVirtualShadowMipDepth(Views[0], Pages, Atlas, 0u, pixel, depth);
                Shadows[index] = float2(valid ? 1.0 : 0.0, valid ? depth : 0.0);
            }
            """, "ReadPoolShadows", new(16, 1, 1),
            [(views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (completed[0], ResourceBindingType.BufferSrv, 4),
             (new uint[2048], ResourceBindingType.BufferUav, 8)],
            new SampledImage(128, 128, rendered[0], 2,
                ViewDimension: TextureViewDimension.Texture2DArray));
        float[] shadows = sampled[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.Equal(available, Enumerable.Range(0, requests).Count(index => shadows[index * 2] == 1f));
        for (int index = 0; index < requests; index++)
            Assert.Equal(shadows[index * 2] == 1f ? 1f - (index + 1f) / 256f : 0f, shadows[index * 2 + 1]);
        for (int page = freeCount; page < available; page++)
            Assert.Equal(0f, shadows[(512 + page) * 2]);
    }

    [Fact]
    public void Dirty_casters_invalidate_both_transforms_and_dilated_coarse_pages_with_overlapping_groups()
    {
        const int dimension = 16, pages = 320;
        VirtualShadowView[] viewData = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 8,
            AtlasSize = 256, PhysicalPagesPerRow = 32, MaxPhysicalPages = pages,
            VirtualPageOriginX = -3, VirtualPageOriginY = 5, CacheGeneration = 9,
            MipLevelCount = 2, LightKind = VirtualShadowLightKind.Point,
        }];
        uint[] views = MemoryMarshal.Cast<VirtualShadowView, uint>(viewData).ToArray();
        var table = new uint[pages];
        var metadata = new VirtualShadowPageMetadata[pages];
        for (int level = 0; level < 2; level++)
        for (int y = 0; y < (dimension >> level); y++)
        for (int x = 0; x < (dimension >> level); x++)
        {
            int wx = (level == 0 ? -3 : -2) + x, wy = (level == 0 ? 5 : 2) + y;
            int size = dimension >> level;
            int entry = level * 256 + (wy & (size - 1)) * size + (wx & (size - 1));
            table[entry] = VirtualShadowPageEntry.Encode((uint)entry, 32u);
            metadata[entry] = new()
            {
                StaticDepthGeneration = 9, WorldPageX = wx, WorldPageY = wy, Generation = 9,
            };
        }
        // Three casters share one root. Only IDs 0 and 2 are dirty; ID 1 must leave its region intact.
        uint[] bounds = new uint[3 * 16];
        for (int id = 0; id < 3; id++)
        for (int previous = 0; previous < 2; previous++)
        {
            int offset = id * 16 + previous * 8;
            float center = id == 1 ? 0.9f : previous == 0 ? -0.5f : 0.25f;
            bounds[offset] = Float(center - 0.05f); bounds[offset + 4] = Float(center + 0.05f);
            bounds[offset + 1] = bounds[offset + 2] = Float(-0.05f);
            bounds[offset + 5] = bounds[offset + 6] = Float(0.05f);
        }
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_caster_invalidation.slang", "InvalidateMovedVirtualShadowCasters", new(2, 1, 1),
        [
            new BufferInput("Invalidation", [2, 1, pages, 1, 10, 46, 2, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("InstanceWorldBounds", bounds, ResourceBindingType.BufferSrv, 16),
            new BufferInput("ChangedInstances", [0, 2], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RasterTargetDepthCacheClasses", [7], ResourceBindingType.BufferSrv, 4),
            new BufferInput("PreviousDepthCacheClasses", [7], ResourceBindingType.BufferSrv, 4),
            new BufferInput("LodWorldBounds", new uint[8], ResourceBindingType.BufferSrv, 16),
            new BufferInput("RequestedShadowPages", new uint[pages], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", new uint[pages], ResourceBindingType.BufferSrv, 4),
            new BufferInput("PageMetadata", MemoryMarshal.Cast<VirtualShadowPageMetadata, uint>(metadata).ToArray(), ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
            new BufferInput("AllocatedPageHierarchy", Enumerable.Repeat(uint.MaxValue, 46).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("AllocatedPageRectBounds", [0, 0, 15, 15, 0, 0, 7, 7], ResourceBindingType.BufferSrv, 16),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
        ]);
        HashSet<int> expected = [];
        foreach (float center in new[] { -0.5f, 0.25f })
        for (int level = 0; level < 2; level++)
        {
            int size = dimension >> level;
            int minX = (int)((center - 0.05f) * size / 2 + size / 2) - 1;
            int maxX = (int)((center + 0.05f) * size / 2 + size / 2) + 1;
            int minY = (int)(.475f * size) - 1, maxY = (int)(.525f * size) + 1;
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                expected.Add(level * 256 + (((5 >> level) + y) & (size - 1)) * size + (((-3 >> level) + x) & (size - 1)));
        }
        uint[][] sampled = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(64,1,1)]
            void ReadInvalidatedShadows(uint index : SV_DispatchThreadID)
            {
                if (index >= 320u) return;
                uint mip; uint2 page;
                VirtualShadowDecodePageTableIndex(Views[0], index, mip, page);
                float depth;
                Coverage[index] = TryReadVirtualShadowMipDepth(Views[0], Pages, Atlas, mip,
                    page * 8u + 4u, depth) ? 1u : 0u;
            }
            """, "ReadInvalidatedShadows", new(5, 1, 1),
            [(views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (result[1], ResourceBindingType.BufferSrv, 4),
             (new uint[pages], ResourceBindingType.BufferUav, 4)],
            new SampledImage(256, 256, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.75f), 256 * 256).ToArray(), 2,
                ViewDimension: TextureViewDimension.Texture2DArray));
        for (int entry = 0; entry < pages; entry++)
            Assert.Equal(expected.Contains(entry) ? 0u : 1u, sampled[0][entry]);
    }

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Instance_bounds_preserve_current_and_previous_transforms(bool history, bool instanceGroups)
    {
        const int count = 129;
        int currentOffset = count * 8, previousOffset = currentOffset + count * 48;
        uint[] data = new uint[(previousOffset + count * 48) / 4];
        uint[] order = Enumerable.Range(0, count).Reverse().Select(x => (uint)x).ToArray();
        uint[] bounds = new uint[count * 16];
        for (int id = 0; id < count; id++)
        {
            data[id] = Float(0.25f);
            for (int previous = 0; previous < 2; previous++)
            {
                int start = (previous == 0 ? currentOffset : previousOffset) / 4 + id * 12;
                data[start + 3] = data[start + 7] = Float(1);
                data[start + 8] = data[start + 9] = data[start + 10] = Float(1);
                data[start + 4] = Float(id + (previous == 0 ? 0 : -10));
            }
            for (int axis = 0; axis < 3; axis++)
            { bounds[id * 16 + axis] = Float(-1000 - id); bounds[id * 16 + 4 + axis] = Float(1000 + id); }
        }
        ClusterBVHNode[] nodes = [new()];
        nodes[0].SetChild(0, new ClusterHierarchySlice
        {
            BoundsCenter = Vector3.Zero,
            BoundsExtent = Vector3.One,
            PageRangeKey = 1,
        });
        uint[] node = MemoryMarshal.Cast<ClusterBVHNode, uint>(nodes).ToArray();
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("cluster_scene_bounds.slang", "BuildClusterSceneBounds", new(3, 1, 1),
        [
            ([(uint)count, history ? 1u : 0u, instanceGroups ? 1u : 0u, 0], ResourceBindingType.ConstantBuffer, 0),
            (node, ResourceBindingType.BufferSrv, 0), (data, ResourceBindingType.BufferSrv, 0),
            ([0x80000000, 0x80000000 | count * 4, 0, 0x80000000 | (uint)currentOffset,
                0x80000000 | (uint)previousOffset], ResourceBindingType.ConstantBuffer, 0),
            (order, ResourceBindingType.BufferSrv, 4), (new uint[3 * 8], ResourceBindingType.BufferUav, 16),
            (bounds, ResourceBindingType.BufferUav, 16),
        ]);
        for (int chunk = 0; instanceGroups && chunk < 3; chunk++)
        {
            int first = chunk * 64, last = Math.Min(count, first + 64) - 1;
            Assert.Equal(Float(order[last] - 1.25f), result[0][chunk * 8]);
            Assert.Equal(Float(order[first] + 1.25f), result[0][chunk * 8 + 4]);
        }
        for (int id = 0; id < count; id++)
        {
            Assert.Equal(Float(id - 1.25f), result[1][id * 16]);
            Assert.Equal(Float(id + 1.25f), result[1][id * 16 + 4]);
            Assert.Equal(Float(history ? -1000 - id : id - 11.25f), result[1][id * 16 + 8]);
            Assert.Equal(Float(history ? 1000 + id : id - 8.75f), result[1][id * 16 + 12]);
        }
    }

}
