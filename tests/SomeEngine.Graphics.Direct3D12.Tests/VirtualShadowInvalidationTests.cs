using System.Numerics;
using System.Runtime.InteropServices;
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
        Assert.Equal(intersects, Enumerable.Range(0, 16).Any(page => pages[page * 4 + 3] == 0));
        for (int page = 0; page < 16; page++)
        {
            Assert.Equal((uint)page + 1, pages[page * 4]);
            Assert.Equal((uint)page % 4, pages[page * 4 + 1]);
            Assert.Equal((uint)page / 4, pages[page * 4 + 2]);
        }
    }

    [Fact]
    public void LeavingTheLightStillInvalidatesPagesCoveredByPreviousBounds()
    {
        uint[] pages = Invalidate(new(-0.5f, -0.5f, 2), new(0.5f, 0.5f, 3),
            new(-0.5f, -0.5f, -3), new(0.5f, 0.5f, -2));
        Assert.Contains(Enumerable.Range(0, 16), page => pages[page * 4 + 3] == 0);
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
        Assert.Equal(intersects, Enumerable.Range(0, 16).Any(page => pages[page * 4 + 3] == 0));
    }

    [Fact]
    public void LeavingLocalLightRangeStillInvalidatesPreviousCoverage()
    {
        uint[] pages = Invalidate(new(2.2f, -4, -2.9f), new(2.9f, 4, -2.2f),
            new(1.5f, -4, -2.9f), new(2.9f, 4, -1.5f), lightRange: 3);
        Assert.Contains(Enumerable.Range(0, 16), page => pages[page * 4 + 3] == 0);
    }

    private uint[] Invalidate(Vector3 min, Vector3 max, Vector3 previousMin, Vector3 previousMax,
        bool allocated = true, float lightRange = 0)
    {
        // Public GPU view data: perspective projection, four pages per axis, one mip.
        uint[] view = new uint[SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes / sizeof(uint)];
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, lightRange > 0 ? lightRange : 10);
        MemoryMarshal.Cast<Matrix4x4, uint>(new[] { projection }.AsSpan()).CopyTo(view);
        view[16] = 512; view[17] = 128; view[18] = 512; view[19] = 4;
        view[20] = 16; view[24] = 7; view[34] = 1; view[37] = 1;
        view[43] = BitConverter.SingleToUInt32Bits(lightRange);
        Vector4[] bounds = [new(min, 0), new(max, 0), new(previousMin, 0), new(previousMax, 0)];
        uint[] table = Enumerable.Range(0, 16)
            .SelectMany(page => new[] { (uint)page + 1, (uint)page % 4, (uint)page / 4, 7u }).ToArray();
        if (!allocated) Array.Clear(table);
        uint[] hierarchy = allocated ? [15, 15, 15, 15, 3, 3, 1] : new uint[7];
        uint[] rect = allocated ? [0, 0, 3, 3] : [uint.MaxValue, uint.MaxValue, 0, 0];
        // Requests are deliberately empty: cached pages must be invalidated even when unused this frame.
        return new D3D12TestCompute(shaderCompiler).Execute("vsm_caster_invalidation.slang", "InvalidateMovedVirtualShadowCasters", new(1, 1, 1),
            [([1, 1, 16, 0, 3, 7, 1, 0], ResourceBindingType.ConstantBuffer, 0),
             (view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes),
             (MemoryMarshal.Cast<Vector4, uint>(bounds.AsSpan()).ToArray(), ResourceBindingType.BufferSrv, 16),
             ([0], ResourceBindingType.BufferSrv, 4),
             (new uint[16], ResourceBindingType.BufferSrv, 4),
             (new uint[16], ResourceBindingType.BufferSrv, 4),
             (new uint[64], ResourceBindingType.BufferSrv, 16),
             (hierarchy, ResourceBindingType.BufferSrv, 4),
             (rect, ResourceBindingType.BufferSrv, 16),
             (table, ResourceBindingType.BufferUav, 16)])[0];
    }
}
