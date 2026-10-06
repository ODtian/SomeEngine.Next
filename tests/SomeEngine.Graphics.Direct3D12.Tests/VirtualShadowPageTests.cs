using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;
using System.Numerics;
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
    public void CoarseRequestsUseDirectionalCentersAndOneLocalFallback(uint lightKind, int originX, int originY)
    {
        const int pageTableEntryCount = 22000;
        uint[] view = new uint[52];
        view[16] = 16384;
        view[17] = 128;
        view[21] = unchecked((uint)originX);
        view[25] = unchecked((uint)originY);
        view[33] = 1;
        view[34] = lightKind == 0 ? 1u : 5u;
        view[36] = lightKind == 0 ? 1u : 0u;
        view[37] = lightKind;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
        [
            new BufferInput("CoarsePageDemand", [1, pageTableEntryCount, 7, 0], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", view, ResourceBindingType.BufferSrv, 208),
            new BufferInput("RequestedShadowPages", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageFlags", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageIndices", new uint[pageTableEntryCount], ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageCount", new uint[1], ResourceBindingType.BufferUav, 4),
            new BufferInput("PageReceiverMasks", new uint[pageTableEntryCount * 2], ResourceBindingType.BufferUav, 8),
        ]);
        int expectedRequests = lightKind == 0 ? 4 : 1;
        Assert.Equal(expectedRequests, result[0].Count(value => value == 7));
        Assert.Equal((uint)expectedRequests, result[3][0]);
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
        var table = new uint[1024 * 4];
        var owners = Enumerable.Repeat(uint.MaxValue, capacity).ToArray();
        int next = freeCount;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        for (int index = 0; index < counts[bucket]; index++)
        {
            owners[next] = (uint)(512 + next);
            table[(512 + next) * 4] = (uint)next + 1;
            next++;
        }
        var requested = new uint[1024];
        Array.Fill(requested, 10u, 0, requests);
        uint[] views = new uint[52];
        views[16] = 4096; views[17] = 128; views[21] = unchecked((uint)-3);
        views[24] = 5; views[25] = 5; views[34] = 1;
        var pageLists = new uint[4 * (capacity + 1)];
        int availableStart = capacity + 1;
        for (int index = 0; index < available - freeCount; index++)
            pageLists[availableStart + index] = (uint)(freeCount + index);
        for (int index = 0; index < freeCount; index++)
            pageLists[availableStart + available - freeCount + index] = (uint)index;
        pageLists[2 * capacity + 1] = (uint)available;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(16, 1, 1),
        [
            ([1024, 1024, capacity, 1, 10, 0, 63, 1], ResourceBindingType.ConstantBuffer, 0),
            (views, ResourceBindingType.BufferSrv, 208),
            (Enumerable.Range(0, requests).Select(index => (uint)index).ToArray(), ResourceBindingType.BufferSrv, 4),
            ([requests], ResourceBindingType.BufferSrv, 4),
            (Enumerable.Repeat(1u, 1024).ToArray(), ResourceBindingType.BufferSrv, 4),
            (table, ResourceBindingType.BufferUav, 16), (owners, ResourceBindingType.BufferUav, 4),
            (new uint[capacity * 4], ResourceBindingType.BufferUav, 16),
            (pageLists, ResourceBindingType.BufferUav, 4),
            (new uint[capacity * 4], ResourceBindingType.BufferUav, 16), ([0], ResourceBindingType.BufferUav, 4),
            ([uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
            (new uint[63], ResourceBindingType.BufferUav, 4),
            ([0, 0], ResourceBindingType.BufferUav, 4),
            (new uint[2048], ResourceBindingType.BufferUav, 8),
        ]);
        Assert.Equal(0u, result[3][2 * capacity + 1]);
        Assert.Equal((uint)available, result[5][0]);
        Assert.Equal(new uint[] { (uint)(requests - available), 0 }, result[8]);
        // Allocation makes pages drawable. Completed depth supplies the generation
        // that allows these same mappings to be sampled and cached.
        uint[][] completed = new D3D12TestCompute(shaderCompiler).Execute(
            "vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth",
            new((uint)(available + 63) / 64, 1, 1),
            [
                new BufferInput("ActivePhysicalPages", result[4], ResourceBindingType.BufferSrv, 16),
                new BufferInput("CompletePageCount", result[5], ResourceBindingType.BufferSrv, 4),
                new BufferInput("CompletedShadowPageTable", result[0], ResourceBindingType.BufferUav, 16),
                new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 0),
                new BufferInput("CompletedPageMetadata", result[2], ResourceBindingType.BufferUav, 16),
            ]);
        result[0] = completed[0];
        result[2] = completed[1];
        var allocated = new HashSet<uint>();
        var activeTables = new HashSet<uint>();
        for (int index = 0; index < available; index++)
        {
            uint page = result[4][index * 4], entry = result[4][index * 4 + 1];
            Assert.True(allocated.Add(page));
            Assert.True(activeTables.Add(entry));
            Assert.InRange(page, 0u, (uint)available - 1u);
            Assert.InRange(entry, 0u, requests - 1u);
            Assert.Equal(page + 1, result[0][entry * 4]);
            Assert.Equal(5u, result[0][entry * 4 + 3]);
            Assert.Equal(entry, result[1][page]);
            Assert.Equal(10u, result[2][page * 4]);
        }
        for (int page = freeCount; page < available; page++)
            Assert.Equal(0u, result[0][(512 + page) * 4]);
    }

    [Fact]
    public void Dirty_casters_invalidate_both_transforms_and_dilated_coarse_pages_with_overlapping_groups()
    {
        const int dimension = 16, pages = 320;
        uint[] views = new uint[52];
        Matrix4x4 matrix = Matrix4x4.Identity;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(views.AsSpan()), in matrix);
        views[16] = 2048; views[17] = 128; views[21] = unchecked((uint)-3);
        views[24] = 9; views[25] = 5; views[34] = 2; views[37] = 1;
        var table = new uint[pages * 4];
        for (int level = 0; level < 2; level++)
        for (int y = 0; y < (dimension >> level); y++)
        for (int x = 0; x < (dimension >> level); x++)
        {
            int wx = (level == 0 ? -3 : -2) + x, wy = (level == 0 ? 5 : 2) + y;
            int size = dimension >> level;
            int entry = level * 256 + (wy & (size - 1)) * size + (wx & (size - 1));
            table[entry * 4] = (uint)entry + 1;
            table[entry * 4 + 1] = unchecked((uint)wx);
            table[entry * 4 + 2] = unchecked((uint)wy);
            table[entry * 4 + 3] = 9;
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
            ([2, 1, pages, 1, 10, 46, 2, 0], ResourceBindingType.ConstantBuffer, 0),
            (views, ResourceBindingType.BufferSrv, 208), (bounds, ResourceBindingType.BufferSrv, 16),
            ([0, 2], ResourceBindingType.BufferSrv, 4),
            (new uint[pages], ResourceBindingType.BufferSrv, 4),
            (new uint[pages], ResourceBindingType.BufferSrv, 4),
            (new uint[pages * 4], ResourceBindingType.BufferSrv, 16),
            (Enumerable.Repeat(uint.MaxValue, 46).ToArray(), ResourceBindingType.BufferSrv, 4),
            ([0, 0, 15, 15, 0, 0, 7, 7], ResourceBindingType.BufferSrv, 16),
            (table, ResourceBindingType.BufferUav, 16),
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
            Assert.Equal(expected.Contains(entry) ? 0u : 9u, result[0][entry * 4 + 3]);
            Assert.Equal(table[entry * 4], result[0][entry * 4]);
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
        uint[] node = new uint[16];
        node[0] = node[1] = node[2] = Float(-1);
        node[4] = node[5] = node[6] = Float(1);
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("cluster_scene_bounds.slang", "BuildClusterSceneBounds", new(3, 1, 1),
        [
            ([(uint)count, history ? 1u : 0u, instanceGroups ? 1u : 0u, 0], ResourceBindingType.ConstantBuffer, 0),
            (node, ResourceBindingType.BufferSrv, 64), (data, ResourceBindingType.BufferSrv, 0),
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
