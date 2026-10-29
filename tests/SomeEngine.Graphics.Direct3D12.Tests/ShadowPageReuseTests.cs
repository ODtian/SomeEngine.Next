using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowPageReuseTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void RecentlyTouchedShadowsSurvivePressureAndRecycledShadowsRecover()
    {
        var history = new ShadowPages(compiler);
        history.Request(0); // A
        history.Request(1); // B
        history.Request(0); // touch A again
        history.Idle(32); // both ages exceed the old eight-bucket range
        history.Request(2); // C needs one of the two atlas pages
        Assert.Equal(new float[] { 1, 0, 0, 1, 1, 0 }, history.Observe());

        history.Request(1);
        var fresh = new ShadowPages(compiler);
        fresh.Request(1);
        Assert.Equal(fresh.Observe().AsSpan(2, 2).ToArray(), history.Observe().AsSpan(2, 2).ToArray());
    }

    [Fact]
    public void CachedShadowsKeepReuseOrderAcrossPageBatches()
    {
        const uint capacity = 640, tableEntries = 4096, hierarchyWords = 191;
        uint[] positions = [13, 73, 158, 236, 271, 355, 423, 538, 612];
        uint[] cachedPages = [18, 502, 5, 64, 508, 31, 387, 7, 511];
        uint[] emptyPages = [19, 617];
        uint[] lists = Enumerable.Repeat(uint.MaxValue, 4 * ((int)capacity + 1)).ToArray();
        for (int index = 0; index < positions.Length; index++)
            lists[positions[index]] = cachedPages[index];
        lists[capacity] = 0;
        lists[2 * capacity + 1] = 0;
        emptyPages.CopyTo(lists, 2 * ((int)capacity + 1));
        lists[3 * capacity + 2] = (uint)emptyPages.Length;
        lists[4 * capacity + 3] = capacity - (uint)(cachedPages.Length + emptyPages.Length);

        var compute = new D3D12TestCompute(compiler);
        uint[] table = new uint[tableEntries];
        uint[] pages = Enumerable.Repeat(uint.MaxValue, (int)capacity).ToArray();
        uint[] metadata = new uint[capacity * 8];
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.Identity,
            VirtualResolution = 512, PageSize = 8, AtlasSize = 256,
            PhysicalPagesPerRow = 32, MaxPhysicalPages = capacity,
            CacheGeneration = 7, MipLevelCount = 1,
            LightKind = VirtualShadowLightKind.Spot, LightRange = 10,
            WorldUnitsPerCentimeter = 1,
        };
        uint[] views = MemoryMarshal.Cast<VirtualShadowView, uint>(MemoryMarshal.CreateReadOnlySpan(ref view, 1)).ToArray();
        uint[][] compacted = compute.Execute("vsm_page_pool.slang", "BuildVirtualShadowPagePool", new(1, 1, 1),
        [
            new BufferInput("PagePool", [capacity, tableEntries, 1, 2, tableEntries, hierarchyWords, 1, 1], ResourceBindingType.ConstantBuffer, 0),
            new BufferInput("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
            new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
            new BufferInput("RequestedPageFlags", new uint[tableEntries], ResourceBindingType.BufferSrv, 4),
            new BufferInput("RequestedPageCount", [0], ResourceBindingType.BufferSrv, 4),
            new BufferInput("PageRequestDispatchArgs", new uint[4], ResourceBindingType.BufferUav, 0),
            new BufferInput("PhysicalPageOwners", pages, ResourceBindingType.BufferUav, 4),
            new BufferInput("PageMetadata", metadata, ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
            new BufferInput("ShadowPageLists", lists, ResourceBindingType.BufferUav, 4),
            new BufferInput("AllocatedPageHierarchy", new uint[hierarchyWords], ResourceBindingType.BufferUav, 4),
            new BufferInput("AllocatedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
        ]);
        lists = compacted[4];

        uint[] expectedPages = [.. emptyPages.Reverse(), .. cachedPages.Reverse()];
        var activePages = new List<uint>();
        for (uint index = 0; index < expectedPages.Length; index++)
        {
            uint[][] allocated = compute.ExecuteImages("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(1, 1, 1),
            [
                new BufferInput("PageAllocation", [tableEntries, tableEntries, capacity, 1, 1, 0, hierarchyWords, 1], ResourceBindingType.ConstantBuffer, 0),
                new BufferInput("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
                new BufferInput("RequestedPageIndices", [index], ResourceBindingType.BufferSrv, 4),
                new BufferInput("RequestedPageCount", [1], ResourceBindingType.BufferSrv, 4),
                new BufferInput("RequestedPageFlags", new uint[tableEntries], ResourceBindingType.BufferSrv, 4),
                new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
                new BufferInput("PhysicalPageOwners", pages, ResourceBindingType.BufferUav, 4),
                new BufferInput("PageMetadata", metadata, ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
                new BufferInput("ShadowPageLists", lists, ResourceBindingType.BufferUav, 4),
                new BufferInput("ActivePhysicalPages", new uint[capacity * 4], ResourceBindingType.BufferUav, 16),
                new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
                new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
                new BufferInput("RasterTargetPageHierarchy", new uint[hierarchyWords * 2], ResourceBindingType.BufferUav, 8),
                new BufferInput("ShadowAllocationFailures", new uint[3], ResourceBindingType.BufferUav, 4),
            ], ShadowTestImages.ReceiverHierarchy(64, 128));
            table = allocated[0]; pages = allocated[1]; metadata = allocated[2]; lists = allocated[3];
            activePages.AddRange(allocated[4].AsSpan(0, 4).ToArray());
        }
        uint[][] completed = compute.Execute("vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth", new(1, 1, 1),
        [
            new BufferInput("ActivePhysicalPages", activePages.ToArray(), ResourceBindingType.BufferSrv, 16),
            new BufferInput("CompletePageCount", [(uint)expectedPages.Length], ResourceBindingType.BufferSrv, 4),
            new BufferInput("CompletedShadowPageTable", table, ResourceBindingType.BufferUav, 4),
            new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 0),
            new BufferInput("CompletedPageMetadata", metadata, ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes),
        ]);
        Assert.Equal(expectedPages.Select(page => VirtualShadowPageEntry.Encode(page, view.PhysicalPagesPerRow)),
            completed[0].AsSpan(0, expectedPages.Length).ToArray());
    }

    private sealed class ShadowPages(D3D12TestCompiler compiler)
    {
        private readonly D3D12TestCompute _compute = new(compiler);
        private readonly uint[] _view = View();
        private uint[] _table = new uint[4];
        private uint[] _pages = [uint.MaxValue, uint.MaxValue];
        private uint[] _metadata = new uint[16];
        private uint[] _lists = new uint[12];
        private uint _frame;

        internal void Request(uint page) => ProcessFrame(page);

        internal void Idle(uint frames)
        {
            _frame += frames - 1;
            ProcessFrame(null);
        }

        private void ProcessFrame(uint? page)
        {
            _frame++;
            uint requestCount = page.HasValue ? 1u : 0u;
            uint[] requests = new uint[4];
            if (page is uint region) requests[region] = 1u;
            if (_frame == 1u)
            {
                uint[][] initialized = _compute.ExecuteImages("vsm_state_init.slang", "InitializeVirtualShadowState", new(1, 1, 1),
                [
                    new BufferInput("StateInit", [4, 2, 4, 0, 0, 0], ResourceBindingType.ConstantBuffer, 0),
                    new BufferInput("ShadowPageTable", _table, ResourceBindingType.BufferUav, 4),
                    new BufferInput("PhysicalPageOwners", _pages, ResourceBindingType.BufferUav, 4),
                    new BufferInput("PageMetadata", _metadata, ResourceBindingType.BufferUav, 32),
                    new BufferInput("RequestedPageFlags", new uint[4], ResourceBindingType.BufferUav, 4),
                    new BufferInput("RequestedPageCount", [0], ResourceBindingType.BufferUav, 4),
                    new BufferInput("ShadowPageLists", _lists, ResourceBindingType.BufferUav, 4),
                ], ShadowTestImages.ReceiverHierarchy(4, 6));
                _table = initialized[0]; _pages = initialized[1]; _metadata = initialized[2]; _lists = initialized[5];
            }
            BuildPool(0);
            BuildPool(1);
            BuildPool(2);
            uint[][] allocated = _compute.ExecuteImages("vsm_allocate_pages.slang", "AllocateVirtualShadowPages", new(1, 1, 1),
            [
                new BufferInput("PageAllocation", [4, 4, 2, 1, _frame, 0, 3, 1], ResourceBindingType.ConstantBuffer, 0),
                new BufferInput("ShadowViews", _view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
                new BufferInput("RequestedPageIndices", [page ?? 0u], ResourceBindingType.BufferSrv, 4),
                new BufferInput("RequestedPageCount", [requestCount], ResourceBindingType.BufferSrv, 4),
                new BufferInput("RequestedPageFlags", [1, 1, 1, 1], ResourceBindingType.BufferSrv, 4),
                new BufferInput("ShadowPageTable", _table, ResourceBindingType.BufferUav, 4),
                new BufferInput("PhysicalPageOwners", _pages, ResourceBindingType.BufferUav, 4),
                new BufferInput("PageMetadata", _metadata, ResourceBindingType.BufferUav, 32),
                new BufferInput("ShadowPageLists", _lists, ResourceBindingType.BufferUav, 4),
                new BufferInput("ActivePhysicalPages", new uint[8], ResourceBindingType.BufferUav, 16),
                new BufferInput("ActivePhysicalPageCount", [0], ResourceBindingType.BufferUav, 4),
                new BufferInput("UncachedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
                new BufferInput("RasterTargetPageHierarchy", new uint[6], ResourceBindingType.BufferUav, 8),
                new BufferInput("ShadowAllocationFailures", new uint[3], ResourceBindingType.BufferUav, 4),
            ], ShadowTestImages.ReceiverHierarchy(4, 6));
            uint[][] completed = _compute.Execute("vsm_page_depth_complete.slang", "CompleteVirtualShadowPageDepth", new(1, 1, 1),
            [
                new BufferInput("ActivePhysicalPages", allocated[4], ResourceBindingType.BufferSrv, 16),
                new BufferInput("CompletePageCount", allocated[5], ResourceBindingType.BufferSrv, 4),
                new BufferInput("CompletedShadowPageTable", allocated[0], ResourceBindingType.BufferUav, 4),
                new BufferInput("GeometryWorkStatus", [0], ResourceBindingType.BufferSrv, 0),
                new BufferInput("CompletedPageMetadata", allocated[2], ResourceBindingType.BufferUav, 32),
            ]);
            _table = completed[0];
            _metadata = completed[1];
            _pages = allocated[1];
            _lists = allocated[3];
            BuildPool(3);

            void BuildPool(uint phase)
            {
                uint[][] values = _compute.Execute("vsm_page_pool.slang", "BuildVirtualShadowPagePool", new(1, 1, 1),
                [
                    new BufferInput("PagePool", [2, 4, _frame, phase, 4, 3, 1, 1], ResourceBindingType.ConstantBuffer, 0),
                    new BufferInput("ShadowViews", _view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
                    new BufferInput("ShadowPageTable", _table, ResourceBindingType.BufferUav, 4),
                    new BufferInput("RequestedPageFlags", requests, ResourceBindingType.BufferSrv, 4),
                    new BufferInput("RequestedPageCount", [requestCount], ResourceBindingType.BufferSrv, 4),
                    new BufferInput("PageRequestDispatchArgs", new uint[4], ResourceBindingType.BufferUav, 0),
                    new BufferInput("PhysicalPageOwners", _pages, ResourceBindingType.BufferUav, 4),
                    new BufferInput("PageMetadata", _metadata, ResourceBindingType.BufferUav, 32),
                    new BufferInput("ShadowPageLists", _lists, ResourceBindingType.BufferUav, 4),
                    new BufferInput("AllocatedPageHierarchy", new uint[3], ResourceBindingType.BufferUav, 4),
                    new BufferInput("AllocatedPageRectBounds", [uint.MaxValue, uint.MaxValue, 0, 0], ResourceBindingType.BufferUav, 16),
                ]);
                _table = values[0]; _pages = values[2]; _metadata = values[3]; _lists = values[4];
            }
        }

        internal float[] Observe()
        {
            uint[][] result = _compute.ExecuteSource(Sampling, "Observe", new(1, 1, 1),
                [(_view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
                 (_table, ResourceBindingType.BufferSrv, 4), (new uint[6], ResourceBindingType.BufferUav, 8)],
                new SampledImage(16, 16, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.75f), 256).ToArray(), 2, ViewDimension: TextureViewDimension.Texture2DArray));
            return result[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        }

        private static uint[] View()
        {
            VirtualShadowView view = new()
            {
                LightViewProjection = Matrix4x4.Identity,
                VirtualResolution = 16, PageSize = 8, AtlasSize = 16,
                PhysicalPagesPerRow = 2, MaxPhysicalPages = 2,
                CacheGeneration = 7, MipLevelCount = 1,
                LightKind = VirtualShadowLightKind.Spot, LightRange = 10,
                WorldUnitsPerCentimeter = 1,
            };
            return MemoryMarshal.Cast<VirtualShadowView, uint>(MemoryMarshal.CreateReadOnlySpan(ref view, 1)).ToArray();
        }
    }

    private const string Sampling = """
        #define VSM_WHOLE_PAGE_SAMPLING 1
        #include "vsm_sampling.slang"
        StructuredBuffer<VirtualShadowMapParameters> Views;
        StructuredBuffer<uint> Pages;
        Texture2DArray<uint> Atlas;
        RWStructuredBuffer<float2> Observations;
        [shader("compute")]
        [numthreads(4,1,1)]
        void Observe(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= 3u) return;
            float2 uv = (float2(id.x & 1u, id.x >> 1u) + .5) / 2.0;
            float3 position = float3(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, .5);
            float visibility;
            bool valid = TrySampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, .001,
                position, position + float3(.001,0,0), position + float3(0,.001,0),
                1u,1u,0.0,0.0,float3(0,0,1),visibility);
            Observations[id.x] = float2(valid ? 1.0 : 0.0, visibility);
        }
        """;
}
