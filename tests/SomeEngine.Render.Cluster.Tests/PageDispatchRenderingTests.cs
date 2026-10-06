using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class PageDispatchRenderingTests
{
    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public async Task MoreThanOneDispatchRowOfDirtyPagesCompletesWithoutLosingReceivers(string backend)
    {
        var settings = new VirtualShadowMapSettings { VirtualResolution = 2048, AtlasSize = 2048, PageSize = 8,
            MaxShadowLights = 1, DirectionalClipmapLevels = 1, DynamicResolutionEnabled = false,
            LocalResolutionLodBias = -8, MaxFilterRadiusTexels = 32, BlockerSearchRadiusTexels = 0, NormalBias = .001f };
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true };
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync(backend, true, options, plane, settings);
        scene.SetView(new RenderView(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 64, 64));
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -2), Quaternion.Identity) { Stretch = new(4, 4, 1) });
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6), Quaternion.Identity));
        scene.SetPointLight(new PointLight(Vector3.Zero, 30, Vector3.One, 30));
        var first = await scene.ReadShadowQualityAsync();
        Assert.True(first.Quality.RequestedPages > 65535);
        Assert.True(first.Quality.DirtyPages > 65535);
        Assert.Equal(ClusterWorkOverflow.None, first.Quality.WorkOverflow);
        Assert.Equal(first.Quality.DirtyPages, (uint)first.Quality.Views.Sum(view =>
            view.Mips.Sum(mip => (long)mip.WholeDepthPages + mip.ReceiverDepthPages)));
        var reused = await scene.ReadShadowQualityAsync();
        RasterImage.AssertEqual(first.Image, reused.Image);
    }

    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public async Task LargeAcceptedPagesCompleteTheirFullTileGrid(string backend)
    {
        var settings = new VirtualShadowMapSettings { VirtualResolution = 16384, AtlasSize = 4096, PageSize = 4096,
            MaxShadowLights = 1, DirectionalClipmapLevels = 1, DynamicResolutionEnabled = false };
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true };
        var lamp = new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
            MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30);
        await using var scene = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: settings);
        scene.UseSpotLight(lamp);
        var first = await scene.ReadShadowQualityAsync();
        Assert.True(first.Quality.DirtyPages > 0);
        Assert.Equal(ClusterWorkOverflow.None, first.Quality.WorkOverflow);
        scene.SetMeshTransform(1, new(new Vector3(20, 0, -6), Quaternion.Identity, .6f));
        var cleared = await scene.ReadShadowQualityAsync();
        await using var fresh = await FrameStageScene.CreateAsync(backend, true, options, shadowSettings: settings);
        fresh.UseSpotLight(lamp);
        fresh.SetMeshTransform(1, new(new Vector3(20, 0, -6), Quaternion.Identity, .6f));
        var expected = await fresh.ReadShadowQualityAsync();
        Assert.NotEqual(first.Image.Color, expected.Image.Color);
        RasterImage.AssertEqual(expected.Image, cleared.Image);
    }
}
