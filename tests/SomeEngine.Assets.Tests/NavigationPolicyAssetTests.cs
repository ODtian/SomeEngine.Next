using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationPolicyAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationPolicy-" + Guid.NewGuid().ToString("N"));
    public NavigationPolicyAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReopenedPolicyProvidesItsTraversalCostsAfterAssetClosure()
    {
        var policy = new NavigationPolicy([new(3, 2.5f), new(7, float.PositiveInfinity)],
            [new(12, 4f)], defaultAreaCost: 1.25f, allowedCapabilities: 5);
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
    }
}
