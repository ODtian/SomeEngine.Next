namespace SomeEngine.Assets.Schema;

public sealed partial class ClusteredLightGridAlgorithm
{
    internal static async ValueTask<ClusteredLightGridAlgorithm> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<ClusteredLightGridAlgorithm> document =
            await context.OpenAsync<ClusteredLightGridAlgorithm>().ConfigureAwait(false);
        ClusteredLightGridAlgorithm asset = document.Root;
        _ = await context.LoadDependencyAsync(
            new AssetId<Shader>(asset.GetShaderDependency(string.Empty))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return asset;
    }
}
