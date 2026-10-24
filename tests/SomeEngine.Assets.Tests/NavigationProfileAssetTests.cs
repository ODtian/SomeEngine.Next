using System.Numerics;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationProfileAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationProfile-" + Guid.NewGuid().ToString("N"));
    public NavigationProfileAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReopenedProfileKeepsItsShapeAndMovementLimitsAfterAssetClosure()
    {
        var profile = new NavigationProfile(
            [new(-0.3f, -0.6f), new(0.3f, -0.6f), new(0.3f, 0.6f), new(-0.3f, 0.6f)],
            height: 1.8f, maximumSlopeRadians: 0.6f, maximumStepHeight: 0.25f,
            maximumSpeed: 2.5f, maximumAcceleration: 6f, maximumAngularSpeed: 1.4f, capabilities: 5);
        string path = Path.Combine(_directory, "courier.navigation-profile.asset");
        using (var asset = new NavigationProfileAsset())
        using (var block = new NavigationProfileBlock())
        {
            block.SetProfile(profile);
            asset.SetBlock(block);
            await asset.SaveAsync(path);
        }
        NavigationProfile restored;
        using (var asset = await NavigationProfileAsset.OpenAsync(path))
            restored = await (await asset.GetBlockAsync<NavigationProfileBlock>()).CreateAsync();

        Assert.Equal(new Vector2[] { new(-0.3f, -0.6f), new(0.3f, -0.6f), new(0.3f, 0.6f), new(-0.3f, 0.6f) },
            restored.Footprint.ToArray());
        Assert.Equal((1.8f, 0.6f, 0.25f, 2.5f, 6f, 1.4f, 5UL),
            (restored.Height, restored.MaximumSlopeRadians, restored.MaximumStepHeight, restored.MaximumSpeed,
                restored.MaximumAcceleration, restored.MaximumAngularSpeed, restored.Capabilities));
    }
}
