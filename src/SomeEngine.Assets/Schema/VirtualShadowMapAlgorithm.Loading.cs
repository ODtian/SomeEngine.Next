namespace SomeEngine.Assets.Schema;

public sealed partial class VirtualShadowMapAlgorithm
{
    internal static async ValueTask<VirtualShadowMapAlgorithm> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<VirtualShadowMapAlgorithm> document =
            await context.OpenAsync<VirtualShadowMapAlgorithm>().ConfigureAwait(false);
        VirtualShadowMapAlgorithm asset = document.Root;
        foreach (AssetGuid shader in asset.GetDependencies(string.Empty))
        {
            _ = await context.LoadDependencyAsync(new AssetId<Shader>(shader)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return asset;
    }
}
