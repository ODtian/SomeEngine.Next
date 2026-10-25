using SomeEngine.Graphics;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowTimeBudgetRenderingTests
{
    [Fact]
    public async Task PositiveTimeBudgetRendersAFrameWithoutThrowing()
    {
        var settings = new VirtualShadowMapSettings
        {
            Enabled = true,
            VirtualResolution = 4096,
            AtlasSize = 2048,
            MaxShadowLights = 1,
            DirectionalClipmapLevels = 1,
            DynamicResolutionEnabled = false,
            WorkTimeBudgetMilliseconds = 1f,
        };

        await using var scene = await FrameStageScene.CreateAsync(
            "D3D12", shadows: true, shadowSettings: settings,
            optionalFeatures: DeviceFeatures.CalibratedTimestamps);

        RasterImage image = await scene.ReadNextRasterImageAsync();

        Assert.Equal(64 * 64 * 8, image.Color.Length);
        Assert.Equal(64 * 64 * 4, image.Depth.Length);
    }
}
