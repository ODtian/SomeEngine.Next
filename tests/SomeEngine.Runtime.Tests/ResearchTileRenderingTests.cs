using SomeEngine.Transparency.Research;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ResearchTileRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task TileVisibilityMatchesCompleteCoverageCompositing()
    {
        SceneImage? reference = null, tiled = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 3,
            [new(2, scene => scene.CaptureImage(image => reference = image))])
        {
            CreateColorFeature = rendering => new TransparencyColorFeature(rendering,
                ResearchGeometry.CreateGlass(3, rendering.Width, rendering.Height), 0),
        }, "--static-scene", "--no-taa", "--no-vsm");
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 3,
            [new(2, scene => scene.CaptureImage(image => tiled = image))])
        {
            CreateColorFeature = rendering => new TileVisibilityColorFeature(rendering,
                ResearchGeometry.CreateGlass(3, rendering.Width, rendering.Height), TransparencyComparison.TileSoftware, 0),
        }, "--static-scene", "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(reference!, tiled!, tolerance: 2);
    }

    [Fact]
    public async Task TileBlendWordsMatchForwardCompositing()
    {
        SceneImage? reference = null, tiled = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 3,
            [new(2, scene => scene.CaptureImage(image => reference = image))])
        {
            CreateColorFeature = rendering => new BlendEffectColorFeature(rendering,
                new BlendEffectData(BlendEffect.Xray, 17, rendering.Width, rendering.Height), BlendMethod.Forward, 64),
        }, "--static-scene", "--no-taa", "--no-vsm");
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 3,
            [new(2, scene => scene.CaptureImage(image => tiled = image))])
        {
            CreateColorFeature = rendering => new BlendEffectColorFeature(rendering,
                new BlendEffectData(BlendEffect.Xray, 17, rendering.Width, rendering.Height), BlendMethod.Tile, 64),
        }, "--static-scene", "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(reference!, tiled!, tolerance: 2);
    }
}