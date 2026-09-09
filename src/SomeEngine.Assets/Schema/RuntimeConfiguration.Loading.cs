namespace SomeEngine.Assets.Schema;

internal sealed partial class ApplicationData
{
    internal IReadOnlyList<Guid> GetDependencies(string path)
    {
        if (WindowWidth == 0 || WindowHeight == 0)
            throw new InvalidDataException($"Runtime configuration '{path}' requires a non-empty window.");
        return
        [
            Require(SceneGuid, nameof(SceneGuid)),
            Require(ClusterPipelineGuid, nameof(ClusterPipelineGuid)),
            Require(VirtualShadowMapGuid, nameof(VirtualShadowMapGuid)),
            RequireRasterShaderPair(UiShaders, nameof(UiShaders)),
            RequireRasterShaderPair(PresentationShaders, nameof(PresentationShaders)),
            RequireComputeShader(LightingShader, nameof(LightingShader)),
            RequireRasterShaderPair(TemporalShaders, nameof(TemporalShaders)),
            RequireRasterShaderPair(TranslucencyShaders, nameof(TranslucencyShaders)),
            RequireRasterShaderPair(DebugShaders, nameof(DebugShaders)),
        ];
    }

    private static Guid RequireComputeShader(ShaderRef? shader, string field) =>
        ShaderRef.Require(
            shader,
            "Runtime configuration",
            field,
            ShaderStage.Compute);

    private static Guid RequireRasterShaderPair(
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

        Guid vertexGuid = ShaderRef.Require(
            vertex,
            "Runtime configuration",
            $"{field}.Vertex",
            ShaderStage.Vertex);
        Guid pixelGuid = ShaderRef.Require(
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

    private static Guid Require(string? value, string field)
    {
        if (!global::System.Guid.TryParse(value, out Guid guid) || (guid == System.Guid.Empty))
            throw new InvalidDataException($"Runtime configuration field '{field}' has invalid asset GUID '{value}'.");
        return guid;
    }
}
