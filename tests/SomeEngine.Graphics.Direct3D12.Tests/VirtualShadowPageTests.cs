using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources.Geometry;
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
    public void CoarseRequestsUseDirectionalCentersAndOneLocalFallback(uint lightKind, int originX, int originY)
    {
        const int pageTableEntryCount = 22000;
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.Identity,
            VirtualResolution = 16384, PageSize = 128,
            VirtualPageOriginX = originX, VirtualPageOriginY = originY,
            ClipmapLevelCount = 1, MipLevelCount = 8,
            Flags = VirtualShadowViewFlags.CoarseClipLevel,
            LightKind = (VirtualShadowLightKind)lightKind,
        };
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
        [
            new BufferInput("CoarsePageDemand", [1, pageTableEntryCount, 7, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", MemoryMarshal.Cast<VirtualShadowView, uint>(new[] { view }.AsSpan()).ToArray(),
                ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedPageFlags", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageIndices", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageCount", new uint[1], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", null, ResourceBindingType.TextureUav, 0),
        ]);
        int expectedRequests = lightKind == 0 ? 4 : 1;
        Assert.Equal(expectedRequests, result[0].Count(value => value == 10));
        Assert.Equal((uint)expectedRequests, result[2][0]);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(63)]
    public void Wave_allocation_exhausts_partial_pools_without_duplicates_or_counter_underflow(int freeCount)
    {
        const int capacity = 256, requests = 255;
        uint[] counts = [3, 0, 13, 17, 0, 5, 31, 2];
        int available = freeCount + (int)counts.Sum(x => (long)x);
        var table = new uint[1024];
        var owners = Enumerable.Repeat(uint.MaxValue, capacity).ToArray();
        for (int page = freeCount; page < available; page++)
        {
            owners[page] = (uint)(512 + page);
            table[512 + page] = VirtualShadowPageEntry.Encode((uint)page, 16);
        }
        VirtualShadowView view = new()
        {
            VirtualResolution = 4096, PageSize = 128, PhysicalPagesPerRow = 16,
            MaxPhysicalPages = capacity, VirtualPageOriginX = -3, VirtualPageOriginY = 5,
            CacheGeneration = 5, MipLevelCount = 1,
        };
        uint[] lists = new uint[4 * (capacity + 1)];
        for (int page = 0; page < available; page++) lists[capacity + 1 + page] = (uint)page;
        lists[2 * (capacity + 1) - 1] = (uint)available;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(16, 1, 1),
        [
            new BufferInput("PageAllocation", [1024, 1024, capacity, 1, 10, 8, 63, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", MemoryMarshal.Cast<VirtualShadowView, uint>(new[] { view }.AsSpan()).ToArray(),
                ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("RequestedPageIndices", Enumerable.Range(0, requests).Select(index => (uint)index).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", [requests], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageFlags", Enumerable.Repeat(1u, 1024).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
            new BufferInput("PhysicalPageOwners", owners, ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", new uint[capacity * 8], ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
            new BufferInput("ShadowPageLists", lists, ResourceBindingType.BufferUav, 4),
            new BufferInput("ActivePhysicalPages", new uint[capacity * 4], ResourceBindingType.BufferUav, 16),
            new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
            new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            new BufferInput("RasterTargetPageHierarchy", new uint[63 * 2], ResourceBindingType.BufferUav, 8),
            new BufferInput("ShadowAllocationFailures", [0, 0, 0], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMaskMip1", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip2", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip3", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip4", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip5", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip6", null, ResourceBindingType.TextureUav, 0),
            new BufferInput("PageReceiverMaskMip7", null, ResourceBindingType.TextureUav, 0),
        ], [new WritableImage("PageReceiverMasks", 32, 64)]);
        Assert.Equal(0u, result[3][2 * (capacity + 1) - 1]);
        Assert.Equal((uint)available, result[3][4 * (capacity + 1) - 1]);
        Assert.Equal((uint)available, result[5][0]);
        Assert.Equal(new uint[] { (uint)(requests - available), 0, 0 }, result[8]);
        var allocated = new HashSet<uint>();
        var activeTables = new HashSet<uint>();
        for (int index = 0; index < available; index++)
        {
            uint page = result[4][index * 4], entry = result[4][index * 4 + 1];
            Assert.True(allocated.Add(page));
            Assert.True(activeTables.Add(entry));
            Assert.InRange(page, 0u, (uint)available - 1u);
            Assert.InRange(entry, 0u, requests - 1u);
            Assert.Equal(VirtualShadowPageEntry.Encode(page, 16) & 0x000fffffu, result[0][entry] & 0x000fffffu);
            Assert.NotEqual(0u, result[0][entry] & 0x40000000u);
            Assert.Equal(entry, result[1][page]);
            Assert.Equal(10u, result[2][page * 8]);
            Assert.Equal(5u, result[2][page * 8 + 6]);
        }
        for (int page = freeCount; page < available; page++)
            Assert.Equal(0u, result[0][512 + page]);
    }

    [Fact]
    public void Dirty_casters_invalidate_both_transforms_and_dilated_coarse_pages_with_overlapping_groups()
    {
        const int dimension = 16, pages = 320;
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.Identity,
            VirtualResolution = 2048, PageSize = 128, PhysicalPagesPerRow = 32,
            MaxPhysicalPages = pages, VirtualPageOriginX = -3, VirtualPageOriginY = 5,
            CacheGeneration = 9, MipLevelCount = 2, LightKind = VirtualShadowLightKind.Point,
        };
        var table = new uint[pages];
        var metadata = new VirtualShadowPageMetadata[pages];
        for (int level = 0; level < 2; level++)
        for (int y = 0; y < (dimension >> level); y++)
        for (int x = 0; x < (dimension >> level); x++)
        {
            int wx = (level == 0 ? -3 : -2) + x, wy = (level == 0 ? 5 : 2) + y;
            int size = dimension >> level;
            int entry = level * 256 + (wy & (size - 1)) * size + (wx & (size - 1));
            table[entry] = VirtualShadowPageEntry.Encode((uint)entry, 32);
            metadata[entry] = new() { Generation = 9, WorldPageX = wx, WorldPageY = wy };
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
            new BufferInput("ShadowViews", MemoryMarshal.Cast<VirtualShadowView, uint>(new[] { view }.AsSpan()).ToArray(),
                ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("InstanceWorldBounds", bounds, ResourceBindingType.BufferSrv, 16),
            new BufferInput("ChangedInstances", [0, 2], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RasterTargetDepthCacheClasses", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("PreviousDepthCacheClasses", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("LodWorldBounds", new uint[pages * 4], ResourceBindingType.BufferSrv, 16),
            new BufferInput("RequestedPageFlags", new uint[pages], ResourceBindingType.BufferSrv, 4),
            new BufferInput("AllocatedPageHierarchy", Enumerable.Repeat(uint.MaxValue, 46).ToArray(), ResourceBindingType.BufferSrv, 4),
            new BufferInput("AllocatedPageRectBounds", [0, 0, 15, 15, 0, 0, 7, 7], ResourceBindingType.BufferSrv, 16),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", MemoryMarshal.Cast<VirtualShadowPageMetadata, uint>(metadata.AsSpan()).ToArray(),
                ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
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
        for (int entry = 0; entry < pages; entry++)
        {
            Assert.Equal(!expected.Contains(entry), (result[0][entry] & 0x80000000u) != 0u);
            Assert.Equal(table[entry] & 0x000fffffu, result[0][entry] & 0x000fffffu);
        }
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
        ClusterBVHNode node = new();
        node.SetChild(0, new ClusterHierarchySlice { BoundsExtent = Vector3.One, PageRangeKey = 1 });
        uint[] properties = new uint[12];
        properties[1] = 0x80000000u;
        properties[2] = 0x80000000u | count * 4u;
        properties[3] = count * 4u;
        properties[4] = count * 4u;
        properties[5] = 0x80000000u | (uint)currentOffset;
        properties[6] = 0x80000000u | (uint)previousOffset;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("cluster_scene_bounds.slang", "BuildClusterSceneBounds", new(3, 1, 1),
        [
            new BufferInput("Scene", [(uint)count, history ? 1u : 0u, instanceGroups ? 1u : 0u, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("GlobalBVH", MemoryMarshal.Cast<ClusterBVHNode, uint>(new[] { node }.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 0),
            new BufferInput("InstanceData", data, ResourceBindingType.BufferSrv, 0),
            new BufferInput("InstanceProperties", properties, ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ClusterCurrentBounds", null, ResourceBindingType.BufferSrv, 16),
            new BufferInput("ClusterPreviousBounds", null, ResourceBindingType.BufferSrv, 16),
            new BufferInput("MaterialBounds", [1], ResourceBindingType.BufferSrv, 4),
            new BufferInput("SceneInstances", order, ResourceBindingType.BufferSrv, 4),
            new BufferInput("SceneChunks", new uint[3 * 8], ResourceBindingType.BufferUav, 16),
            new BufferInput("InstanceWorldBounds", bounds, ResourceBindingType.BufferUav, 16),
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
