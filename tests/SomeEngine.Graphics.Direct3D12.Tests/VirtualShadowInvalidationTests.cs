using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class VirtualShadowInvalidationTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(-0.5f, -0.5f, 2f, 0.5f, 0.5f, 3f, false)]
    [InlineData(10f, -0.5f, -3f, 11f, 0.5f, -2f, false)]
    [InlineData(-0.5f, -0.5f, -30f, 0.5f, 0.5f, -20f, false)]
    [InlineData(-0.1f, -0.1f, -0.5f, 0.1f, 0.1f, -0.2f, false)]
    [InlineData(-0.5f, -0.5f, -3f, 0.5f, 0.5f, -2f, true)]
    [InlineData(-0.5f, -0.5f, -2f, 0.5f, 0.5f, 2f, true)]
    public void OnlyCastersIntersectingTheLightViewInvalidateCachedPages(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool intersects)
    {
        Vector3 min = new(minX, minY, minZ), max = new(maxX, maxY, maxZ);
        uint[] pages = Invalidate(min, max, min, max);
        Assert.Equal(intersects, pages.Any(page => (page & 0x80000000u) == 0u));
        for (int page = 0; page < 16; page++)
        {
            Assert.Equal(VirtualShadowPageEntry.Encode((uint)page, 4) & 0x000fffffu,
                pages[page] & 0x000fffffu);
        }
    }

    [Fact]
    public void LeavingTheLightStillInvalidatesPagesCoveredByPreviousBounds()
    {
        uint[] pages = Invalidate(new(-0.5f, -0.5f, 2), new(0.5f, 0.5f, 3),
            new(-0.5f, -0.5f, -3), new(0.5f, 0.5f, -2));
        Assert.Contains(pages, page => (page & 0x80000000u) == 0u);
    }

    [Fact]
    public void NoAllocatedPagesRemainUnchanged()
    {
        uint[] pages = Invalidate(new(-0.5f, -0.5f, -2), new(0.5f, 0.5f, 2),
            new(-0.5f, -0.5f, -2), new(0.5f, 0.5f, 2), allocated: false);
        Assert.All(pages, value => Assert.Equal(0u, value));
    }

    [Theory]
    [InlineData(2.2f, false)]
    [InlineData(1.5f, true)]
    public void LocalLightRangeKeepsCachedPagesOutsideItsInfluence(float nearestAxis, bool intersects)
    {
        // Both boxes intersect the cube-face frustum. Only one reaches the light sphere.
        Vector3 min = new(nearestAxis, -4, -2.9f), max = new(2.9f, 4, -nearestAxis);
        uint[] pages = Invalidate(min, max, min, max, lightRange: 3);
        Assert.Equal(intersects, pages.Any(page => (page & 0x80000000u) == 0u));
    }

    [Fact]
    public void LeavingLocalLightRangeStillInvalidatesPreviousCoverage()
    {
        uint[] pages = Invalidate(new(2.2f, -4, -2.9f), new(2.9f, 4, -2.2f),
            new(1.5f, -4, -2.9f), new(2.9f, 4, -1.5f), lightRange: 3);
        Assert.Contains(pages, page => (page & 0x80000000u) == 0u);
    }

    private uint[] Invalidate(Vector3 min, Vector3 max, Vector3 previousMin, Vector3 previousMax,
        bool allocated = true, float lightRange = 0)
    {
        // Public GPU view data: perspective projection, four pages per axis, one mip.
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, lightRange > 0 ? lightRange : 10),
            VirtualResolution = 512, PageSize = 128, AtlasSize = 512, PhysicalPagesPerRow = 4,
            MaxPhysicalPages = 16, CacheGeneration = 7, MipLevelCount = 1,
            LightKind = VirtualShadowLightKind.Point, LightRange = lightRange,
        };
        Vector4[] bounds = [new(min, 0), new(max, 0), new(previousMin, 0), new(previousMax, 0)];
        uint[] table = Enumerable.Range(0, 16)
            .Select(page => VirtualShadowPageEntry.Encode((uint)page, 4)).ToArray();
        VirtualShadowPageMetadata[] metadata = Enumerable.Range(0, 16).Select(page => new VirtualShadowPageMetadata
        {
            StaticDepthGeneration = 7, WorldPageX = page % 4, WorldPageY = page / 4, Generation = 7,
        }).ToArray();
        if (!allocated) Array.Clear(table);
        uint[] hierarchy = allocated ? [15, 15, 15, 15, 3, 3, 1] : new uint[7];
        uint[] rect = allocated ? [0, 0, 3, 3] : [uint.MaxValue, uint.MaxValue, 0, 0];
        // Requests are deliberately empty: cached pages must be invalidated even when unused this frame.
        return new D3D12TestCompute(shaderCompiler).Execute("vsm_caster_invalidation.slang", "InvalidateMovedVirtualShadowCasters", new(1, 1, 1),
            [new BufferInput("Invalidation", [1, 1, 16, 0, 3, 7, 1, 0], ResourceBindingType.ConstantBuffer, 0),
             new BufferInput("ShadowViews", MemoryMarshal.Cast<VirtualShadowView, uint>(new[] { view }.AsSpan()).ToArray(),
                 ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("InstanceWorldBounds", MemoryMarshal.Cast<Vector4, uint>(bounds.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 16),
             new BufferInput("ChangedInstances", [0], ResourceBindingType.BufferSrv, 4),
             new BufferInput("RasterTargetDepthCacheClasses", [0], ResourceBindingType.BufferSrv, 4),
             new BufferInput("PreviousDepthCacheClasses", [0], ResourceBindingType.BufferSrv, 4),
             new BufferInput("LodWorldBounds", new uint[64], ResourceBindingType.BufferSrv, 16),
             new BufferInput("RequestedPageFlags", new uint[16], ResourceBindingType.BufferSrv, 4),
             new BufferInput("AllocatedPageHierarchy", hierarchy, ResourceBindingType.BufferSrv, 4),
             new BufferInput("AllocatedPageRectBounds", rect, ResourceBindingType.BufferSrv, 16),
             new BufferInput("ShadowPageTable", table, ResourceBindingType.BufferUav, 4),
             new BufferInput("PageMetadata", MemoryMarshal.Cast<VirtualShadowPageMetadata, uint>(metadata.AsSpan()).ToArray(),
                 ResourceBindingType.BufferUav, VirtualShadowPageMetadata.SizeInBytes)])[0];
    }
}
