using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowWindowRenderingTests
{
    [Fact]
    public async Task FourPagesAcrossTheGridBoundaryUseOneHardwareWindow()
    {
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 2048, AtlasSize = 1024, PageSize = 128,
            MaxShadowLights = 1, DirectionalClipmapLevels = 1,
            DynamicResolutionEnabled = false, LocalResolutionLodBias = -8,
            LocalReceiverMask = false,
        };
        var options = new ClusterPipelineOptions
        {
            MaxCandidates = 1024, EnableFrameMetricsReadback = true, ForceHardwareRaster = true,
        };
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        scene.SetView(new RenderView(Matrix4x4.CreateTranslation(0, -6.5f, 0),
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64));
        // Both planes project to light pages x=5..8, y=1. One is the visible
        // receiver; the closer plane casts its shadow from outside the camera.
        scene.SetMeshTransform(0, new(new Vector3(-1, 6.5f, -8), Quaternion.Identity)
            { Stretch = new(2, .5f, .1f) });
        scene.SetMeshTransform(1, new(new Vector3(-.5f, 3.25f, -4), Quaternion.Identity)
            { Stretch = new(1, .25f, .1f) });
        scene.UseSpotLight(new SpotLight(Vector3.Zero, 100, -Vector3.UnitZ,
            MathF.Cos(MathF.PI / 6), MathF.Cos(MathF.PI / 4), Vector3.One, 30,
            ShadowSettings: new(0, LightMobility.Static)));
        RasterImage image = await scene.ReadNextRasterImageAsync();
        // Each plane emits one fine window and one coarsest-page window.
        Assert.True(image.Metrics.Work.Complete);
        Assert.Equal(4ul, image.Metrics.Work.RequiredHardwareWindows);
        Assert.Equal(4ul, image.Metrics.Work.AcceptedHardwareWindows);
    }
}
