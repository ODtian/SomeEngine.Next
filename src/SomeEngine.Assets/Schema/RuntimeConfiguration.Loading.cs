namespace SomeEngine.Assets.Schema;

public sealed partial class RuntimeConfiguration
{
    internal static async ValueTask<RuntimeConfiguration> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<RuntimeConfiguration> document =
            await context.OpenAsync<RuntimeConfiguration>().ConfigureAwait(false);
        RuntimeConfiguration asset = document.Root;
        AssetGuid scene = Require(asset.SceneGuid, nameof(SceneGuid));
        AssetGuid renderer = Require(asset.ClusterRendererGuid, nameof(ClusterRendererGuid));
        AssetGuid uiShader = Require(asset.UiShaderGuid, nameof(UiShaderGuid));
        if (asset.WindowWidth == 0 || asset.WindowHeight == 0)
            throw new InvalidDataException("Runtime configuration requires a non-empty window.");

        _ = await context.LoadDependencyAsync(new AssetId<RenderScene>(scene)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await context.LoadDependencyAsync(new AssetId<ClusterShaders>(renderer)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await context.LoadDependencyAsync(new AssetId<Shader>(uiShader)).ConfigureAwait(false);
        return asset;
    }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        if (WindowWidth == 0 || WindowHeight == 0)
            throw new InvalidDataException($"Runtime configuration '{path}' requires a non-empty window.");
        return
        [
            Require(SceneGuid, nameof(SceneGuid)),
            Require(ClusterRendererGuid, nameof(ClusterRendererGuid)),
            Require(UiShaderGuid, nameof(UiShaderGuid)),
        ];
    }

    private static AssetGuid Require(string? value, string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Runtime configuration field '{field}' has invalid asset GUID '{value}'.");
        return guid;
    }
}
