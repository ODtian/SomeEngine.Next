using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShadowShaderTextureTests
{
    [Fact]
    public async Task SavingShadowShadersRequiresTheBlueNoiseTexture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-shadow-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var asset = new VirtualShadowMapShadersAsset();
            VirtualShadowMapShadersBlock settings = asset.GetBlock<VirtualShadowMapShadersBlock>();
            var shader = new ShaderRef { Asset = new(Guid.NewGuid()), EntryPoint = "Compute" };
            settings.ProjectLights = shader;
            settings.ResetReceiverMasks = shader;
            settings.ResetFrame = shader;
            settings.MarkPages = shader;
            settings.MarkCoarsePages = shader;
            settings.AllocatePages = shader;
            settings.ClearPages = shader;
            settings.InitializeState = shader;
            settings.InvalidateCasters = shader;
            settings.BuildPagePool = shader;
            settings.CompactViews = shader;
            settings.InitializePageHiZ = shader;
            settings.ReducePageHiZ = shader;
            settings.DynamicResolution = shader;
            settings.CompletePageDepth = shader;

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await asset.SaveAsync(Path.Combine(directory, "shadow.virtualshadow.asset")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
