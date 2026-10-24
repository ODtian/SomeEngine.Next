using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationPolicyAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationPolicy-" + Guid.NewGuid().ToString("N"));
    public NavigationPolicyAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReopenedPolicyProvidesItsTraversalCostsAndCrowdPreferencesAfterAssetClosure()
    {
        var crowd = new NavigationCrowdPolicy
        {
            Sampling = NavigationCrowdCostSampling.Scalar, FreeSpeed = 1.8f, MinimumSpeed = 0.2f,
            LowDensity = 0.3f, HighDensity = 1.2f, DistanceWeight = 0.7f, TimeWeight = 2, DiscomfortWeight = 3,
        };
        var policy = new NavigationPolicy([new(3, 2.5f), new(7, float.PositiveInfinity)],
            [new(12, 4f)], defaultAreaCost: 1.25f, allowedCapabilities: 5, crowd: crowd);
        string path = Path.Combine(_directory, "courier.navigation-policy.asset");
        using (var asset = new NavigationPolicyAsset())
        using (var block = new NavigationPolicyBlock())
        {
            block.SetPolicy(policy);
            asset.SetBlock(block);
            await asset.SaveAsync(path);
        }
        NavigationPolicy restored;
        using (var asset = await NavigationPolicyAsset.OpenAsync(path))
            restored = await (await asset.GetBlockAsync<NavigationPolicyBlock>()).CreateAsync();

        Assert.Equal((2.5f, float.PositiveInfinity, 1.25f, 4f, 5UL),
            (restored.GetAreaCost(3), restored.GetAreaCost(7), restored.GetAreaCost(9),
                restored.GetActionCost(12), restored.AllowedCapabilities));
        Assert.Equal(crowd, restored.Crowd);
    }
}
