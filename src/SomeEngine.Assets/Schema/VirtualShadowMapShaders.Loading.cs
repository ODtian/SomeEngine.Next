namespace SomeEngine.Assets.Schema;

public sealed partial class VirtualShadowMapShaders
{
    internal static async ValueTask<VirtualShadowMapShaders> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<VirtualShadowMapShaders> document =
            await context.OpenAsync<VirtualShadowMapShaders>().ConfigureAwait(false);
        VirtualShadowMapShaders asset = document.Root;
        foreach (AssetGuid shader in asset.GetDependencies(string.Empty))
        {
            _ = await context.LoadDependencyAsync(new AssetId<Shader>(shader)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return asset;
    }
}
