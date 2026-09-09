using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPipelineTests
{
    [Fact]
    public void ViewUniformsExactlyMatchTheClusterShaderAbi()
    {
        Assert.Equal(432, Marshal.SizeOf<ClusterViewUniforms>());
        AssertOffset(nameof(ClusterViewUniforms.ViewProj), 0);
        AssertOffset(nameof(ClusterViewUniforms.CameraPos), 64);
        AssertOffset(nameof(ClusterViewUniforms.LodThreshold), 76);
        AssertOffset(nameof(ClusterViewUniforms.Pad0), 80);
        AssertOffset(nameof(ClusterViewUniforms.MaxCandidates), 84);
        AssertOffset(nameof(ClusterViewUniforms.ViewPadding), 88);
        AssertOffset(nameof(ClusterViewUniforms.InstanceCount), 92);
        AssertOffset(nameof(ClusterViewUniforms.MaxPageFaults), 96);
        AssertOffset(nameof(ClusterViewUniforms.RasterMode), 100);
        AssertOffset(nameof(ClusterViewUniforms.SoftwareRasterEdgeThreshold), 104);
        AssertOffset(nameof(ClusterViewUniforms.CullingEnabled), 108);
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
        AssertOffset(nameof(ClusterViewUniforms.ClipToWorld), 352);
        AssertOffset(nameof(ClusterViewUniforms.TargetPolicy), 416);
        AssertOffset(nameof(ClusterViewUniforms.TargetViewIndex), 420);
        AssertOffset(nameof(ClusterViewUniforms.Pad5), 424);
        AssertOffset(nameof(ClusterViewUniforms.Pad6), 428);

        Matrix4x4 view = Matrix4x4.CreateTranslation(-1.0f, -2.0f, -3.0f);
        Matrix4x4 projection = Matrix4x4.Identity;
        var source = new RenderView(view, projection, 800, 600);
        var options = new ClusterPipelineOptions
        {
            MaxCandidates = 256,
            LodThreshold = 0.75f,
            ForceHardwareRaster = true,
            SoftwareRasterEdgeThreshold = 48.0f,
        };

        ClusterViewUniforms uniforms = ClusterViewUniforms.Create(
            in source,
            options,
            ClusterViewRasterMode.Hardware,
            cullingEnabled: true,
            instanceCount: 7,
            pageFaultCapacity: 11);

        Assert.Equal(view, uniforms.ViewProj);
        Assert.Equal(new Vector3(1.0f, 2.0f, 3.0f), uniforms.CameraPos);
        Assert.Equal(0.75f, uniforms.LodThreshold);
        Assert.Equal(0u, uniforms.Pad0);
        Assert.Equal(256u, uniforms.MaxCandidates);
        Assert.Equal(7u, uniforms.InstanceCount);
        Assert.Equal(11u, uniforms.MaxPageFaults);
        Assert.Equal((uint)ClusterViewRasterMode.Hardware, uniforms.RasterMode);
        Assert.Equal(48.0f, uniforms.SoftwareRasterEdgeThreshold);
        Assert.Equal(1u, uniforms.CullingEnabled);
        Assert.Equal(view, uniforms.PrevViewProj);
        Assert.Equal(0u, uniforms.HasPrevHistory);
        Assert.Equal(0u, uniforms.HiZMipCount);
        Assert.Equal(view, uniforms.View);
        Assert.Equal(view, uniforms.PrevView);
        Assert.True(Matrix4x4.Invert(view, out Matrix4x4 clipToWorld));
        Assert.Equal(clipToWorld, uniforms.ClipToWorld);
        Assert.Equal(1.0f, uniforms.P00);
        Assert.Equal(1.0f, uniforms.P11);
        Assert.Equal(800u, uniforms.ScreenWidth);
        Assert.Equal(600u, uniforms.ScreenHeight);
    }

    [Fact]
    public void RasterBinningUniformsExactlyMatchTheSharedGpuContract()
    {
        Assert.Equal(32, Marshal.SizeOf<ClusterRasterBinningUniforms>());
        AssertOffset<ClusterRasterBinningUniforms>(
            nameof(ClusterRasterBinningUniforms.RasterMaxBins), 0);
        AssertOffset<ClusterRasterBinningUniforms>(
            nameof(ClusterRasterBinningUniforms.SlotCapacity), 4);
        AssertOffset<ClusterRasterBinningUniforms>(
            nameof(ClusterRasterBinningUniforms.MaxVisibleClusters), 8);
        AssertOffset<ClusterRasterBinningUniforms>(
            nameof(ClusterRasterBinningUniforms.CollectMetrics), 12);
    }

    [Fact]
    public void TessellationUniformsExactlyMatchTheBoundedGpuContracts()
    {
        Assert.Equal(32, Marshal.SizeOf<ClusterTessellationUniforms>());
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.ScreenWidth), 0);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.CurrentBin), 8);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.MaxPatchItems), 12);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.MaxFactor), 16);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.TargetEdgePixels), 20);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.UsePreviousHiZ), 24);
        AssertOffset<ClusterTessellationUniforms>(
            nameof(ClusterTessellationUniforms.SplitLevel), 28);

        Assert.Equal(16, Marshal.SizeOf<ClusterTessellationBinUniforms>());
        AssertOffset<ClusterTessellationBinUniforms>(
            nameof(ClusterTessellationBinUniforms.BinCount), 0);
        AssertOffset<ClusterTessellationBinUniforms>(
            nameof(ClusterTessellationBinUniforms.MaxPatchItems), 4);
        AssertOffset<ClusterTessellationBinUniforms>(
            nameof(ClusterTessellationBinUniforms.SourceCounterOffset), 8);
        AssertOffset<ClusterTessellationBinUniforms>(
            nameof(ClusterTessellationBinUniforms.Pad0), 12);
    }

    [Fact]
    public void GlobalDeformCacheContractsDoNotExist()
    {
        HashSet<string> names = typeof(ClusterPipelineSystem).Assembly
            .GetTypes()
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ClusterDeformUniforms", names);
        Assert.DoesNotContain("ClusterDeformPrefixUniforms", names);
        Assert.DoesNotContain("ClusterDeformCache", names);
    }

    [Fact]
    public void VisibilityCapacityPolicyRejectsValuesTheShadersCannotRepresent()
    {
        var valid = new ClusterPipelineOptions
        {
            MaxCandidates = 64,
            LodThreshold = 0.0f,
        };
        valid.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxCandidates = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxCandidates = 4_194_241 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = -1.0f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { LodThreshold = float.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { SoftwareRasterEdgeThreshold = -1.0f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { SoftwareRasterEdgeThreshold = float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxTessellationFactor = 17 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { MaxTessellationFactor = 257 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { TessellationTargetEdgePixels = 0.5f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (valid with { TessellationTargetEdgePixels = float.NaN }).Validate());
    }

    [Fact]
    public void VisibilityModeIsExactlyImageA64BufferA64OrHardwareOnly()
    {
        var automatic = new ClusterPipelineOptions();
        Assert.Equal(
            ClusterVisibilityMode.ImageAtomic64,
            automatic.SelectVisibilityMode(Capabilities(bufferA64: true, imageA64: true)));
        Assert.Equal(
            ClusterVisibilityMode.BufferAtomic64,
            automatic.SelectVisibilityMode(Capabilities(bufferA64: true, imageA64: false)));
        Assert.Equal(
            ClusterVisibilityMode.HardwareOnly,
            automatic.SelectVisibilityMode(Capabilities(bufferA64: false, imageA64: false)));
        Assert.Equal(
            ClusterVisibilityMode.HardwareOnly,
            (automatic with { ForceHardwareRaster = true }).SelectVisibilityMode(
                Capabilities(bufferA64: true, imageA64: true)));
    }

    private static DeviceCapabilities Capabilities(bool bufferA64, bool imageA64)
    {
        FormatSupport[] formats = Enum.GetValues<Format>()
            .Select(static format => new FormatSupport(
                format,
                FormatFeatures.None,
                SampleCounts.None,
                SampleCounts.None))
            .ToArray();
        return new DeviceCapabilities(
            default,
            supportsBundles: false,
            supportsPipelineStatistics: false,
            supportsStreamOutputStatistics: false,
            supportsDepthBounds: false,
            DynamicStates.None,
            formats,
            supportsBufferInt64Atomics: bufferA64,
            supportsImageInt64Atomics: imageA64);
    }

    private static void AssertOffset(string field, int expected) =>
        Assert.Equal(expected, Marshal.OffsetOf<ClusterViewUniforms>(field).ToInt32());

    private static void AssertOffset<T>(string field, int expected) where T : struct =>
        Assert.Equal(expected, Marshal.OffsetOf<T>(field).ToInt32());

}
