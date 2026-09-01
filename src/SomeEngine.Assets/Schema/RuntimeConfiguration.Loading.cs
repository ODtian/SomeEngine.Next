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
        AssetGuid pipeline = Require(asset.ClusterPipelineGuid, nameof(ClusterPipelineGuid));
        AssetGuid uiShader = RequireRasterShaderPair(asset.UiShaders, nameof(UiShaders));
        AssetGuid presentationShader = RequireRasterShaderPair(
            asset.PresentationShaders,
            nameof(PresentationShaders));
        if (asset.WindowWidth == 0 || asset.WindowHeight == 0)
            throw new InvalidDataException("Runtime configuration requires a non-empty window.");

        _ = await context.LoadDependencyAsync(new AssetId<RenderScene>(scene)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await context.LoadDependencyAsync(new AssetId<ClusterShaders>(pipeline)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await context.LoadDependencyAsync(new AssetId<Shader>(uiShader)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await context.LoadDependencyAsync(new AssetId<Shader>(presentationShader)).ConfigureAwait(false);
        return asset;
    }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        if (WindowWidth == 0 || WindowHeight == 0)
            throw new InvalidDataException($"Runtime configuration '{path}' requires a non-empty window.");
        return
        [
            Require(SceneGuid, nameof(SceneGuid)),
            Require(ClusterPipelineGuid, nameof(ClusterPipelineGuid)),
            RequireRasterShaderPair(UiShaders, nameof(UiShaders)),
            RequireRasterShaderPair(PresentationShaders, nameof(PresentationShaders)),
        ];
    }

    private static AssetGuid RequireRasterShaderPair(
        IList<ShaderRef>? shaders,
        string field)
    {
        if (shaders is not { Count: 2 })
        {
            throw new InvalidDataException(
                $"Runtime configuration field '{field}' must contain one vertex and one pixel entry.");
        }

        ShaderRef? vertex = null;
        ShaderRef? pixel = null;
        for (int index = 0; index < shaders.Count; index++)
        {
            ShaderRef shader = shaders[index]
                ?? throw new InvalidDataException($"Runtime configuration field '{field}[{index}]' is null.");
            if (shader.Stage == ShaderStage.Vertex && vertex is null)
                vertex = shader;
            else if (shader.Stage == ShaderStage.Pixel && pixel is null)
                pixel = shader;
            else
            {
                throw new InvalidDataException(
                    $"Runtime configuration field '{field}' must contain one vertex and one pixel entry.");
            }
        }

        AssetGuid vertexGuid = ShaderRef.Require(
            vertex,
            "Runtime configuration",
            $"{field}.Vertex",
            ShaderStage.Vertex);
        AssetGuid pixelGuid = ShaderRef.Require(
            pixel,
            "Runtime configuration",
            $"{field}.Pixel",
            ShaderStage.Pixel);
        if (vertexGuid != pixelGuid)
        {
            throw new InvalidDataException(
                $"Runtime configuration field '{field}' entries must come from one shader asset.");
        }
        return vertexGuid;
    }

    private static AssetGuid Require(string? value, string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Runtime configuration field '{field}' has invalid asset GUID '{value}'.");
        return guid;
    }
}
