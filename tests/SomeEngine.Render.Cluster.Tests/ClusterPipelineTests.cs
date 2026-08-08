using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPipelineTests
{
    [Fact]
    public void ViewUniformsExactlyMatchTheClusterShaderAbi()
    {
        Assert.Equal(352, Marshal.SizeOf<ClusterViewUniforms>());
        AssertOffset(nameof(ClusterViewUniforms.ViewProj), 0);
        AssertOffset(nameof(ClusterViewUniforms.CameraPos), 64);
        AssertOffset(nameof(ClusterViewUniforms.LodThreshold), 76);
        AssertOffset(nameof(ClusterViewUniforms.LodScale), 80);
        AssertOffset(nameof(ClusterViewUniforms.MaxCandidates), 84);
        AssertOffset(nameof(ClusterViewUniforms.MaxTraversalDepth), 88);
        AssertOffset(nameof(ClusterViewUniforms.InstanceCount), 92);
        AssertOffset(nameof(ClusterViewUniforms.MaxPageFaults), 96);
        AssertOffset(nameof(ClusterViewUniforms.ForceHardwareRaster), 100);
        AssertOffset(nameof(ClusterViewUniforms.SoftwareRasterAreaThreshold), 104);
        AssertOffset(nameof(ClusterViewUniforms.Pad2), 108);
        AssertOffset(nameof(ClusterViewUniforms.PrevViewProj), 112);
        AssertOffset(nameof(ClusterViewUniforms.HasPrevHistory), 176);
        AssertOffset(nameof(ClusterViewUniforms.HiZMipCount), 180);
        AssertOffset(nameof(ClusterViewUniforms.HiZInvSize), 184);
        AssertOffset(nameof(ClusterViewUniforms.View), 192);
        AssertOffset(nameof(ClusterViewUniforms.P00), 256);
        AssertOffset(nameof(ClusterViewUniforms.P11), 260);
        AssertOffset(nameof(ClusterViewUniforms.ScreenWidth), 264);
        AssertOffset(nameof(ClusterViewUniforms.ScreenHeight), 268);
        AssertOffset(nameof(ClusterViewUniforms.PrevView), 272);
        AssertOffset(nameof(ClusterViewUniforms.PrevP00), 336);
        AssertOffset(nameof(ClusterViewUniforms.PrevP11), 340);
        AssertOffset(nameof(ClusterViewUniforms.Pad3), 344);

        Matrix4x4 view = Matrix4x4.CreateTranslation(-1.0f, -2.0f, -3.0f);
        Matrix4x4 projection = Matrix4x4.Identity;
        var source = new RenderView(view, projection, 800, 600);
        var options = new ClusterPipelineOptions
        {
            MaxCandidates = 256,
            MaxTraversalDepth = 32,
            LodThreshold = 0.75f,
            ForceHardwareRaster = true,
            SoftwareRasterAreaThreshold = 1_024.0f,
        };

        ClusterViewUniforms uniforms = ClusterViewUniforms.Create(
            in source,
            options,
            instanceCount: 7,
            pageFaultCapacity: 11);

        Assert.Equal(view, uniforms.ViewProj);
        Assert.Equal(new Vector3(1.0f, 2.0f, 3.0f), uniforms.CameraPos);
        Assert.Equal(0.75f, uniforms.LodThreshold);
        Assert.Equal(300.0f, uniforms.LodScale);
        Assert.Equal(256u, uniforms.MaxCandidates);
        Assert.Equal(32u, uniforms.MaxTraversalDepth);
        Assert.Equal(7u, uniforms.InstanceCount);
        Assert.Equal(11u, uniforms.MaxPageFaults);
        Assert.Equal(1u, uniforms.ForceHardwareRaster);
        Assert.Equal(1_024.0f, uniforms.SoftwareRasterAreaThreshold);
        Assert.Equal(view, uniforms.PrevViewProj);
        Assert.Equal(0u, uniforms.HasPrevHistory);
        Assert.Equal(0u, uniforms.HiZMipCount);
        Assert.Equal(view, uniforms.View);
        Assert.Equal(view, uniforms.PrevView);
        Assert.Equal(1.0f, uniforms.P00);
        Assert.Equal(1.0f, uniforms.P11);
        Assert.Equal(800u, uniforms.ScreenWidth);
        Assert.Equal(600u, uniforms.ScreenHeight);
    }

    [Fact]
    public void RasterDeformBinningUniformsExactlyMatchTheSharedGpuContract()
    {
        Assert.Equal(32, Marshal.SizeOf<ClusterRasterDeformBinningUniforms>());
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.RasterMaxBins), 0);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.DeformMaxBins), 4);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.SlotCapacity), 8);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.RasterBinFieldIndex), 12);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.DeformBinFieldIndex), 16);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.MaxVisibleClusters), 20);
        AssertOffset<ClusterRasterDeformBinningUniforms>(
            nameof(ClusterRasterDeformBinningUniforms.ResetCacheAllocationState), 24);
    }

    [Fact]
    public void DeformCacheUniformsContainOnlyKernelInputs()
    {
        Assert.Equal(16, Marshal.SizeOf<ClusterDeformUniforms>());
        AssertOffset<ClusterDeformUniforms>(
            nameof(ClusterDeformUniforms.MaxDeformCacheBytes), 0);
        AssertOffset<ClusterDeformUniforms>(
            nameof(ClusterDeformUniforms.MaxClusterVertices), 4);
        AssertOffset<ClusterDeformUniforms>(
            nameof(ClusterDeformUniforms.CurrentBin), 8);
    }

    [Fact]
    public void VisibilityCapacityPolicyRejectsValuesTheShadersCannotRepresent()
    {
        var valid = new ClusterPipelineOptions
        {
            MaxCandidates = 64,
            MaxTraversalDepth = 128,
            LodThreshold = 0.0f,
        };
        valid.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxCandidates = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxCandidates = 4_194_241 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxTraversalDepth = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxTraversalDepth = 129 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = -1.0f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = float.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { SoftwareRasterAreaThreshold = -1.0f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { SoftwareRasterAreaThreshold = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with
            {
                DeformCacheBytes = ClusterPipelineOptions.MaximumDeformCacheBytes + 1,
            }).Validate());
    }

    private static void AssertOffset(string field, int expected) =>
        Assert.Equal(expected, Marshal.OffsetOf<ClusterViewUniforms>(field).ToInt32());

    private static void AssertOffset<T>(string field, int expected) where T : struct =>
        Assert.Equal(expected, Marshal.OffsetOf<T>(field).ToInt32());

}
