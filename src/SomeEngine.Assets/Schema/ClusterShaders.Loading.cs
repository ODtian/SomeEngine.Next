namespace SomeEngine.Assets.Schema;

public partial class ClusterShaders
{
    internal static async ValueTask<ClusterShaders> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<ClusterShaders> document = await context
            .OpenAsync<ClusterShaders>()
            .ConfigureAwait(false);
        ClusterShaders asset = document.Root;

        foreach (AssetGuid shader in asset.GetDependencies(string.Empty))
        {
            _ = await context.LoadDependencyAsync(new AssetId<Shader>(shader)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return asset;
    }
}
