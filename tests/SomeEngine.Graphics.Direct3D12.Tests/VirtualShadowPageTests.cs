using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowPageTests
{
    [Theory]
    [InlineData(0, -17, 5)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 0)]
    public void CoarsePagesCoverEveryFinePageIncludingScrolledBorders(uint lightKind, int originX, int originY)
    {
        uint[] view = new uint[42];
        view[16] = 16384;
        view[17] = 128;
        view[21] = unchecked((uint)originX);
        view[25] = unchecked((uint)originY);
        view[34] = 5;
        view[36] = lightKind;
        uint[][] result = Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
        [
            ([1, 64, 7, 0], ResourceBindingType.ConstantBuffer, 0),
            (view, ResourceBindingType.BufferSrv, 168),
            (new uint[64], ResourceBindingType.BufferUav, 4),
        ]);
        var requiredPages = new HashSet<int>();
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            int coarseX = (int)Math.Floor((originX + x) / 32.0);
            int coarseY = (int)Math.Floor((originY + y) / 32.0);
            int tableIndex = (coarseY & 7) * 8 + (coarseX & 7);
            requiredPages.Add(tableIndex);
            Assert.Equal(7u, result[0][tableIndex]);
        }
        Assert.Equal(requiredPages.Count, result[0].Count(value => value != 0));
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
        var eviction = new uint[capacity * 8];
        int next = freeCount;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        for (int index = 0; index < counts[bucket]; index++)
        {
            eviction[bucket * capacity + index] = (uint)next;
            owners[next] = (uint)(512 + next);
            table[(512 + next) * 4] = (uint)next + 1;
            next++;
        }
        var requested = new uint[1024];
        Array.Fill(requested, 10u, 0, requests);
        uint[] views = new uint[42];
        views[16] = 4096; views[17] = 128; views[21] = unchecked((uint)-3);
        views[24] = 5; views[25] = 5;
        uint[][] result = Execute("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(16, 1, 1),
        [
            ([1024, capacity, 1024, 1024, 64, 1, 10, 8], ResourceBindingType.ConstantBuffer, 0),
            (views, ResourceBindingType.BufferSrv, 168), (requested, ResourceBindingType.BufferSrv, 4),
            (table, ResourceBindingType.BufferUav, 16), (owners, ResourceBindingType.BufferUav, 4),
            (new uint[capacity * 4], ResourceBindingType.BufferUav, 16),
            (Enumerable.Range(0, capacity).Select(x => (uint)x).ToArray(), ResourceBindingType.BufferUav, 4),
            ([(uint)freeCount], ResourceBindingType.BufferUav, 4),
            (eviction, ResourceBindingType.BufferUav, 4), (counts, ResourceBindingType.BufferUav, 4),
            (new uint[capacity * 2], ResourceBindingType.BufferUav, 8), ([0], ResourceBindingType.BufferUav, 4),
            ([0, 0], ResourceBindingType.BufferUav, 4),
        ]);
        Assert.Equal(0u, result[4][0]);
        Assert.All(result[6], count => Assert.Equal(0u, count));
        Assert.Equal((uint)available, result[8][0]);
        Assert.Equal(new uint[] { 0, (uint)(requests - available) }, result[9]);
        var allocated = new HashSet<uint>();
        var activeTables = new HashSet<uint>();
        for (int index = 0; index < available; index++)
        {
            uint page = result[7][index * 2], entry = result[7][index * 2 + 1];
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
        const int dimension = 16, coarseDimension = 16, pages = 512;
        uint[] views = new uint[42];
        Matrix4x4 matrix = Matrix4x4.Identity;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(views.AsSpan()), in matrix);
        views[16] = 2048; views[17] = 128; views[21] = unchecked((uint)-3);
        views[24] = 9; views[25] = 5; views[34] = 1; views[35] = 256;
        var table = new uint[pages * 4];
        for (int level = 0; level < 2; level++)
        for (int y = 0; y < dimension; y++)
        for (int x = 0; x < dimension; x++)
        {
            int wx = (level == 0 ? -3 : -2) + x, wy = (level == 0 ? 5 : 2) + y;
            int entry = level * 256 + (wy & 15) * 16 + (wx & 15);
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
        uint[][] result = Execute("vsm_caster_invalidation.slang", "InvalidateMovedVirtualShadowCasters", new(2, 1, 1),
        [
            ([2, 1, pages, 1], ResourceBindingType.ConstantBuffer, 0),
            (views, ResourceBindingType.BufferSrv, 168), (bounds, ResourceBindingType.BufferSrv, 16),
            (table, ResourceBindingType.BufferUav, 16),
            ([0, 2], ResourceBindingType.BufferSrv, 4),
        ]);
        HashSet<int> expected = [];
        foreach (float center in new[] { -0.5f, 0.25f })
        {
            int minX = (int)((center - 0.05f) * 8 + 8) - 1, maxX = (int)((center + 0.05f) * 8 + 8) + 1;
            int minY = 6, maxY = 9;
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                expected.Add(((5 + y) & 15) * dimension + ((-3 + x) & 15));
            for (int y = (int)Math.Floor((5 + minY) / 2.0); y <= (5 + maxY) / 2; y++)
            for (int x = (int)Math.Floor((-3 + minX) / 2.0); x <= (int)Math.Floor((-3 + maxX) / 2.0); x++)
                expected.Add(256 + (y & 15) * coarseDimension + (x & 15));
        }
        for (int entry = 0; entry < pages; entry++)
        {
            Assert.Equal(expected.Contains(entry) ? 0u : 9u, result[0][entry * 4 + 3]);
            Assert.Equal(table[entry * 4], result[0][entry * 4]);
        }
    }

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Spatial_chunks_cover_reordered_instances_and_keep_the_last_rendered_bounds(bool history)
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
        uint[][] result = Execute("cluster_scene_bounds.slang", "BuildClusterSceneBounds", new(3, 1, 1),
        [
            ([(uint)count, history ? 1u : 0u, 0, 0], ResourceBindingType.ConstantBuffer, 0),
            (node, ResourceBindingType.BufferSrv, 64), (data, ResourceBindingType.BufferSrv, 0),
            ([0x80000000, 0x80000000 | count * 4, 0, 0x80000000 | (uint)currentOffset,
                0x80000000 | (uint)previousOffset], ResourceBindingType.ConstantBuffer, 0),
            (order, ResourceBindingType.BufferSrv, 4), (new uint[3 * 8], ResourceBindingType.BufferUav, 16),
            (bounds, ResourceBindingType.BufferUav, 16),
        ]);
        for (int chunk = 0; chunk < 3; chunk++)
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
