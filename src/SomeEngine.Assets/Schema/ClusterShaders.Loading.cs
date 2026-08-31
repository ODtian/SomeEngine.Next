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

        var shaders = new Dictionary<AssetGuid, Shader>();
        foreach (AssetGuid shader in asset.GetDependencies(string.Empty))
        {
            shaders.Add(
                shader,
                await context.LoadDependencyAsync(new AssetId<Shader>(shader)).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (ClusterShaderOperation operation in asset.Operations ?? [])
        {
            foreach (ShaderRef reference in operation.Shaders ?? [])
            {
                AssetGuid shader = ShaderRef.Require(
                    reference,
                    "Cluster render asset",
                    "Operations.Shaders");
                reference.Asset = shaders[shader];
            }
        }

        return asset;
    }
}
